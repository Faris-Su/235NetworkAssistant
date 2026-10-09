using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;
using RuijieNetworkAssistant.Services.Snmp;
using RuijieNetworkAssistant.ViewModels;
using RuijieNetworkAssistant.Views;

namespace RuijieNetworkAssistant;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 兜底要**最先**装好：OnStartup 是 async void，装晚了的话初始化阶段（读设置/建服务/建 VM）
        // 任何一处抛异常都是未处理异常 → 进程直接消失，用户看到的只是"点了没反应"。
        DispatcherUnhandledException += (_, args) =>
        {
            AppServices.Log.Error("界面线程未处理异常（已兜住，程序继续运行）", args.Exception);
            _mainViewModel?.ReportStatus($"出现异常：{args.Exception.Message}（已记录日志）");
            args.Handled = true;
        };

        var options = StartupOptions.Parse(e.Args);
        var startupWatch = System.Diagnostics.Stopwatch.StartNew();

        // 命令的可用状态通知可能来自后台线程（读取线程/登录线程），统一回到 UI 线程触发，
        // 否则 WPF 按钮更新 IsEnabled 时会抛跨线程异常。
        // 命令的可用状态通知可能来自后台线程（读取线程/登录线程/线程池），统一回到 UI 线程触发，
        // 否则 WPF 按钮更新 IsEnabled 时会抛跨线程异常（“连接失败：The calling thread cannot access…”）。
        UiThread.Marshal = action =>
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _ = dispatcher.InvokeAsync(action);
            }
        };

        try
        {
            // 启动不等待网络：这里只做本地文件读写。
            await AppServices.InitializeAsync(options.ResourceDatabasePath).ConfigureAwait(true);
            AppServices.Clipboard = new WpfClipboardService();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：{ex.Message}", AppInfo.ChineseName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        startupWatch.Stop();

        // 诊断设置必须在 viewModel.Start() 之前生效：
        // 概览页在 Start() 时就会初始化（并触发一次 SNMP 读取）。
        // 诊断用：临时套用界面字体档位，验证“大字体 + 小窗口”下的布局（不写回设置文件）。
        if (options.UiFontScale is { Length: > 0 } fontScale)
        {
            AppServices.Settings.UiFontScale = fontScale;
        }

        // 诊断用：强制行模式（默认是终端模式）
        if (options.UiLineMode)
        {
            AppServices.Settings.CliTerminalMode = false;
        }

        // 诊断用：把"退格发 DEL(0x7F)"打开（默认 0x08）；用于验收这个设置项的两条路径
        if (options.UiBackspaceSendsDel)
        {
            AppServices.Settings.CliBackspaceSendsDel = true;
        }

        // 诊断用：覆盖串口 DTR / RTS（验收"取消勾选拉 DTR"是否也能连上）
        if (options.UiSerialDtr is { } dtr)
        {
            AppServices.Settings.Serial.DtrEnable = dtr;
        }

        if (options.UiSerialRts is { } rts)
        {
            AppServices.Settings.Serial.RtsEnable = rts;
        }

        // 诊断用：启用 SNMP 指向本地模拟代理（host:port:community）
        if (options.UiSnmp is { Length: > 0 } snmpTarget)
        {
            var parts = snmpTarget.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppServices.Snmp.Enabled = true;
            AppServices.Snmp.Port = parts.Length > 1 && int.TryParse(parts[1], out var snmpPort) ? snmpPort : 161;
            AppServices.Snmp.Community = parts.Length > 2 ? parts[2] : "example-community";
            // 默认 1200ms 是给"本机模拟代理"用的；过隧道（UU/SnmpTunnel）时要能调大，
            // 否则一来一回 2~3 秒的链路会被判成超时（--ui-snmp-timeout / --ui-snmp-retries）
            AppServices.Snmp.TimeoutMs = options.UiSnmpTimeoutMs is { } uiTimeout
                ? Math.Clamp(uiTimeout, 300, 30000)
                : 1200;
            if (options.UiSnmpRetries is { } uiRetries)
            {
                AppServices.Snmp.Retries = Math.Clamp(uiRetries, 0, 5);
            }
            AppServices.Snmp.LastHost = parts[0];
        }

        var viewModel = new MainViewModel(AppServices.Connections);
        _mainViewModel = viewModel;
        viewModel.Start();

        // 兜底一：命令里的异常（ICommand.Execute 是 async void，不兜住就会崩进程）
        AsyncRelayCommand.OnError = ex =>
        {
            _mainViewModel?.ReportStatus($"操作失败：{ex.Message}");
            _mainViewModel?.AddRecentOperation("操作失败", ex.Message, succeeded: false);
        };

        if (options.PerfMode)
        {
            await RunPerformanceReportAsync(viewModel, options, startupWatch.ElapsedMilliseconds).ConfigureAwait(true);
            Shutdown(0);
            return;
        }

        if (options.IsDiagnosticsMode)
        {
            await RunDiagnosticsAsync(viewModel, options).ConfigureAwait(true);
            Shutdown(0);
            return;
        }

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // 空闲预热：首屏出来之后，用一个 BelowNormal 后台线程把"第一次用到才付钱"的纯 CPU 路径走一遍
        // （资源库搜索串预计算、解析器正则编译、Excel 依赖类型初始化）。见 IdleWarmup 的注释。
        IdleWarmup.Start(AppServices.Log);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Stop();

        if (AppServices.Connections is not null)
        {
            try
            {
                AppServices.Connections.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // 退出阶段忽略释放异常。
            }
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 性能测量（Phase 5）：启动耗时、内存占用、空闲 CPU 消耗、各页面首次打开耗时。
    /// 结果写入报告文件，便于在低配电脑上对比（不属于正常运行路径）。
    /// </summary>
    private async Task RunPerformanceReportAsync(MainViewModel viewModel, StartupOptions options, long startupMs)
    {
        if (options.PerfWarm)
        {
            // 模拟"启动后空闲一会儿再点页面"：先同步跑一遍空闲预热，再量各页首次打开耗时。
            IdleWarmup.RunNow(AppServices.Log);
        }

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var report = new List<string>
        {
            $"{AppInfo.ChineseName}（{AppInfo.EnglishName}）性能测量报告（{DateTime.Now:yyyy-MM-dd HH:mm:ss}）",
            $"操作系统：{Environment.OSVersion}｜64 位进程：{Environment.Is64BitProcess}｜逻辑核心：{Environment.ProcessorCount}",
            $"资源库：交换机 {AppServices.ResourceRepository.Database.SwitchCount} / " +
            $"VLAN {AppServices.ResourceRepository.Database.VlanCount} / 场所 {AppServices.ResourceRepository.Database.LocationCount}",
            $"启动耗时（服务初始化 + 资源库加载）：{startupMs} ms",
        };

        var window = new MainWindow { DataContext = viewModel };
        var content = (FrameworkElement)window.Content;

        var renderWatch = System.Diagnostics.Stopwatch.StartNew();
        content.Measure(new Size(1280, 800));
        content.Arrange(new Rect(0, 0, 1280, 800));
        content.UpdateLayout();
        renderWatch.Stop();
        report.Add($"首次界面布局（1280x800，未 Show）：{renderWatch.ElapsedMilliseconds} ms");
        report.Add(DescribeMemory(process, "启动后内存"));

        // 空闲 CPU：等待 N 秒不做任何操作（应只有状态栏时钟在跑，验证没有后台轮询）
        var cpuBefore = process.TotalProcessorTime;
        await Task.Delay(TimeSpan.FromSeconds(options.PerfSeconds)).ConfigureAwait(true);
        var idleCpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        report.Add(
            $"空闲 {options.PerfSeconds} 秒 CPU 时间：{idleCpuMs:F0} ms" +
            $"（占单核 {idleCpuMs / (options.PerfSeconds * 1000.0) * 100:F2}%）");

        // 页面切换：按需创建 ViewModel + 首次布局
        foreach (var item in viewModel.NavigationItems)
        {
            var pageWatch = System.Diagnostics.Stopwatch.StartNew();
            viewModel.NavigateTo(item.Key);
            await Task.Delay(30).ConfigureAwait(true);
            content.UpdateLayout();
            pageWatch.Stop();
            report.Add($"  页面“{item.Title}”首次打开（含按需初始化）：{pageWatch.ElapsedMilliseconds} ms");
        }

        report.Add(DescribeMemory(process, "全部页面访问后内存"));

        var reportPath = string.IsNullOrWhiteSpace(options.PerfReportPath)
            ? Path.Combine(AppPaths.RootDirectory, "perf-report.txt")
            : options.PerfReportPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllLinesAsync(reportPath, report).ConfigureAwait(true);
    }

    private static string DescribeMemory(System.Diagnostics.Process process, string label) =>
        $"{label}：工作集 {process.WorkingSet64 / 1024.0 / 1024.0:F1} MB｜" +
        $"私有内存 {process.PrivateMemorySize64 / 1024.0 / 1024.0:F1} MB｜" +
        $"托管堆 {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F1} MB｜线程 {process.Threads.Count}";

    /// <summary>界面验收：构建窗口与页面，校验 XAML/绑定，必要时连接模拟设备并输出界面快照。</summary>
    private async Task RunDiagnosticsAsync(MainViewModel viewModel, StartupOptions options)
    {
        var messages = new List<string> { $"shell-check 开始 {DateTime.Now:HH:mm:ss}" };

        var width = options.SnapshotWidth;
        var height = options.SnapshotHeight;

        // 诊断用：强制本次诊断连接的权限模式，用来复现「普通模式 / 管理模式」两种场景。
        ApplyPrivilegeModeOption(options.UiPrivilegeMode, messages);

        if (options.DemoTelnet is not null)
        {
            var parts = options.DemoTelnet.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var settings = new Models.TelnetConnectionSettings
            {
                Host = parts[0],
                Port = parts.Length > 1 && int.TryParse(parts[1], out var demoPort) ? demoPort : 23,
                Username = options.DemoUsername,
                Password = options.DemoPassword,
                AutoLogin = true,
                ConnectionTimeoutMs = 5000,
                CommandTimeoutMs = 5000,
                IdleQuietMs = 400,
                KeepAliveSeconds = options.DemoKeepAliveSeconds,
            };

            try
            {
                // 与【连接】页保持一致：诊断模式也按当前权限模式自动尝试进入特权模式，
                // 这样快照能反映真实的“连接后权限”状态。
                var privilege = new Models.PrivilegeRequest(
                    AppServices.Settings.PrivilegeMode,
                    AppServices.EnablePassword);
                await AppServices.Connections
                    .ConnectTelnetAsync(settings, CancellationToken.None, privilege)
                    .ConfigureAwait(true);
                messages.Add($"已连接模拟设备：{options.DemoTelnet}");
                messages.Add(
                    $"权限：{AppServices.Connections.PrivilegeText}｜" +
                    $"{AppServices.Connections.PrivilegeNotice ?? "(无提示)"}");

                // 真机验收用：连上之后按需跑一条只读命令（原来是只有串口/SSH 分支才有这个钩子）
                if (options.UiConsoleCommand is { Length: > 0 } demoCommand)
                {
                    await RunConsoleCommandCheckAsync(demoCommand, messages).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                messages.Add($"连接模拟设备失败：{ex.Message}");
            }
        }

        if (options.PageKey is not null)
        {
            viewModel.NavigateTo(options.PageKey);
            messages.Add($"已导航到页面：{options.PageKey}");
        }

        if (options.UiFocusMode)
        {
            viewModel.SetFocusMode(true);
            messages.Add("已进入专注 CLI 模式");
        }

        // 页面初始化（如资源库加载）是异步的，这里给它一点时间再截图。
        var dispatcher = Dispatcher;
        await Task.Delay(400).ConfigureAwait(true);
        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        // 诊断用：资源库页面按指定楼栋筛选（验证"交换机/场所/VLAN-IP 三张表同一套筛选规则"）
        if (options.UiBuilding is { Length: > 0 } demoBuilding &&
            viewModel.CurrentViewModel is ResourceLibraryViewModel resourcePage)
        {
            resourcePage.SelectedBuilding = demoBuilding;
            messages.Add(
                $"资源库楼栋筛选「{demoBuilding}」→ VLAN {resourcePage.VlanResults.Count} 行 / " +
                $"交换机 {resourcePage.SwitchResults.Count} 行 / 场所 {resourcePage.LocationResults.Count} 行｜" +
                $"摘要：{resourcePage.QuerySummary}");
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        // 已连接页面（端口/VLAN/Trunk/LLDP）需要用户点击[刷新]才会执行 show 命令；
        // 诊断模式下自动触发一次，便于验证“已解析数据”的界面。
        if (viewModel.CurrentViewModel is DeviceShowPageViewModel showPage && AppServices.Connections.IsConnected)
        {
            try
            {
                await showPage.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
                await Task.Delay(400).ConfigureAwait(true);
                dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                messages.Add($"已自动执行刷新：{showPage.Title}（{showPage.ParseSummary}）");

                if (options.SnapshotPath is not null && showPage.RawOutput.Length > 0)
                {
                    var rawPath = options.SnapshotPath + ".raw.txt";
                    File.WriteAllText(rawPath, showPage.RawOutput);
                    messages.Add($"已输出原始 show 输出：{rawPath}");
                }
            }
            catch (Exception ex)
            {
                messages.Add($"自动刷新失败：{ex.Message}");
            }
        }

        try
        {
            if (string.Equals(options.PageKey, "commandpreview", StringComparison.OrdinalIgnoreCase))
            {
                var plan = new Commands.VlanCommandGenerator().AssignAccessVlan(new[] { "Gi0/1", "Gi0/2", "Gi0/3" }, 100);
                var previewViewModel = new CommandPreviewViewModel(
                    plan,
                    AppServices.Commands,
                    AppServices.Connections,
                    viewModel);
                var previewWindow = new CommandPreviewWindow { DataContext = previewViewModel };
                var content = (FrameworkElement)previewWindow.Content;
                messages.Add("Command Preview 窗口已构建");

                if (options.SnapshotPath is not null)
                {
                    UiSnapshot.Render(content, options.SnapshotPath, 880, 600);
                    messages.Add($"已输出快照：{options.SnapshotPath}");
                }
            }
            else if (string.Equals(options.PageKey, "saveconfig", StringComparison.OrdinalIgnoreCase))
            {
                // [保存配置]（write）弹窗：界面自检 + 防误触逻辑自检。
                // 这条分支以前不处理 --ui-serial，导致"保存配置真机验收"永远跑成"设备未连接"（本次踩到）。
                if (options.UiSerialPort is { Length: > 0 } savePort)
                {
                    await RunSerialDirectConnectAsync(savePort, messages).ConfigureAwait(true);
                }

                var saveViewModel = new SaveConfigViewModel(
                    AppServices.ConfigSave,
                    AppServices.Connections,
                    viewModel);
                var saveWindow = new SaveConfigWindow { DataContext = saveViewModel };
                var content = (FrameworkElement)saveWindow.Content;
                messages.Add("保存配置窗口已构建");

                // 防误触：没按满 1.2 秒就直接调用写入 → 必须被拒绝，且不发送任何命令。
                await saveViewModel.WriteAsync().ConfigureAwait(true);
                messages.Add($"防误触自检：未按住直接写入 → 状态=「{saveViewModel.StatusText}」");
                messages.Add($"防误触自检：HoldProgress={saveViewModel.HoldProgress:0.00}，IsHoldSatisfied={saveViewModel.IsHoldSatisfied}");
                messages.Add($"保存配置自检：{AppServices.ConfigSave.BlockedReason}");
                messages.Add($"保存配置自检：{AppServices.ConfigSave.LastSavedText}");

                if (options.UiSaveDemo && AppServices.Connections.IsConnected)
                {
                    // 走完整流程：按住 1.2 秒 → 真正发送 write → 结果快照。
                    saveViewModel.BeginHold();
                    await Task.Delay(1300).ConfigureAwait(true);
                    var holdSatisfied = saveViewModel.UpdateHold();
                    messages.Add($"保存配置自检：按住 1.3 秒 → IsHoldSatisfied={holdSatisfied}");
                    await saveViewModel.WriteAsync().ConfigureAwait(true);
                    messages.Add($"保存配置自检：结果={saveViewModel.ResultSummary}");
                    messages.Add(
                        "保存配置自检：设备回显=" +
                        saveViewModel.ResultText.Replace("\r", " ").Replace("\n", " ").Trim());
                }
                else if (options.UiSaveDemo)
                {
                    messages.Add("保存配置自检：设备未连接，跳过真实写入（只验证弹窗与防误触）");
                }

                if (options.SnapshotPath is not null)
                {
                    // 窗口内容平时挂在 Window 上；离屏渲染时先摘下来，套一层带底色的画布，
                    // 否则根元素的 Margin 会让右下角落在画布外（快照少掉最后一排按钮）。
                    saveWindow.Content = null;
                    content.DataContext = saveViewModel;
                    var canvas = new Border
                    {
                        Background = (System.Windows.Media.Brush)FindResource("AppBackgroundBrush"),
                        Child = content,
                    };
                    UiSnapshot.Render(canvas, options.SnapshotPath, 640, 640);
                    messages.Add($"已输出快照：{options.SnapshotPath}");

                    var clipped = UiSnapshot.FindClippedContent(canvas);
                    messages.Add(clipped.Count == 0
                        ? "布局检查：保存配置窗口内容没有被裁切"
                        : $"布局检查：{clipped.Count} 处被裁切且无法滚动到");
                    foreach (var line in clipped.Take(8))
                    {
                        messages.Add("裁切 · " + line);
                    }

                    foreach (var line in UiSnapshot.DescribeScrollRegions(canvas))
                    {
                        messages.Add("滚动区 · " + line);
                    }

                    foreach (var line in DescribeButtons(canvas))
                    {
                        messages.Add("按钮 · " + line);
                    }
                }
            }
            else
            {
                var window = new MainWindow { DataContext = viewModel, Width = width, Height = height };
                // 快照不 Show 窗口，Loaded 不会触发，这里显式套用一次字体档位。
                window.FontSize = AppearanceService.UiFontSize;
                // 未 Show 时 SizeChanged 不触发，这里显式套用一次自适应布局（验证小屏折叠）。
                viewModel.UpdateAdaptiveLayout(width);
                var content = (FrameworkElement)window.Content;
                messages.Add("MainWindow 已构建（未 Show）");
                messages.Add($"当前页面：{viewModel.CurrentViewModel?.Title ?? "（空）"}");
                messages.Add($"界面字体：{AppServices.Settings.UiFontScale}（{AppearanceService.UiFontSize:0.#}）");

                // 必须在窗口内容建立之后：CliView 这时才把终端显示注入 ViewModel。
                if (options.UiTypeCommand is { Length: > 0 } typed)
                {
                    await RunCliTypingCheckAsync(viewModel, typed, messages).ConfigureAwait(true);
                }

                if (options.UiPrivilegeRadio is { Length: > 0 } radioTarget)
                {
                    // 先渲染一次：连接页的控件这时才真正创建出来（和串口自检同样的前置条件）。
                    UiSnapshot.Render(content, Path.Combine(Path.GetTempPath(), "prerender-privilege-radio.png"), width, height);
                    CheckPrivilegeRadio(viewModel, content, radioTarget, messages);
                }

                if (options.UiConnectTarget is { Length: > 0 } connectTarget)
                {
                    await RunConnectFailureCheckAsync(viewModel, connectTarget, messages).ConfigureAwait(true);
                }

                if (options.UiExpand is { Length: > 0 } expandTarget)
                {
                    // 先渲染一次：折叠面板里的控件这时才真正创建出来（和其它连接页检查同样的前置条件）。
                    UiSnapshot.Render(content, Path.Combine(Path.GetTempPath(), "prerender-expand.png"), width, height);
                    CheckExpanderPanel(viewModel, content, expandTarget, messages);
                }

                if (options.UiExcelPath is { Length: > 0 } excelPath)
                {
                    await RunExcelImportCheckAsync(viewModel, excelPath, messages).ConfigureAwait(true);
                }

                if (options.UiSerialPort is { Length: > 0 } serialPort)
                {
                    // 先渲染一次：让连接页的按钮真正创建出来。
                    // 真实使用中按钮已经存在（这正是当初崩溃的条件），只有先建好界面才能复现。
                    UiSnapshot.Render(content, options.SnapshotPath ?? Path.Combine(Path.GetTempPath(), "pre-render.png"), width, height);
                    if (viewModel.CurrentViewModel is ConnectionViewModel)
                    {
                        await RunSerialConnectCheckAsync(
                                viewModel,
                                serialPort,
                                messages,
                                options.UiConsoleCommand,
                                options.UiConsoleProvision,
                                options.UiConsoleCreateUser)
                            .ConfigureAwait(true);
                    }
                    else
                    {
                        // 不在连接页：直接连（诊断用）。**故意保持会话不断开**，
                        // 这样后面的 --ui-keys 能在真实串口会话上按真实按键（排查方向键/回显这类问题）。
                        await RunSerialDirectConnectAsync(serialPort, messages).ConfigureAwait(true);
                        if (options.UiConsoleCommand is { Length: > 0 } directCommand)
                        {
                            await RunConsoleCommandCheckAsync(directCommand, messages).ConfigureAwait(true);
                        }

                        if (options.UiConsoleProvision is { Length: > 0 } directProvision)
                        {
                            await RunConsoleProvisionAsync(directProvision, messages).ConfigureAwait(true);
                        }

                        if (options.UiConsoleCreateUser is { Length: > 0 } directCreateUser)
                        {
                            await RunConsoleCreateUserAsync(directCreateUser, messages).ConfigureAwait(true);
                        }
                    }
                }

                if (options.UiSsh is { Length: > 0 } sshTarget)
                {
                    // 诊断：直接建一条 SSH 连接（host:port:用户名:密码），用于真机验收 SSH 链路。
                    // 连接时同样按当前权限模式尝试进入特权模式（与连接页行为一致）。
                    var sshParts = sshTarget.Split(':', 4, StringSplitOptions.TrimEntries);
                    if (sshParts.Length < 4 || !int.TryParse(sshParts[1], out var sshPort))
                    {
                        messages.Add($"SSH 自检：参数格式应为 host:port:用户名:密码，收到「{sshTarget}」");
                    }
                    else
                    {
                        messages.Add($"SSH 自检：连接 {sshParts[0]}:{sshPort}（用户 {sshParts[2]}，密码不打印）…");
                        try
                        {
                            var sshSettings = new Models.SshConnectionSettings
                            {
                                Host = sshParts[0],
                                Port = sshPort,
                                Username = sshParts[2],
                                Password = sshParts[3],
                            };
                            var sshPrivilege = new Models.PrivilegeRequest(
                                AppServices.Settings.PrivilegeMode,
                                AppServices.EnablePassword);
                            await AppServices.Connections
                                .ConnectSshAsync(sshSettings, CancellationToken.None, sshPrivilege)
                                .ConfigureAwait(true);
                            messages.Add($"SSH 自检：状态={AppServices.Connections.State}｜{AppServices.Connections.CurrentKind}｜"
                                         + $"{AppServices.Connections.CliStateText}｜权限={AppServices.Connections.PrivilegeText}｜"
                                         + $"设备名={AppServices.Connections.DeviceName ?? "(未识别)"}");
                            messages.Add($"SSH 自检：{AppServices.Connections.PrivilegeNotice ?? "(无提示)"}");

                            if (options.UiConsoleCommand is { Length: > 0 } sshCommand)
                            {
                                await RunConsoleCommandCheckAsync(sshCommand, messages).ConfigureAwait(true);
                            }
                        }
                        catch (Exception ex)
                        {
                            messages.Add($"SSH 自检：连接失败 {ex.GetType().Name} {ex.Message}");
                        }
                    }
                }

                if (options.UiSnmp is { Length: > 0 })
                {
                    // 概览页在启动时就初始化过（那时还没连接设备），这里在连接之后重新读一次。
                    if (viewModel.CurrentViewModel is OverviewViewModel overviewPage)
                    {
                        overviewPage.RefreshSnmpCommand.Execute(null);
                    }

                    // SNMP 页面：填好目标并真跑一次完整查询（含 WALK 表）
                    if (viewModel.CurrentViewModel is SnmpViewModel snmpPage)
                    {
                        snmpPage.Host = AppServices.Snmp.LastHost;
                        snmpPage.Community = AppServices.Snmp.Community;
                        snmpPage.Port = AppServices.Snmp.Port;
                        snmpPage.FullLargeTables = options.UiSnmpFull;
                        await snmpPage.QueryCommand.ExecuteAsync().ConfigureAwait(true);
                        messages.Add($"SNMP 页面自检：{snmpPage.Status}");
                        messages.Add($"SNMP 页面自检：{snmpPage.LastUpdate}");
                        foreach (var section in snmpPage.Sections)
                        {
                            messages.Add($"  SNMP 表 · {section.Title}：{section.Summary}");
                        }

                        // 快照里把信息库展开，验证「按设备保存」的界面
                        snmpPage.IsLibraryExpanded = true;
                        messages.Add($"SNMP 信息库：{snmpPage.DeviceSummary}");

                        if (options.UiSnmpResumeRounds is { } resumeRounds && resumeRounds > 0)
                        {
                            for (var round = 1; round <= resumeRounds && snmpPage.CanResumeBigTables; round++)
                            {
                                await snmpPage.ResumeBigTablesCommand.ExecuteAsync().ConfigureAwait(true);
                                var mac = snmpPage.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Mac);
                                var ip = snmpPage.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Ip);
                                messages.Add(
                                    $"  SNMP 续拉第 {round} 轮：MAC {mac?.Rows.Count ?? 0} 行"
                                    + $"（还能续={mac?.ResumeOid is not null}）｜ARP {ip?.Rows.Count ?? 0} 行"
                                    + $"（还能续={ip?.ResumeOid is not null}）｜{snmpPage.Status}");
                            }
                        }

                        if (snmpPage.Sections.FirstOrDefault(s => s.Rows.Count > 0) is { } first)
                        {
                            foreach (var row in first.Rows.Take(3))
                            {
                                messages.Add($"  SNMP 行 · {string.Join(" | ", row)}");
                            }
                        }

                        // MAC 表自查（续拉修复的现场证据）：VLAN/MAC 两列**不该有空缺**（N/A 半截行会被丢弃），
                        // 同一个 `VLAN|MAC` 也不该出现两次（按行索引累积后整表重排）。
                        if (snmpPage.Sections.FirstOrDefault(s => s.Category == SnmpCategory.Mac) is { Rows.Count: > 0 } macAudit)
                        {
                            var missing = macAudit.Rows.Count(r => r.Length > 1 && (r[0] == "N/A" || r[1] == "N/A"));
                            var duplicated = macAudit.Rows
                                .GroupBy(r => $"{r[0]}|{r[1]}", StringComparer.Ordinal)
                                .Count(g => g.Count() > 1);
                            messages.Add($"  MAC 自查：{macAudit.Rows.Count} 行｜VLAN/MAC 空缺={missing}（应为 0）｜"
                                         + $"重复(VLAN|MAC)={duplicated}（应为 0）｜还能续={macAudit.ResumeOid is not null}");
                            foreach (var row in macAudit.Rows.Take(3))
                            {
                                messages.Add($"  MAC 行 · {string.Join(" | ", row)}");
                            }
                        }
                    }

                    // SNMP 卡片是异步加载的：等它取完再出快照
                    await Task.Delay(2500).ConfigureAwait(true);
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    if (viewModel.CurrentViewModel is OverviewViewModel overview)
                    {
                        messages.Add($"SNMP 自检：{overview.SnmpStatus}");
                        foreach (var line in overview.SnmpLines)
                        {
                            messages.Add($"  SNMP · {line.Name} = {line.Value}");
                        }
                    }
                }

                if (options.UiPlan is { Length: > 0 } planKind)
                {
                    await RunCommandPreviewCheckAsync(viewModel, options, planKind, messages).ConfigureAwait(true);
                }

                if (options.UiPortsSelect)
                {
                    // 先渲染一次：DataGrid 的行与单元格这时才真正生成。
                    UiSnapshot.Render(content, Path.Combine(Path.GetTempPath(), "prerender-ports.png"), width, height);
                    CheckPortsSelection(viewModel, content, messages);
                }

                if (options.UiLldpSelect)
                {
                    CheckLldpSelection(viewModel, messages);
                }

                if (options.UiQuery is { Length: > 0 } queryText
                    && viewModel.CurrentViewModel is MacIpViewModel macIp)
                {
                    // 诊断：在 MAC/IP 页跑一次查询，验证"IP → ARP 表 / DHCP 绑定 / MAC 表"这条链路的输出。
                    macIp.QueryText = queryText;
                    macIp.QueryCommand.Execute(null);
                    messages.Add($"MAC/IP 查询自检：{queryText} → {macIp.QueryResultText.Replace("\n", " ｜ ")}");
                }

                // 诊断：--ui-inspection / --ui-locate / --ui-locate-trace 属于【巡检】【定位】两个页面，
                // 而这两个页面按用户要求**不打包**（源码保留在项目里，见 RuijieNetworkAssistant.csproj 的排除清单），
                // 所以对应的诊断入口也一并去掉了；恢复这两个页面的同时把这两段诊断加回来即可。


                if (options.UiBulkBackup is { Length: > 0 } bulkIps
                    && viewModel.CurrentViewModel is BackupViewModel bulkBackup)
                {
                    // 诊断：在【备份】页跑一次批量备份，验证"顺序连接 → 读 running-config → 落盘"整条链路。
                    var bulkEntries = bulkIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    // 只拿 IP 部分去勾选（端口是整批共用的参数，不属于设备标识）
                    bulkBackup.Bulk.SelectIps(bulkEntries.Select(e =>
                    {
                        var at = e.LastIndexOf(':');
                        return at > 0 ? e[..at] : e;
                    }));
                    bulkBackup.Bulk.Username = options.DemoUsername;
                    bulkBackup.Bulk.Password = options.DemoPassword;
                    // 端口可以写在第一个 IP 后面（host:port），与 --demo-telnet 一个风格
                    var first = bulkIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    var colon = first?.LastIndexOf(':') ?? -1;
                    if (first is not null && colon > 0 && int.TryParse(first[(colon + 1)..], out var bulkPort))
                    {
                        bulkBackup.Bulk.Port = bulkPort;
                    }
                    await bulkBackup.Bulk.RunBulkBackupAsync().ConfigureAwait(true);
                    messages.Add($"批量备份自检：{bulkBackup.Bulk.SummaryText}｜{bulkBackup.Bulk.StatusHint}");
                    foreach (var item in bulkBackup.Bulk.Results)
                    {
                        messages.Add(
                            $"  批量备份行：{item.Ip}｜{item.Status}｜文件={item.FilePath}｜"
                            + $"原因={item.Error}｜不完整={item.IncompleteReason}｜{item.ElapsedMs} ms");
                    }
                }

                if (options.UiDiffRun && viewModel.CurrentViewModel is BackupViewModel diffVm)
                {
                    // 诊断：跑一次配置对比（左=最新备份，右=当前设备），验证"读文件 → 读设备 → 行级 diff"整条链路。
                    await diffVm.RunDiffForDiagnosticsAsync().ConfigureAwait(true);
                    messages.Add($"配置对比自检：{diffVm.DiffSummary}");
                    foreach (var line in diffVm.DiffLines.Take(20))
                    {
                        messages.Add($"  对比行：{line.KindLabel}｜旧={line.OldLineNumber}｜新={line.NewLineNumber}｜{line.Text}");
                    }
                }

                if (options.UiBackupCurrent && viewModel.CurrentViewModel is BackupViewModel backupVm)
                {
                    // 诊断：点一次【备份】页的"备份当前设备" —— 走"当前连接 → 读 running-config → 落盘"整条链路。
                    // （批量备份只认资源库里的设备，实验机不在库里，所以单设备这条路才是真机验收入口。）
                    var filesBefore = Directory.Exists(AppServices.Settings.BackupDirectory)
                        ? Directory.GetFiles(AppServices.Settings.BackupDirectory, "*.cfg").Length
                        : 0;
                    await backupVm.BackupCommand.ExecuteAsync().ConfigureAwait(true);
                    await Task.Delay(300).ConfigureAwait(true);
                    var filesAfter = Directory.Exists(AppServices.Settings.BackupDirectory)
                        ? Directory.GetFiles(AppServices.Settings.BackupDirectory, "*.cfg").Length
                        : 0;
                    messages.Add($"备份自检：{backupVm.StatusText}｜{backupVm.LastBackupText}");
                    messages.Add($"备份自检：备份目录 {AppServices.Settings.BackupDirectory}｜"
                                 + $"文件数 {filesBefore} → {filesAfter}");
                    var newest = Directory.Exists(AppServices.Settings.BackupDirectory)
                        ? new DirectoryInfo(AppServices.Settings.BackupDirectory).GetFiles("*.cfg")
                            .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
                        : null;
                    if (newest is not null)
                    {
                        var head = File.ReadLines(newest.FullName).Take(3).Select(l => l.Trim()).ToArray();
                        messages.Add($"备份自检：最新文件 {newest.Name}（{newest.Length} 字节）｜开头={string.Join(" / ", head)}");
                    }
                }

                if (options.UiSaveStateRun && viewModel.CurrentViewModel is BackupViewModel saveStateVm)
                {
                    // 诊断：跑一次"检查保存状态"（running vs startup，只读命令），验证整条链路与结论文案。
                    await saveStateVm.RunSaveStateForDiagnosticsAsync().ConfigureAwait(true);
                    messages.Add($"保存状态自检：{saveStateVm.DiffSummary}");
                    foreach (var line in saveStateVm.DiffLines.Take(20))
                    {
                        messages.Add($"  差异行：{line.KindLabel}｜旧={line.OldLineNumber}｜新={line.NewLineNumber}｜{line.Text}");
                    }
                }

                if (options.UiTrafficRun && viewModel.CurrentViewModel is SnmpViewModel trafficVm)
                {
                    // 诊断：跑一次端口流量采样（间隔采两次接口计数器求差，只读）。
                    await trafficVm.RunTrafficForDiagnosticsAsync().ConfigureAwait(true);
                    messages.Add($"流量采样自检：{trafficVm.TrafficSummary}");
                    foreach (var item in trafficVm.TrafficResults.Take(10))
                    {
                        messages.Add(
                            $"  流量行：{item.Port}｜收={item.InText}｜发={item.OutText}｜利用率={item.UtilizationText}｜"
                            + $"错包={item.ErrorText}｜回绕={item.CounterReset}");
                    }
                }

                if (options.UiQuickPing is { Length: > 0 } quickPingTargets
                    && viewModel.CurrentViewModel is QuickPingViewModel quickPing)
                {
                    // 诊断：在【QuickPing】页跑一轮。支持两种写法：
                    //   `192.0.2.1-254` → 拆成"IP 前缀 + 从/到"，**走界面上那条真实路径**（并能让图形模式亮起来）；
                    //   其它（`192.0.2.1,192.0.2.5` 等）→ 走自由目标解析。
                    var dash = quickPingTargets.LastIndexOf('-');
                    var dotted = quickPingTargets.LastIndexOf('.');
                    var usedRange = false;
                    if (dash > dotted && dotted > 0)
                    {
                        var left = quickPingTargets[..dash];
                        var right = quickPingTargets[(dash + 1)..];
                        var lastDot = left.LastIndexOf('.');
                        if (lastDot > 0
                            && int.TryParse(left[(lastDot + 1)..], out var rangeStart)
                            && int.TryParse(right, out var rangeEnd))
                        {
                            quickPing.IpPrefix = left[..lastDot];
                            quickPing.RangeStart = rangeStart;
                            quickPing.RangeEnd = rangeEnd;
                            usedRange = true;
                        }
                    }

                    quickPing.GraphMode = options.UiQuickPingGraph;
                    if (usedRange)
                    {
                        await quickPing.RunRangeForDiagnosticsAsync().ConfigureAwait(true);
                    }
                    else
                    {
                        await quickPing.RunForDiagnosticsAsync(quickPingTargets).ConfigureAwait(true);
                    }

                    messages.Add($"QuickPing 自检：{quickPing.Summary}｜{quickPing.ProgressText}");
                    messages.Add(
                        $"  QuickPing 统计：起始={quickPing.StartTimeText}｜终止={quickPing.EndTimeText}｜"
                        + $"用时={quickPing.ElapsedText}｜已发现={quickPing.FoundCount}｜模式={(quickPing.GraphMode ? "图形" : "表格")}");
                    // 只在线的行最有用（超时的行都是一样的说明，列一堆没意义）
                    foreach (var row in quickPing.Results.Where(r => r.Status == QuickPingStatus.Ok).Take(20))
                    {
                        messages.Add(
                            $"  Ping 行：{row.Target}｜{row.StatusText}｜{row.RttText}｜"
                            + $"MAC={row.MacAddress}｜主机名={row.HostName}｜{row.Detail}");
                    }
                    messages.Add($"  QuickPing 提示：{quickPing.StatusHint}");

                    if (options.UiQuickPingCell is { } cellOctet)
                    {
                        // 真正走一遍界面路径：切图形模式 → 渲染出 256 个格子 → 对目标格子发 Click 事件
                        quickPing.GraphMode = true;
                        UiSnapshot.Render(content, Path.Combine(Path.GetTempPath(), "prerender-quickping-graph.png"), width, height);
                        var cellButton = FindElement<Button>(
                            content,
                            b => b.DataContext is QuickPingGraphItem item && item.LastOctet == cellOctet);

                        if (cellButton is null)
                        {
                            messages.Add($"QuickPing 点格子自检：没找到最后一段为 {cellOctet} 的格子控件");
                        }
                        else
                        {
                            cellButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            messages.Add(
                                $"QuickPing 点格子自检：点 {cellOctet} → 模式={(quickPing.GraphMode ? "图形" : "表格")}｜"
                                + $"选中行={(quickPing.SelectedResult?.Target ?? "（无）")}｜提示={quickPing.StatusHint}");
                        }
                    }
                }

                if (options.UiShowKey is { Length: > 0 } showKey)
                {
                    // 【设备】页：选中某个分类并真跑一次刷新（用于在真机上验表格数据与列名）
                    if (viewModel.CurrentViewModel is DeviceInfoViewModel devicePage)
                    {
                        var category = devicePage.Categories.FirstOrDefault(
                            c => string.Equals(c.Key, showKey, StringComparison.OrdinalIgnoreCase));
                        if (category is null)
                        {
                            messages.Add(
                                $"设备页自检：未知分类 {showKey}（可选："
                                + string.Join(" / ", devicePage.Categories.Select(c => c.Key)) + "）");
                        }
                        else
                        {
                            devicePage.SelectedCategory = category;
                            await devicePage.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
                            await Task.Delay(400).ConfigureAwait(true);
                            messages.Add($"设备页自检：{category.Title}（{category.Command}）→ {devicePage.StatusHint}");
                            messages.Add($"设备页自检：端口 {devicePage.Ports.Count} 行｜VLAN {devicePage.Vlans.Count} 行｜"
                                         + $"MAC {devicePage.MacTable.Count} 行｜三层接口 {devicePage.IpInterfaces.Count} 行");
                            messages.Add($"设备页自检：{devicePage.RawInfo}");
                            foreach (var line in devicePage.RawOutput
                                         .Replace("\r", string.Empty)
                                         .Split('\n')
                                         .Where(l => l.Trim().Length > 0)
                                         .Take(6))
                            {
                                messages.Add("  原始输出 | " + line.Trim());
                            }

                            foreach (var row in devicePage.IpInterfaces.Take(6))
                            {
                                messages.Add($"  IP 行：{row.Interface}｜{row.IpAddress}｜OK?={row.Ok}｜"
                                             + $"Method={row.Method}｜{row.Status}｜{row.Protocol}");
                            }
                        }
                    }
                    else
                    {
                        messages.Add($"设备页自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
                    }
                }

                if (options.UiKeys is { Length: > 0 } keys)
                {
                    // 按键事件需要真实的 PresentationSource（未 Show 的窗口拿不到），
                    // 所以这里临时把窗口显示出来；测完再收起来，快照照常输出。
                    window.Show();
                    window.Activate();
                    UiSnapshot.Render(content, Path.Combine(Path.GetTempPath(), "prerender-keys.png"), width, height);
                    await RunTerminalKeyCheckAsync(viewModel, content, keys, messages, options.UiKeysDelayMs)
                        .ConfigureAwait(true);
                    window.Hide();
                }

                if (options.UiNavAfter is { Length: > 0 } navTarget)
                {
                    // 诊断：诊断动作都跑完后再切页，然后才出快照（用来验证跨页面联动）
                    viewModel.NavigateTo(navTarget);
                    await Task.Delay(400).ConfigureAwait(true);
                    Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    messages.Add($"已切到页面：{viewModel.CurrentViewModel?.Title ?? navTarget}");
                    if (viewModel.CurrentViewModel is OverviewViewModel overviewAfterNav)
                    {
                        messages.Add($"  概览页 · 管理 IP={overviewAfterNav.ManagementAddress}｜"
                                     + $"连接方式={overviewAfterNav.ConnectionKind}｜连接端口={overviewAfterNav.ConnectionPort}");
                    }
                }

                if (options.SnapshotPath is not null)
                {
                    UiSnapshot.Render(content, options.SnapshotPath, width, height);
                    messages.Add($"已输出快照：{options.SnapshotPath}");
                    // 可视树在第一次布局后才生成，这里（渲染之后）才能拿到页面控件。
                    CheckConnectionModeTabs(content, messages);
                    CheckSettingsImportButtons(content, messages);
                    ReportClippedContent(content, width, height, messages);
                }
            }
        }
        catch (Exception ex)
        {
            messages.Add($"失败：{ex.GetType().Name} {ex.Message}");
            UiSnapshot.WriteLog(options.SnapshotPath ?? Path.Combine(AppPaths.RootDirectory, "shell-check.txt"), messages);
            AppServices.Log.Error("界面自检失败", ex);
            Shutdown(2);
            return;
        }

        // 诊断：诊断动作与快照都跑完后再"挂"一会儿不退出 —— 用来观察需要时间才发生的现象
        // （例如 Telnet 空闲保活到点才发 NOP；程序默认 2 秒左右就退出，会看不到）。
        if (options.UiHoldSeconds > 0)
        {
            messages.Add($"按参数保持 {options.UiHoldSeconds} 秒不退出（观察长连接/保活行为）…");
            await Task.Delay(TimeSpan.FromSeconds(options.UiHoldSeconds)).ConfigureAwait(true);
            messages.Add("保持结束，准备退出。");
        }

        messages.Add("shell-check 成功");
        if (options.SnapshotPath is not null)
        {
            UiSnapshot.WriteLog(options.SnapshotPath, messages);
        }
    }

    /// <summary>
    /// 布局验收：报告小窗口下被裁切、且无法通过滚动条看到的界面元素。
    /// </summary>
    /// <summary>
    /// CLI 输入链路验收：在真实 CLI 页面上敲一条命令并回车（走的是 ViewModel 的 SendCommand，
    /// 与按 Enter 完全同一条路径），等设备回显后检查终端里有没有这条命令和回显。
    /// </summary>
    /// <summary>
    /// 报错展示验收：在【连接】页用真实命令走一次连接（失败场景），把报错写进日志，
    /// 用于确认「连接失败时能说清原因和怎么修」。
    /// </summary>
    /// <summary>
    /// Console / Telnet 手风琴验收：展开其中一个时，另一个必须自动收起；检查完恢复原状态。
    /// </summary>
    /// <summary>
    /// 设置页导入验收：跳过文件对话框直接分析 Excel，然后检查「写入资源库」等命令的可用状态。
    /// 对应“预览出来了但按钮点不动”的问题。
    /// </summary>
    /// <summary>
    /// 串口连接验收：在【连接】页走一次真实 Console 连接（连接页必须开着，才会订阅会话事件），
    /// 等“1.5 秒无输出”判定走完，检查不再出现跨线程异常/误报连接失败。
    /// </summary>
    private async Task RunSerialConnectCheckAsync(
        MainViewModel viewModel,
        string portName,
        List<string> messages,
        string? readOnlyCommand = null,
        string? provisionSpec = null,
        string? createUserSpec = null)
    {
        if (viewModel.CurrentViewModel is not ConnectionViewModel connection)
        {
            messages.Add($"串口连接自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        connection.Serial.PortName = portName;
        messages.Add($"串口连接自检：连接 {portName} …");

        await connection.ConnectSerialCommand.ExecuteAsync().ConfigureAwait(true);

        // 等 1.5 秒“无输出”判定 + 状态通知走完（这段以前会在读取线程上触发 UI 更新）
        await Task.Delay(2500).ConfigureAwait(true);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        var errorText = connection.ErrorText ?? string.Empty;
        messages.Add($"串口连接自检：状态={connection.StateText}");
        messages.Add($"串口连接自检：有报错={connection.HasError}｜" +
                     $"报错={(string.IsNullOrWhiteSpace(errorText) ? "（无）" : errorText.Split('\n')[0])}");
        messages.Add($"串口连接自检：提示={connection.ConnectNotice}");
        messages.Add($"串口连接自检：跨线程异常={errorText.Contains("different thread owns it", StringComparison.Ordinal)}");

        // v1.0 门槛 A1：Console →（CLI 就绪）→ 只读命令 → 打印原始输出。
        // 放在断开之前执行，否则会话已经没了。
        if (readOnlyCommand is { Length: > 0 })
        {
            await RunConsoleCommandCheckAsync(readOnlyCommand, messages).ConfigureAwait(true);
        }

        // 开通 SNMP（**写配置**）：固定序列 + write + 回读验证，全部打印进自检日志。
        if (provisionSpec is { Length: > 0 })
        {
            await RunConsoleProvisionAsync(provisionSpec, messages).ConfigureAwait(true);
        }

        // 建本地账号 + 打开 SSH（**写配置**）：给 A2（Telnet/SSH 真机验收）用。
        if (createUserSpec is { Length: > 0 })
        {
            await RunConsoleCreateUserAsync(createUserSpec, messages).ConfigureAwait(true);
        }

        await connection.DisconnectCommand.ExecuteAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 诊断用：不经连接页直接连串口，**连上后保持会话**（供 CLI 页的真实按键验收使用）。
    /// 与 <see cref="RunSerialConnectCheckAsync"/> 的区别：那条走连接页按钮、结束时会断开。
    /// </summary>
    private static async Task RunSerialDirectConnectAsync(string portName, List<string> messages)
    {
        AppServices.Settings.Serial.PortName = portName;
        messages.Add($"串口直连自检：连接 {portName} …（不经连接页）");

        // 与【连接】页保持一致：也按当前权限模式尝试进入特权模式。
        // （2026-09-24 踩到：设备上配了 enable 密码之后，配置线连上默认停在 `Ruijie>`，
        //   直连诊断不带权限请求 → 后面的命令全都因权限不足被拒。）
        var privilege = new Models.PrivilegeRequest(AppServices.Settings.PrivilegeMode, AppServices.EnablePassword);
        await AppServices.Connections
            .ConnectSerialAsync(AppServices.Settings.Serial, CancellationToken.None, privilege)
            .ConfigureAwait(true);

        // 等提示符出来，后面的按键验收才有意义
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!AppServices.Connections.IsCliReady && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250).ConfigureAwait(true);
        }

        messages.Add($"串口直连自检：状态={AppServices.Connections.State}｜{AppServices.Connections.CurrentKind}｜"
                     + $"{AppServices.Connections.CliStateText}｜{AppServices.Connections.PrivilegeText}｜"
                     + $"CLI就绪={AppServices.Connections.IsCliReady}");
    }

    /// <summary>
    /// Console 真机验收（只读）：等 CLI 就绪后发一条 show/display 命令，把设备原始输出打进自检日志。
    /// 只允许 show / display 开头的命令 —— 这个开关不能变成"绕过确认直接改配置"的后门。
    /// </summary>
    private static async Task RunConsoleCommandCheckAsync(string command, List<string> messages)
    {
        var trimmed = command.Trim();
        if (!trimmed.StartsWith("show ", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("display ", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add($"Console 命令自检：拒绝执行非只读命令（只允许 show/display 开头）：{trimmed}");
            return;
        }

        if (!AppServices.Connections.IsConnected)
        {
            messages.Add($"Console 命令自检：当前没有连接（状态={AppServices.Connections.State}），跳过 {trimmed}");
            return;
        }

        // 设备刚上电/刚插线时提示符可能还没出来：最多等 8 秒，别拿"还没就绪"当"命令失败"。
        var waitDeadline = DateTime.UtcNow.AddSeconds(8);
        while (!AppServices.Connections.IsCliReady && DateTime.UtcNow < waitDeadline)
        {
            await Task.Delay(250).ConfigureAwait(true);
        }

        messages.Add($"Console 命令自检：发送 {trimmed} …（连接={AppServices.Connections.CurrentKind}｜"
                     + $"状态={AppServices.Connections.State}｜CLI={AppServices.Connections.CliStateText}｜"
                     + $"权限={AppServices.Connections.PrivilegeText}｜CLI就绪={AppServices.Connections.IsCliReady}）");

        try
        {
            var plan = new CommandPlan
            {
                Title = "Console 真机验收（只读）",
                Description = $"通过当前连接执行 {trimmed}",
                RiskLevel = CommandRiskLevel.Safe,
                Commands = new[] { trimmed },
            };
            var result = await AppServices.Commands.ExecuteAsync(plan).ConfigureAwait(true);
            var output = result.Outputs.FirstOrDefault();
            var raw = output?.RawOutput ?? string.Empty;
            messages.Add($"Console 命令自检：成功={result.Succeeded}｜原始输出 {raw.Length} 字符"
                         + (output?.OutputTruncated == true ? $"（截断：{output.TruncationReason}）" : string.Empty));
            // 打印 60 行：`show running-config` 这类要看到 interface 段（原来 25 行会被截掉）
            foreach (var line in raw.Replace("\r", string.Empty).Split('\n').Take(60))
            {
                messages.Add("  | " + line);
            }

            if (!string.IsNullOrWhiteSpace(output?.Error))
            {
                messages.Add($"Console 命令自检：错误={output!.Error}");
            }
        }
        catch (Exception ex)
        {
            messages.Add($"Console 命令自检：异常 {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// **建本地账号（会改设备配置并 write）**：用户名/密码 + enable 密码 + 打开 SSH 服务。
    ///
    /// 固定序列（可审计，不做通用后门）：
    ///   username &lt;u&gt; password &lt;p&gt; → enable password &lt;p&gt; → line vty 0 4 → login local → exit
    ///   → enable service ssh-server
    /// 全部成功才 `write`（失败不留半截配置）。**不改 line console**：配置线保持免密，
    /// 万一账号配错也不会把自己关在门外（这条是刻意为之）。
    /// 安全：密码**不打印、不写日志**，只报每条命令被接受还是被拒绝。
    /// </summary>
    private static async Task RunConsoleCreateUserAsync(string spec, List<string> messages)
    {
        var colon = spec.IndexOf(':');
        if (colon <= 0 || colon == spec.Length - 1)
        {
            messages.Add("建账号：参数格式应为 <用户名>:<密码>，例如 user1:change-me");
            return;
        }

        var userName = spec[..colon].Trim();
        var password = spec[(colon + 1)..];
        if (userName.Length is < 1 or > 32 || userName.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.'))
        {
            messages.Add("建账号：用户名只能是字母/数字/下划线/短横线/点，且 1~32 位");
            return;
        }

        if (password.Length is < 1 or > 64 || password.Any(char.IsWhiteSpace) || password.Contains(':'))
        {
            messages.Add("建账号：密码 1~64 位，不能含空格或冒号（避免命令解析歧义）");
            return;
        }

        if (!AppServices.Connections.IsConnected)
        {
            messages.Add($"建账号：当前没有连接（状态={AppServices.Connections.State}），跳过");
            return;
        }

        messages.Add($"建账号：即将**写配置** —— 用户 `{userName}`、enable 密码同密码、打开 SSH 服务，最后 write。"
                     + "（密码不打印、不写日志；配置线保持免密不动）");

        var plan = new CommandPlan
        {
            Title = "Console 建本地账号（写配置）",
            Description = $"创建设备本地登录用户 {userName}、设置 enable 密码、line vty 用本地认证、打开 SSH 服务",
            ImpactScope = "写入交换机运行配置并 write 保存；**不改 line console**（配置线仍免密，避免锁死自己）",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = new[]
            {
                $"username {userName} password {password}",
                $"enable password {password}",
                "line vty 0 4",
                "login local",
                "exit",
                "enable service ssh-server",
                // 让配置里的口令以加密形式保存：否则 `show running-config`（以及我们的备份文件）里
                // 会**明文**出现刚建的账号密码（现场实测这台出厂机是 `no service password-encryption`）。
                "service password-encryption",
            },
        };

        try
        {
            var result = await AppServices.Commands.ExecuteAsync(plan).ConfigureAwait(true);
            var index = 0;
            foreach (var output in result.Outputs)
            {
                index++;
                var firstLine = (output.RawOutput ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;

                // 只报"第几条、是什么类型、被接受还是被拒"，**不回显命令原文**（里面有密码）
                var label = index switch
                {
                    1 => "username 行",
                    2 => "enable password 行",
                    3 => "line vty 0 4",
                    4 => "login local",
                    5 => "exit",
                    6 => "enable service ssh-server",
                    _ => $"第 {index} 条",
                };
                messages.Add($"  建账号 · `{label}` → {(output.Succeeded ? "OK" : "被拒绝")}"
                             + (output.Succeeded ? string.Empty : $"｜{output.Error}")
                             + (firstLine.Length > 0 && !firstLine.Contains(password, StringComparison.Ordinal)
                                 ? $"｜回显：{firstLine.Trim()}"
                                 : string.Empty));
            }

            if (!result.Succeeded)
            {
                messages.Add("建账号：有命令被拒绝 → **不执行 write**。请把上面的回显发我。");
                return;
            }
        }
        catch (Exception ex)
        {
            messages.Add($"建账号：写配置异常 {ex.GetType().Name} {ex.Message}（未执行 write）");
            return;
        }

        var savePlan = new CommandPlan
        {
            Title = "保存配置（write）",
            Description = "把账号与 SSH 配置写入启动配置",
            ImpactScope = "落盘：设备重启后仍生效",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = new[] { ConfigSaveService.DefaultCommand },
        };
        try
        {
            var saveResult = await AppServices.Commands.ExecuteAsync(savePlan).ConfigureAwait(true);
            var raw = (saveResult.Outputs.FirstOrDefault()?.RawOutput ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", " ")
                .Trim();
            messages.Add($"  保存配置 · `write` → {(saveResult.Succeeded ? "OK" : "失败")}"
                         + (raw.Length > 0 ? $"｜回显：{raw}" : string.Empty));
        }
        catch (Exception ex)
        {
            messages.Add($"建账号：保存异常 {ex.GetType().Name} {ex.Message}");
        }

        messages.Add($"建账号完成：可以用 `{userName}` 试 Telnet / SSH 了（enable 密码同该密码）。"
                     + "配置线不受影响，出问题仍然可以从 Console 改回来。");
    }

    /// <summary>
    /// **开通 SNMP（会改设备配置并 write）**：给管理接口配 IP + 建只读 community。
    ///
    /// 为什么是固定序列而不是"随便发命令"的诊断开关：这是写操作，必须可审计、可复核、不可被当后门。
    /// 序列（配置类命令由 CommandService 统一包 `configure terminal … end`）：
    ///   interface vlan 1 → ip address &lt;ip&gt; &lt;mask&gt; → no shutdown → exit → snmp-server community &lt;community&gt; ro
    /// 然后：`write` 落盘 → 回读 `show ip interface brief` 与 `show running-config` 作为证据。
    /// 任何一条失败都**不执行 write**（避免半截配置落盘）。
    /// </summary>
    private static async Task RunConsoleProvisionAsync(string spec, List<string> messages)
    {
        var slash = spec.IndexOf('/');
        var colon = spec.IndexOf(':');
        if (slash <= 0 || colon <= slash)
        {
            messages.Add("开通 SNMP：参数格式应为 <管理IP>/<前缀长度>:<community>，例如 192.0.2.240/24:example-community");
            return;
        }

        var ipText = spec[..slash].Trim();
        var prefixText = spec[(slash + 1)..colon].Trim();
        var community = spec[(colon + 1)..].Trim();

        if (!System.Net.IPAddress.TryParse(ipText, out var parsedIp)
            || parsedIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            messages.Add($"开通 SNMP：管理 IP 不是合法 IPv4：{ipText}");
            return;
        }

        if (!int.TryParse(prefixText, out var prefix) || prefix is < 8 or > 30)
        {
            messages.Add($"开通 SNMP：前缀长度不合法（8~30）：{prefixText}");
            return;
        }

        if (community.Length == 0
            || community.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
        {
            messages.Add($"开通 SNMP：community 只能是字母/数字/下划线/短横线：{community}");
            return;
        }

        // 前缀长度 → 点分掩码（只支持连续掩码，8/16/24 之外的按位算）
        var maskValue = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var mask = string.Join(
            '.',
            new[]
            {
                (maskValue >> 24) & 0xFF,
                (maskValue >> 16) & 0xFF,
                (maskValue >> 8) & 0xFF,
                maskValue & 0xFF,
            });

        if (!AppServices.Connections.IsConnected)
        {
            messages.Add($"开通 SNMP：当前没有连接（状态={AppServices.Connections.State}），跳过");
            return;
        }

        messages.Add($"开通 SNMP：即将**写配置** —— 管理 IP {parsedIp}/{prefix}（掩码 {mask}），"
                     + $"只读 community `{community}`，最后执行 write 落盘。");

        var configPlan = new CommandPlan
        {
            Title = "Console 开通 SNMP（写配置）",
            Description = $"给 VLAN 1 配管理 IP {parsedIp}/{prefix} 并创建只读 SNMP community",
            ImpactScope = "写入交换机运行配置，并 write 保存到启动配置（重启后仍生效）",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = new[]
            {
                "interface vlan 1",
                $"ip address {parsedIp} {mask}",
                "no shutdown",
                "exit",
                $"snmp-server community {community} ro",
            },
        };

        try
        {
            var result = await AppServices.Commands.ExecuteAsync(configPlan).ConfigureAwait(true);
            foreach (var output in result.Outputs)
            {
                var firstLine = (output.RawOutput ?? string.Empty)
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
                messages.Add($"  写配置 · `{output.Command}` → {(output.Succeeded ? "OK" : "被拒绝")}"
                             + (output.Succeeded ? string.Empty : $"｜{output.Error}")
                             + (firstLine.Length > 0 ? $"｜回显：{firstLine.Trim()}" : string.Empty));
            }

            if (!result.Succeeded)
            {
                messages.Add("开通 SNMP：有命令被设备拒绝 → **不执行 write**（不留半截配置）。请把上面回显发我。");
                return;
            }
        }
        catch (Exception ex)
        {
            messages.Add($"开通 SNMP：写配置异常 {ex.GetType().Name} {ex.Message}（未执行 write）");
            return;
        }

        var savePlan = new CommandPlan
        {
            Title = "保存配置（write）",
            Description = $"执行 {ConfigSaveService.DefaultCommand} 把运行配置写入启动配置",
            ImpactScope = "落盘：设备重启后配置仍生效",
            RiskLevel = CommandRiskLevel.Caution,
            Commands = new[] { ConfigSaveService.DefaultCommand },
        };
        try
        {
            var saveResult = await AppServices.Commands.ExecuteAsync(savePlan).ConfigureAwait(true);
            var saveOutput = saveResult.Outputs.FirstOrDefault();
            var saveRaw = (saveOutput?.RawOutput ?? string.Empty).Replace("\r", string.Empty).Trim();
            messages.Add($"  保存配置 · `{ConfigSaveService.DefaultCommand}` → {(saveResult.Succeeded ? "OK" : "失败")}"
                         + (saveRaw.Length > 0 ? $"｜回显：{saveRaw.Replace("\n", " ")}" : string.Empty));
            if (!saveResult.Succeeded)
            {
                messages.Add($"开通 SNMP：保存失败（配置可能只在运行配置里）——{saveOutput?.Error}");
            }
        }
        catch (Exception ex)
        {
            messages.Add($"开通 SNMP：保存异常 {ex.GetType().Name} {ex.Message}");
        }

        // 回读验证：接口 IP + running-config 里的 snmp 行（只读）
        try
        {
            var ipBrief = await AppServices.Commands.RunShowCommandAsync("show ip interface brief").ConfigureAwait(true);
            foreach (var line in ipBrief.Replace("\r", string.Empty).Split('\n')
                         .Where(l => l.Contains("VLAN", StringComparison.OrdinalIgnoreCase)
                                     || l.Contains(ipText, StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add("  验证 · show ip interface brief | " + line.Trim());
            }

            var running = await AppServices.Commands.RunShowCommandAsync("show running-config").ConfigureAwait(true);
            foreach (var line in running.Replace("\r", string.Empty).Split('\n')
                         .Where(l => l.Contains("snmp", StringComparison.OrdinalIgnoreCase)
                                     || l.Contains("ip address", StringComparison.OrdinalIgnoreCase)))
            {
                messages.Add("  验证 · show running-config | " + line.Trim());
            }
        }
        catch (Exception ex)
        {
            messages.Add($"开通 SNMP：回读验证失败（不影响已写入的配置）：{ex.Message}");
        }
    }

    private async Task RunExcelImportCheckAsync(MainViewModel viewModel, string path, List<string> messages)
    {
        if (viewModel.CurrentViewModel is not SettingsViewModel settings)
        {
            messages.Add($"Excel 导入自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        messages.Add($"Excel 导入自检：分析前 CanWrite={settings.WriteLibraryCommand.CanExecute(null)}｜" +
                     $"HasPreview={settings.HasPreview}");

        // 关键指标：命令“最后一次通知界面”时按钮处于什么状态。
        // 界面按钮的 IsEnabled 只由这个通知决定（本项目的命令不挂 CommandManager.RequerySuggested），
        // 少了通知，按钮就会停在“预览已生成但仍是禁用”的状态。
        var lastNotifiedCanWrite = settings.WriteLibraryCommand.CanExecute(null);
        void OnCanWriteChanged(object? sender, EventArgs e) =>
            lastNotifiedCanWrite = settings.WriteLibraryCommand.CanExecute(null);

        settings.WriteLibraryCommand.CanExecuteChanged += OnCanWriteChanged;
        try
        {
            await settings.AnalyzeFileAsync(path).ConfigureAwait(true);
        }
        finally
        {
            settings.WriteLibraryCommand.CanExecuteChanged -= OnCanWriteChanged;
        }

        messages.Add($"Excel 导入自检：文件={Path.GetFileName(path)}");
        messages.Add($"Excel 导入自检：预览={settings.PreviewSummary}");
        messages.Add($"Excel 导入自检：HasPreview={settings.HasPreview}｜IsBusy={settings.IsBusy}");
        messages.Add($"Excel 导入自检：最后一次按钮状态通知 = {(lastNotifiedCanWrite ? "可用" : "禁用")}");
        messages.Add($"Excel 导入自检：CanAnalyze={settings.AnalyzeExcelCommand.CanExecute(null)}｜" +
                     $"CanWrite={settings.WriteLibraryCommand.CanExecute(null)}｜" +
                     $"CanExport={settings.ExportLibraryCommand.CanExecute(null)}｜" +
                     $"CanClear={settings.ClearLibraryCommand.CanExecute(null)}");
    }

    /// <summary>设置页按钮的实际可用状态（从可视树里取真实控件，比只看命令更可信）。</summary>
    private static void CheckSettingsImportButtons(FrameworkElement content, List<string> messages)
    {
        var write = FindButton(content, "2. 写入资源库");
        var analyze = FindButton(content, "1. 选择 Excel 生成预览");
        if (write is null && analyze is null)
        {
            return;
        }

        messages.Add(
            $"Excel 导入自检：界面按钮 IsEnabled → 生成预览={analyze?.IsEnabled.ToString() ?? "—"}｜" +
            $"写入资源库={write?.IsEnabled.ToString() ?? "—"}");
    }

    private static Button? FindButton(DependencyObject root, string text) =>
        FindChild<Button>(root, b => string.Equals(b.Content?.ToString(), text, StringComparison.Ordinal));

    /// <summary>
    /// 诊断用：列出可视树里真实画出来的按钮（文本 / 位置 / 尺寸 / 是否可用）。
    /// 比"只看 XAML 写了什么"更可信——能确认按钮没被裁掉、没被禁用。
    /// </summary>
    private static IEnumerable<string> DescribeButtons(FrameworkElement root)
    {
        var results = new List<string>();
        CollectButtons(root, root, results);
        return results;
    }

    private static void CollectButtons(DependencyObject node, FrameworkElement root, List<string> results)
    {
        if (node is UIElement { Visibility: not Visibility.Visible })
        {
            return;
        }

        if (node is Button button && button.ActualWidth > 1)
        {
            Point origin;
            try
            {
                origin = button.TransformToAncestor(root).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                origin = new Point(double.NaN, double.NaN);
            }

            results.Add(
                $"「{button.Content}」@({origin.X:0},{origin.Y:0}) {button.ActualWidth:0}x{button.ActualHeight:0} 可用={button.IsEnabled}");
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            CollectButtons(System.Windows.Media.VisualTreeHelper.GetChild(node, i), root, results);
        }
    }

    private static void CheckConnectionModeTabs(FrameworkElement content, List<string> messages)
    {
        var view = FindChild<Views.ConnectionView>(content);
        if (view is null)
        {
            return;
        }

        var tabs = FindElementByName<TabControl>(content, "ConnectionModeTabs");
        messages.Add(tabs is null
            ? "连接方式自检：未找到连接方式页签"
            : $"连接方式自检：{tabs.Items.Count} 种方式，当前页签={tabs.SelectedIndex}，仅显示当前方式参数");
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
        => FindChild<T>(root, static _ => true);

    /// <summary>诊断用：按参数强制权限模式（normal / manage），未指定时不动。</summary>
    private static void ApplyPrivilegeModeOption(string? mode, List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        if (string.Equals(mode, "normal", StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Settings.PrivilegeMode = Models.PrivilegeMode.Normal;
        }
        else if (string.Equals(mode, "manage", StringComparison.OrdinalIgnoreCase))
        {
            AppServices.Settings.PrivilegeMode = Models.PrivilegeMode.Manage;
        }
        else
        {
            messages.Add($"未知的权限模式参数：{mode}（应为 normal / manage）");
            return;
        }

        messages.Add($"已强制权限模式：{AppServices.Settings.PrivilegeMode}");
    }

    /// <summary>
    /// 诊断用（--ui-expand=console|telnet|ssh）：选择连接页的协议页签，用于生成对应参数页快照。
    /// </summary>
    private static void CheckExpanderPanel(
        MainViewModel viewModel,
        DependencyObject content,
        string target,
        List<string> messages)
    {
        var index = target.Trim().ToLowerInvariant() switch
        {
            "console" or "serial" => 0,
            "telnet" => 1,
            "ssh" => 2,
            _ => -1,
        };

        if (index < 0)
        {
            messages.Add($"连接页签：目标 {target} 不认识（可用 console / telnet / ssh）");
            return;
        }

        var tabs = FindElementByName<TabControl>(content, "ConnectionModeTabs");
        if (tabs is null || index >= tabs.Items.Count)
        {
            messages.Add($"连接页签：当前页面（{viewModel.CurrentViewModel?.Title ?? "空"}）没有找到协议页签");
            return;
        }

        tabs.SelectedIndex = index;
        messages.Add($"连接页签：{target} → 已选择，仅显示该方式参数");
    }

    /// <summary>按 x:Name 在可视树里找控件（诊断自检用）。</summary>
    private static T? FindElementByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        return FindElement<T>(root, e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }

    /// <summary>在可视树里按条件找控件（诊断自检用，例如"最后一段为 37 的图形格子"）。</summary>
    private static T? FindElement<T>(DependencyObject root, Func<T, bool> predicate)
        where T : FrameworkElement
    {
        if (root is T match && predicate(match))
        {
            return match;
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindElement(VisualTreeHelper.GetChild(root, i), predicate);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// 诊断用：找到连接页上的「普通模式 / 管理模式」单选按钮并像用户一样点一下，
    /// 验证单选按钮 → ViewModel → 设置 → 连接流程 这条链路。
    /// </summary>
    private static void CheckPrivilegeRadio(
        MainViewModel viewModel,
        DependencyObject content,
        string target,
        List<string> messages)
    {
        // （本方法保持原样；连接方式页签的自检见 CheckExpanderPanel）
        if (viewModel.CurrentViewModel is not ConnectionViewModel connection)
        {
            messages.Add($"权限模式单选自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        var wantNormal = string.Equals(target, "normal", StringComparison.OrdinalIgnoreCase);
        var radios = new List<System.Windows.Controls.RadioButton>();
        CollectRadios(content, radios);

        // 先反着点一次，确保“切回来”也能生效（模拟用户来回切换）。
        var before = AppServices.Settings.PrivilegeMode;
        var clicked = ToggleRadio(radios, !wantNormal, messages);
        clicked &= ToggleRadio(radios, wantNormal, messages);

        var after = AppServices.Settings.PrivilegeMode;
        var normalChecked = radios.FirstOrDefault(r => r.Content?.ToString()?.Contains("普通模式", StringComparison.Ordinal) == true)?.IsChecked;
        var manageChecked = radios.FirstOrDefault(r => r.Content?.ToString()?.Contains("管理模式", StringComparison.Ordinal) == true)?.IsChecked;

        messages.Add(
            $"权限模式单选自检：点击目标={target}｜点击前设置={before}｜点击后设置={after}｜" +
            $"普通模式选中={normalChecked}｜管理模式选中={manageChecked}｜" +
            $"IsNormalMode={connection.IsNormalMode}｜IsManageMode={connection.IsManageMode}｜" +
            $"绑定请求模式={connection.BuildPrivilegeRequest().Mode}");
        messages.Add($"权限模式提示：{connection.PrivilegeModeHint}");
    }

    /// <summary>
    /// 诊断用：在 CLI 终端上按一串真实按键（走 View 的 PreviewKeyDown / PreviewTextInput），
    /// 用来验证「空格打不出来」「退格没反应」这类键盘问题。
    /// 记号见 <see cref="StartupOptions.UiKeys"/>。
    /// </summary>
    private static async Task RunTerminalKeyCheckAsync(
        MainViewModel viewModel,
        DependencyObject content,
        string script,
        List<string> messages,
        int keyDelayMs = 60)
    {
        if (viewModel.CurrentViewModel is not CliViewModel cli)
        {
            messages.Add($"按键自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        var terminal = FindChild<Views.Controls.TerminalTextBox>(content);
        if (terminal is null)
        {
            messages.Add("按键自检：找不到终端控件");
            return;
        }

        if (!cli.TerminalMode)
        {
            cli.TerminalMode = true;
        }

        var pressed = new List<string>();
        foreach (var token in script.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToUpperInvariant())
            {
                case "SPACE":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Space);
                    pressed.Add("<SPACE>");
                    break;
                case "BACK":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Back);
                    pressed.Add("<BACK>");
                    break;
                case "DEL":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Delete);
                    pressed.Add("<DEL>");
                    break;
                case "ENTER":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Enter);
                    pressed.Add("<ENTER>");
                    break;
                case "TAB":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Tab);
                    pressed.Add("<TAB>");
                    break;
                case "ESC":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Escape);
                    pressed.Add("<ESC>");
                    break;
                case "UP":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Up);
                    pressed.Add("<UP>");
                    break;
                case "DOWN":
                    RaiseTerminalKey(terminal, System.Windows.Input.Key.Down);
                    pressed.Add("<DOWN>");
                    break;
                case "PAUSE":
                    // 等设备把上一条命令的输出打完再按下一个键
                    // （否则"历史里放两条命令再按 ↑"这类判定实验会读到半截输出）
                    await Task.Delay(1500).ConfigureAwait(true);
                    pressed.Add("<PAUSE>");
                    continue;
                default:
                    RaiseTerminalText(terminal, token);
                    pressed.Add(token);
                    break;
            }

            // 按键处理器是 async void：逐个之间让出一下，保持与真实敲键相同的顺序。
            // 间隔可用 --ui-keys-delay= 调小（模拟人手快速连打，用来压"输入乱序/丢字"）。
            await Task.Delay(keyDelayMs).ConfigureAwait(true);
        }

        // 最后一个按键（尤其 ENTER/方向键）之后设备回显可能还在路上 —— 等一拍再读终端，
        // 否则打印的"终端尾部"会缺最后一段，看不出设备到底怎么响应。
        await Task.Delay(900).ConfigureAwait(true);
        Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        await Task.Delay(400).ConfigureAwait(true);

        var text = cli.TerminalText ?? string.Empty;
        // 240 字符太短：两条命令就被截掉一条（验收"命令有没有被抹掉"时看不全）。放宽到 800。
        var tail = text.Length > 800 ? text[^800..] : text;
        messages.Add($"按键自检：按键序列={string.Join(" ", pressed)}");
        messages.Add($"按键自检：终端含擦除控制字符={text.Any(ch => ch is '\b' or '\u007f')}");
        messages.Add(
            $"按键自检：光标位置={terminal.CaretIndex}/{terminal.TextLength}（应等于末尾）｜选区长度={terminal.SelectionLength}");
        messages.Add(
            $"按键自检：终端尾部={tail.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\b", "\\b")}");
    }

    /// <summary>模拟一次真实按键：View 的处理器就挂在 PreviewKeyDown 上。</summary>
    private static void RaiseTerminalKey(System.Windows.IInputElement target, System.Windows.Input.Key key)
    {
        if (target is not System.Windows.UIElement element)
        {
            return;
        }

        var args = new System.Windows.Input.KeyEventArgs(
            System.Windows.Input.Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(element),
            Environment.TickCount,
            key)
        {
            RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent,
        };

        element.RaiseEvent(args);
    }

    /// <summary>模拟一次文本输入（普通字符 / IME 走 PreviewTextInput）。</summary>
    private static void RaiseTerminalText(System.Windows.IInputElement target, string text)
    {
        if (target is not System.Windows.UIElement element)
        {
            return;
        }

        var composition = new System.Windows.Input.TextComposition(
            System.Windows.Input.InputManager.Current, element, text);
        var args = new System.Windows.Input.TextCompositionEventArgs(
            System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = System.Windows.Input.TextCompositionManager.PreviewTextInputEvent,
        };

        element.RaiseEvent(args);
    }

    private static void CollectRadios(DependencyObject root, List<System.Windows.Controls.RadioButton> radios)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.RadioButton radio)
            {
                radios.Add(radio);
            }

            CollectRadios(child, radios);
        }
    }

    /// <summary>把「普通模式 / 管理模式」单选按钮点到指定状态，返回是否找到并点了。</summary>
    private static bool ToggleRadio(List<System.Windows.Controls.RadioButton> radios, bool normal, List<string> messages)
    {
        var keyword = normal ? "普通模式" : "管理模式";
        var radio = radios.FirstOrDefault(r => r.Content?.ToString()?.Contains(keyword, StringComparison.Ordinal) == true);
        if (radio is null)
        {
            messages.Add($"权限模式单选自检：界面上没找到「{keyword}」单选按钮");
            return false;
        }

        radio.IsChecked = true;
        return true;
    }

    /// <summary>
    /// 诊断用：验证【端口】页真的能「直接勾选端口」。
    /// 表格全局样式是 IsReadOnly=True，原来的 DataGridCheckBoxColumn 点不动；
    /// 这里既做命中测试（确认鼠标落到 CheckBox 上，而不是被单元格吃掉），
    /// 也真的勾一行，确认 IsSelected → SelectionSummary 这条绑定链是通的。
    /// </summary>
    private static void CheckPortsSelection(MainViewModel viewModel, DependencyObject content, List<string> messages)
    {
        if (viewModel.CurrentViewModel is not PortsViewModel ports)
        {
            messages.Add($"端口勾选自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        var grid = FindChild<System.Windows.Controls.DataGrid>(content, g => g.Name == "PortsGrid");
        if (grid is null)
        {
            messages.Add("端口勾选自检：找不到端口表格（PortsGrid）");
            return;
        }

        var boxes = new List<System.Windows.Controls.CheckBox>();
        CollectCheckBoxes(grid, boxes);
        var cells = boxes.Where(b => b.DataContext is Models.PortStatusRecord).ToList();

        if (cells.Count == 0)
        {
            messages.Add($"端口勾选自检：表格里没有端口行（端口数={ports.Ports.Count}，复选框={boxes.Count}）");
            return;
        }

        var first = cells[0];
        first.UpdateLayout();
        var center = new System.Windows.Point(first.ActualWidth / 2, first.ActualHeight / 2);
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(first, center)?.VisualHit;
        var hitIsCheckBox = hit is not null && IsSelfOrDescendant(hit, first);

        first.IsChecked = true; // 等价于用户点一下这个复选框
        var item = (Models.PortStatusRecord)first.DataContext;

        messages.Add(
            $"端口勾选自检：端口行={ports.Ports.Count}｜单元格复选框={cells.Count}（含表头 {boxes.Count - cells.Count}）｜" +
            $"命中测试点到复选框={hitIsCheckBox}｜第一行 IsSelected={item.IsSelected}｜" +
            $"选中端口={string.Join(",", ports.SelectedPorts)}");
        messages.Add($"端口勾选自检：{ports.SelectionSummary}");
        messages.Add($"端口详情跟随勾选自检：详情端口={ports.SelectedPortDetail?.NormalizedPort ?? "（未选择）"}");

        // 表头全选：全选后再点应全部取消；部分选中时点表头则补齐全选。
        ports.SelectAllCommand.Execute(null);
        var allSelected = ports.Ports.All(p => p.IsSelected);
        var headerStateAfterAll = ports.IsAllPortsSelected;
        ports.ToggleAllPortsCommand.Execute(null);
        var noneSelected = ports.Ports.All(p => !p.IsSelected);
        if (ports.Ports.Count > 1)
        {
            ports.Ports[0].IsSelected = true;
            ports.ToggleAllPortsCommand.Execute(null);
        }
        var partialBecameAll = ports.Ports.Count > 0 && ports.Ports.All(p => p.IsSelected);
        ports.ToggleAllPortsCommand.Execute(null);
        var secondClickClearedAll = ports.Ports.All(p => !p.IsSelected);
        messages.Add(
            $"端口全选自检：全选后全部选中={allSelected}（表头状态={headerStateAfterAll}）｜" +
            $"再次点击后全部取消={noneSelected && secondClickClearedAll}｜部分选中点击后补全={partialBecameAll}｜" +
            $"全选按钮提示={ports.SelectionSummary}");

        // 留两行勾选着，方便快照里直接看到“勾选后长什么样”。
        foreach (var port in ports.Ports.Take(2))
        {
            port.IsSelected = true;
        }

        ports.SelectedPort = ports.Ports.LastOrDefault();
        messages.Add($"端口勾选自检：快照保留 2 行勾选 → {ports.SelectionSummary}");
        messages.Add($"端口多选显示自检：{ports.SelectedPortsSummary}");
        messages.Add($"端口当前行详情自检：{ports.SelectedPortDetail?.NormalizedPort ?? "（未选择）"}");
    }

    /// <summary>
    /// 诊断用：在【LLDP】页选中第一个邻居，让快照能直接看到详情面板里的完整字段。
    /// </summary>
    private static void CheckLldpSelection(MainViewModel viewModel, List<string> messages)
    {
        if (viewModel.CurrentViewModel is not LldpViewModel lldp)
        {
            messages.Add($"LLDP 选择自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        var first = lldp.Neighbors.FirstOrDefault();
        if (first is null)
        {
            messages.Add("LLDP 选择自检：没有邻居可选");
            return;
        }

        lldp.SelectedNeighbor = first;
        messages.Add(
            $"LLDP 选择自检：选中 {first.LocalPort}｜{first.DetailStateDescription}｜" +
            $"管理IP={Models.LldpNeighborRecord.Or(first.ManagementIp)}｜" +
            $"Capability={Models.LldpNeighborRecord.Or(first.Capability)}｜" +
            $"Aging={Models.LldpNeighborRecord.Or(first.AgingTime)}");
        messages.Add($"LLDP 详情面板首行={first.ToDetailText().Split('\n').FirstOrDefault()?.Trim()}");
    }

    private static void CollectCheckBoxes(DependencyObject root, List<System.Windows.Controls.CheckBox> boxes)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.CheckBox box)
            {
                boxes.Add(box);
            }

            CollectCheckBoxes(child, boxes);
        }
    }

    private static bool IsSelfOrDescendant(DependencyObject candidate, DependencyObject ancestor)
    {
        var current = candidate;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }

            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : null;
        }

        return false;
    }

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match))
            {
                return match;
            }

            var nested = FindChild(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async Task RunConnectFailureCheckAsync(
        MainViewModel viewModel,
        string target,
        List<string> messages)
    {
        if (viewModel.CurrentViewModel is not ConnectionViewModel connection)
        {
            messages.Add($"连接报错自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        var parts = target.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        connection.Telnet.Host = parts[0];
        connection.Telnet.Port = parts.Length > 1 && int.TryParse(parts[1], out var port) ? port : 23;
        connection.Telnet.ConnectionTimeoutMs = 5000;
        connection.Telnet.AutoLogin = false;

        await connection.ConnectTelnetCommand.ExecuteAsync().ConfigureAwait(true);
        await Task.Delay(200).ConfigureAwait(true);

        messages.Add($"连接报错自检：目标={target}｜状态={connection.StateText}｜有报错={connection.HasError}");
        foreach (var line in connection.ErrorText.Split('\n'))
        {
            messages.Add("  报错 · " + line.TrimEnd());
        }
    }

    /// <summary>
    /// 权限闸门验收：用一条真实的配置命令计划打开 Command Preview 弹窗，
    /// 检查普通模式下是否给出[提升权限]入口；可选地点一次[提升权限]，
    /// 验证「提权成功后自动继续执行原操作」。
    /// </summary>
    private async Task RunCommandPreviewCheckAsync(
        MainViewModel viewModel,
        StartupOptions options,
        string planKind,
        List<string> messages)
    {
        var plan = BuildDiagnosticPlan(planKind);
        if (plan is null)
        {
            messages.Add($"命令预览：无法识别的 --ui-plan 取值「{planKind}」");
            return;
        }

        var preview = new CommandPreviewViewModel(
            plan,
            AppServices.Commands,
            AppServices.Connections,
            viewModel);

        var window = new Views.CommandPreviewWindow { DataContext = preview };
        window.Show();
        await Task.Delay(300).ConfigureAwait(true);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        messages.Add(
            $"命令预览：{plan.Title}｜需要管理权限={preview.RequiresPrivilege}｜" +
            $"当前权限={Models.DevicePrivilegeText.ToChinese(preview.PrivilegeLevel)}｜被拦截={preview.IsPrivilegeBlocked}");
        messages.Add($"命令预览提示：{preview.PrivilegeHint}");

        if (options.SnapshotPath is not null)
        {
            var previewPath = options.SnapshotPath + ".preview.png";
            if (window.Content is FrameworkElement content)
            {
                UiSnapshot.Render(content, previewPath, 880, 560);
                messages.Add($"已输出命令预览快照：{previewPath}");
            }
        }

        if (options.UiPlanElevate)
        {
            var before = preview.IsPrivilegeBlocked;
            await preview.ElevateCommand.ExecuteAsync().ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            messages.Add(
                $"命令预览[提升权限]：点击前被拦截={before}｜点击后权限={Models.DevicePrivilegeText.ToChinese(preview.PrivilegeLevel)}｜" +
                $"仍被拦截={preview.IsPrivilegeBlocked}｜已执行={preview.HasResult}");
            messages.Add($"命令预览权限提示：{preview.PrivilegeNotice}");
            if (preview.HasResult)
            {
                messages.Add("命令预览执行结果：");
                foreach (var line in preview.ResultText.Split('\n'))
                {
                    messages.Add("  " + line.TrimEnd());
                }
            }

            if (options.SnapshotPath is not null && window.Content is FrameworkElement afterContent)
            {
                UiSnapshot.Render(afterContent, options.SnapshotPath + ".preview-after.png", 880, 560);
                messages.Add($"已输出提权后的快照：{options.SnapshotPath}.preview-after.png");
            }
        }

        window.Close();
    }

    /// <summary>
    /// 诊断用：把 <c>--ui-plan</c> 的取值翻译成一条真实的配置命令计划。
    /// 目的与 <c>--ui-type</c> 相同 —— 让自动化脚本能走「生成命令并预览 → 执行」这条
    /// 和用户点击完全一样的链路（含 CommandService 的 configure terminal 包装），
    /// 而不是绕过它直接往终端里敲命令。取值形如：
    ///   vlan[:id[:name]]                     新建 VLAN，默认 300 / ExampleNetwork
    ///   vlan-rename[:id[:name]]              改名，默认 300 / ExampleNetwork
    ///   vlan-delete[:id]                     删除，默认 301
    ///   vlan-assign[:ports[:vlanId]]         端口划入 Access VLAN，默认 Gi0/2 / 200
    ///   ports-enable[:port[,port…]]          默认 Gi0/2
    ///   ports-shutdown[:port[,port…]]        默认 Gi0/2
    ///   ports-access[:port[,port…]]          设置为 Access 模式，默认 Gi0/2
    ///   ports-trunk[:port[,port…]]           设置为 Trunk，默认 Gi0/2
    ///   ports-speed[:port[,port…]:speed]     默认 Gi0/2 / 100
    ///   ports-duplex[:port[,port…]:duplex]   默认 Gi0/2 / half
    ///   trunk-allowed-add[:port:vlans]       默认 Ag128 / 200
    ///   trunk-allowed-remove[:port:vlans]    默认 Ag128 / 300
    ///   trunk-native[:port[:vlanId]]         默认 Ag128 / 200
    /// </summary>
    private static Models.CommandPlan? BuildDiagnosticPlan(string planKind)
    {
        var segments = planKind.Split(':', 3);
        var kind = segments[0].Trim().ToLowerInvariant();
        var first = segments.Length > 1 ? segments[1].Trim() : string.Empty;
        var second = segments.Length > 2 ? segments[2].Trim() : string.Empty;

        var vlanGenerator = new Commands.VlanCommandGenerator();
        var portGenerator = new Commands.PortCommandGenerator();
        var trunkGenerator = new Commands.TrunkCommandGenerator();

        switch (kind)
        {
            case "vlan":
            {
                var vlanId = int.TryParse(first, out var parsedId) ? parsedId : 300;
                var name = second.Length > 0 ? second : "ExampleNetwork";
                return vlanGenerator.CreateVlan(vlanId, name);
            }

            case "vlan-rename":
            {
                var vlanId = int.TryParse(first, out var parsedId) ? parsedId : 300;
                var name = second.Length > 0 ? second : "ExampleNetwork";
                return vlanGenerator.RenameVlan(vlanId, name);
            }

            case "vlan-delete":
            {
                var vlanId = int.TryParse(first, out var parsedId) ? parsedId : 301;
                return vlanGenerator.DeleteVlan(vlanId);
            }

            case "vlan-assign":
            {
                var ports = SplitPorts(first);
                var vlanId = int.TryParse(second, out var parsedVlan) ? parsedVlan : 200;
                return vlanGenerator.AssignAccessVlan(ports, vlanId);
            }

            case "ports-enable":
            case "ports-shutdown":
            case "ports-access":
            case "ports-trunk":
            {
                var ports = SplitPorts(first);
                return kind switch
                {
                    "ports-enable" => portGenerator.SetEnabled(ports, true),
                    "ports-shutdown" => portGenerator.SetEnabled(ports, false),
                    "ports-access" => portGenerator.SetMode(ports, false),
                    _ => portGenerator.SetMode(ports, true),
                };
            }

            case "ports-speed":
            {
                var ports = SplitPorts(first);
                return portGenerator.SetSpeed(ports, second.Length > 0 ? second : "100");
            }

            case "ports-duplex":
            {
                var ports = SplitPorts(first);
                return portGenerator.SetDuplex(ports, second.Length > 0 ? second : "half");
            }

            case "trunk-allowed-add":
            case "trunk-allowed-remove":
            {
                var ports = SplitPorts(first, "Ag128");
                var vlanIds = ParseVlanIds(second, kind == "trunk-allowed-add" ? 200 : 300);
                return kind == "trunk-allowed-add"
                    ? trunkGenerator.AddAllowedVlan(ports, vlanIds)
                    : trunkGenerator.RemoveAllowedVlan(ports, vlanIds);
            }

            case "trunk-native":
            {
                var ports = SplitPorts(first, "Ag128");
                var vlanId = int.TryParse(second, out var parsedVlan) ? parsedVlan : 200;
                return trunkGenerator.SetNativeVlan(ports, vlanId);
            }

            default:
                return null;
        }
    }

    /// <summary>诊断参数里的端口列表：逗号分隔，留空时用默认端口。</summary>
    private static string[] SplitPorts(string text, string fallback = "Gi0/2") =>
        text.Length > 0
            ? text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { fallback };

    /// <summary>诊断参数里的 VLAN 列表：支持 10,20,30-32 写法。</summary>
    private static int[] ParseVlanIds(string text, int fallback)
    {
        if (text.Length == 0)
        {
            return new[] { fallback };
        }

        var ids = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 &&
                int.TryParse(part[..dash].Trim(), out var start) &&
                int.TryParse(part[(dash + 1)..].Trim(), out var end))
            {
                for (var id = Math.Min(start, end); id <= Math.Max(start, end); id++)
                {
                    ids.Add(id);
                }
            }
            else if (int.TryParse(part, out var single))
            {
                ids.Add(single);
            }
        }

        return ids.Count > 0 ? ids.ToArray() : new[] { fallback };
    }

    private async Task RunCliTypingCheckAsync(
        MainViewModel viewModel,
        string command,
        List<string> messages)
    {
        if (viewModel.CurrentViewModel is not CliViewModel cli)
        {
            messages.Add($"CLI 输入自检：跳过（当前页面是 {viewModel.CurrentViewModel?.Title ?? "空"}）");
            return;
        }

        messages.Add(
            $"CLI 输入自检：发送前 连接状态={viewModel.ConnectionStateText}｜CLI={viewModel.CliStateText}｜" +
            $"已连接={cli.IsConnected}");
        if (!cli.IsConnected)
        {
            messages.Add("CLI 输入自检：未连接，无法输入");
            return;
        }

        var beforeLength = cli.TerminalText.Length;

        // 支持用 “;;” 连敲多条（例如手动输入账号、密码）。
        var commands = command.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var item in commands)
        {
            if (cli.TerminalMode)
            {
                // 终端模式：走 View 里按键处理所用的同一套方法（逐字符 + 回车）
                messages.Add($"CLI 输入自检：终端模式，逐字符发送「{item}」+ 回车");
                await cli.SendTerminalTextAsync(item).ConfigureAwait(true);
                await cli.SendTerminalKeyAsync(CliViewModel.TerminalKey.Enter).ConfigureAwait(true);
            }
            else
            {
                messages.Add($"CLI 输入自检：行模式，整行发送「{item}」");
                cli.InputText = item;
                await cli.SendCommand.ExecuteAsync().ConfigureAwait(true);
            }

            // 等设备回显 + 终端批量刷新（刷新间隔 80 ms），期间 dispatcher 正常跑消息循环。
            await Task.Delay(900).ConfigureAwait(true);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        }

        var text = cli.TerminalText;
        var echoed = commands.All(c => text.Contains(c, StringComparison.Ordinal));
        var increased = text.Length > beforeLength;

        messages.Add($"CLI 输入自检：输入框已清空={string.IsNullOrEmpty(cli.InputText)}");
        messages.Add(
            $"CLI 输入自检：发送后 连接状态={viewModel.ConnectionStateText}｜CLI={viewModel.CliStateText}");
        messages.Add($"CLI 输入自检：终端长度 {beforeLength} → {text.Length}（增加={increased}）");
        messages.Add($"CLI 输入自检：终端包含命令回显={echoed}");

        if (cli.TerminalMode)
        {
            // 终端模式：↑ 把 ESC[A 发给设备（和 Xshell 一致，由设备回显历史命令）
            await cli.SendTerminalKeyAsync(CliViewModel.TerminalKey.HistoryPrevious).ConfigureAwait(true);
            await Task.Delay(400).ConfigureAwait(true);
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            messages.Add($"CLI 输入自检：终端模式={cli.TerminalMode}｜↑ 已发送设备历史键（ESC[A）");
            messages.Add($"CLI 输入自检：输入框可见={!cli.TerminalMode}");
        }
        else
        {
            // 行模式：↑ 取回上一条命令（本地历史）
            var last = commands[^1];
            var recalled = cli.TryHistoryPrevious() && string.Equals(cli.InputText, last, StringComparison.Ordinal);
            messages.Add($"CLI 输入自检：行模式，↑ 能取回上一条命令={recalled}");
            cli.InputText = string.Empty;
        }

        var tail = text.Length > 400 ? text[^400..] : text;
        messages.Add("CLI 输入自检：终端内容尾部=" + tail.Replace("\r", "\\r").Replace("\n", "\\n"));
    }

    private static void ReportClippedContent(
        FrameworkElement content,
        int width,
        int height,
        List<string> messages)
    {
        var clipped = UiSnapshot.FindClippedContent(content);

        foreach (var region in UiSnapshot.DescribeScrollRegions(content).Take(10))
        {
            messages.Add("  滚动区 · " + region);
        }

        if (clipped.Count == 0)
        {
            messages.Add($"布局检查：{width}x{height} 下没有被裁切的元素");
            return;
        }

        messages.Add($"布局检查：{width}x{height} 下有 {clipped.Count} 处被裁切且无法滚动到");
        foreach (var item in clipped.Take(20))
        {
            messages.Add("  裁切 · " + item);
        }

        if (clipped.Count > 20)
        {
            messages.Add($"  …… 其余 {clipped.Count - 20} 处省略");
        }
    }
}
