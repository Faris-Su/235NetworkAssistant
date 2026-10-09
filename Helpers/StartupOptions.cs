namespace RuijieNetworkAssistant.Helpers;

/// <summary>
/// 启动参数。除诊断参数外，正常启动不需要任何参数。
/// 诊断参数用于开发/验收：不显示窗口即可校验 XAML、绑定与页面布局。
/// </summary>
public sealed class StartupOptions
{
    public bool ShellCheck { get; private set; }

    public string? SnapshotPath { get; private set; }

    public string? PageKey { get; private set; }

    /// <summary>诊断/多库场景：指定资源库文件路径（默认使用 %LocalAppData% 下的库）。</summary>
    public string? ResourceDatabasePath { get; private set; }

    /// <summary>诊断用：渲染页面前先连接一个 Telnet 设备（host:port），用于验证已连接状态的界面。</summary>
    public string? DemoTelnet { get; private set; }

    /// <summary>诊断用：--demo-telnet 使用的登录用户名；不预置账号。</summary>
    public string DemoUsername { get; private set; } = string.Empty;

    /// <summary>诊断用：--demo-telnet 使用的登录密码；不预置凭据。</summary>
    public string DemoPassword { get; private set; } = string.Empty;

    /// <summary>
    /// 诊断用：`--demo-telnet` 连接的**空闲保活间隔（秒）**，0 = 关（默认）。
    /// 用来验收"保活真的在发 IAC NOP"（配合假 Telnet 服务端抓字节）。
    /// </summary>
    public int DemoKeepAliveSeconds { get; private set; }

    /// <summary>性能测量模式：启动耗时 / 内存 / 空闲 CPU / 页面切换耗时，结果写入报告文件。</summary>
    public bool PerfMode { get; private set; }

    public int PerfSeconds { get; private set; } = 5;

    public string? PerfReportPath { get; private set; }

    /// <summary>界面快照尺寸（默认 1280x800）。用于验证小屏布局，例如 --ui-size=1280x720。</summary>
    public int SnapshotWidth { get; private set; } = 1280;

    public int SnapshotHeight { get; private set; } = 800;

    /// <summary>快照时进入专注 CLI 模式（验证小屏下的 CLI 最大空间布局）。</summary>
    public bool UiFocusMode { get; private set; }

    /// <summary>快照时临时套用的界面字体档位（小 / 标准 / 大），用于验证大字体下的小屏布局。</summary>
    public string? UiFontScale { get; private set; }

    /// <summary>
    /// 诊断用：在 CLI 页真正“敲一条命令并回车”，然后等待设备回显再出快照。
    /// 用于自动验证「连接后 CLI 能不能正常输入」这条链路。
    /// </summary>
    public string? UiTypeCommand { get; private set; }

    /// <summary>
    /// 诊断用：在【连接】页走一次真实的连接流程（host:port），用于验证失败时的报错内容与界面展示。
    /// </summary>
    public string? UiConnectTarget { get; private set; }

    /// <summary>
    /// 诊断用：进入页面后展开指定的折叠面板（console / telnet / ssh），用于给新面板出快照。
    /// 折叠面板默认是收起的，出图时看不见内容，所以需要显式展开一次。
    /// </summary>
    public string? UiExpand { get; private set; }

    /// <summary>
    /// 诊断用：在【设置】页直接分析一个 Excel（跳过文件对话框），用于验证导入预览与按钮可用状态。
    /// </summary>
    public string? UiExcelPath { get; private set; }

    /// <summary>诊断用：强制使用 CLI 行模式（默认是终端模式），便于两种输入模式都做验证。</summary>
    public bool UiLineMode { get; private set; }

    /// <summary>诊断用：在【连接】页走一次真实的 Console/串口连接（例如 COM1），验证串口链路与状态。</summary>
    public string? UiSerialPort { get; private set; }

    /// <summary>
    /// 诊断用：串口连上之后，再发一条**只读**命令（例如 <c>show version</c>）并打印原始输出。
    /// 这是 v1.0 门槛"Console → show version"的自动化版本；只允许 show/display 开头，别的直接拒绝。
    /// </summary>
    public string? UiConsoleCommand { get; private set; }

    /// <summary>
    /// 诊断用：**写配置**——通过 Console 给管理接口配 IP 并建只读 SNMP 社区（用于新机型开通 SNMP 对拍）。
    /// 格式 <c>192.0.2.240/24:example-community</c>（IP/前缀长度:community）。
    /// 这是固定序列、不是"随便发命令"的通道：interface vlan 1 → ip address → no shutdown → snmp-server community … ro → write。
    /// </summary>
    public string? UiConsoleProvision { get; private set; }

    /// <summary>
    /// 诊断用：**写配置**——通过 Console 建设备本地登录账号 + enable 密码 + 打开 SSH 服务。
    /// 格式 <c>用户名:密码</c>（例 `user1:change-me`）。固定序列，密码不打印、不写日志。
    /// </summary>
    public string? UiConsoleCreateUser { get; private set; }

    /// <summary>
    /// 诊断用：直接建一条 **SSH** 连接，格式 <c>host:port:用户名:密码</c>（例 `192.0.2.235:22:user1:change-me`）。
    /// 用来在真机上验收 SSH 链路（含"连上后自动进入特权模式"）。
    /// </summary>
    public string? UiSsh { get; private set; }

    /// <summary>
    /// 诊断用：在【设备】页选中某个分类并执行一次刷新（key 见 <c>ShowCenterCategoryKeys</c>：
    /// basic / interface-status / vlan / mac / ip-interface / cpu / logs / running-config）。
    /// </summary>
    public string? UiShowKey { get; private set; }

    /// <summary>
    /// 诊断用：所有诊断动作跑完之后再切到某个页面，然后才出快照。
    /// 用来验证"跨页面的联动"——例如【设备】页读到管理 IP 后，切回【概览】看它有没有显示出来。
    /// </summary>
    public string? UiNavAfter { get; private set; }

    /// <summary>
    /// 诊断用：所有诊断动作与快照跑完之后，**再保持 N 秒不退出**（0~300，默认 0）。
    /// 用于观察"需要等一会儿才发生"的行为 —— 例如 Telnet 空闲保活到底有没有按时发 NOP。
    /// </summary>
    public int UiHoldSeconds { get; private set; }

    /// <summary>诊断用：把"退格发 DEL(0x7F)"打开（默认发 0x08），用于验收该设置项两条路径。</summary>
    public bool UiBackspaceSendsDel { get; private set; }

    /// <summary>
    /// 诊断用：`--ui-keys` 每次按键之间的间隔（毫秒，默认 60）。
    /// 调小可以模拟"人手快速连打"，用来压"输入会不会乱序/丢字"。
    /// </summary>
    public int UiKeysDelayMs { get; private set; } = 60;

    /// <summary>诊断用：覆盖串口 DTR 设置（on/off），用于验收"取消勾选拉 DTR 也能连"。</summary>
    public bool? UiSerialDtr { get; private set; }

    /// <summary>诊断用：覆盖串口 RTS 设置（on/off）。</summary>
    public bool? UiSerialRts { get; private set; }

    /// <summary>
    /// 诊断用：在【备份】页点一次单设备备份（`BackupCommand`）—— 走"当前连接 → 读 running-config → 落盘"整条链路。
    /// 真机验收备份功能用（批量备份只认资源库里的设备，实验机不在库里）。
    /// </summary>
    public bool UiBackupCurrent { get; private set; }

    /// <summary>诊断用：启用 SNMP 并指定目标，格式 host:port:community（用于验证概览页 SNMP 卡片）。</summary>
    public string? UiSnmp { get; private set; }

    /// <summary>
    /// 诊断用：强制本次诊断连接的权限模式（normal / manage），
    /// 用于验证「普通模式下点改配置 → 就地提权」这条链路。
    /// </summary>
    public string? UiPrivilegeMode { get; private set; }

    /// <summary>
    /// 诊断用：用一条示例配置命令计划打开 Command Preview 弹窗，验证权限面板。
    /// 取值：vlan[:id[:name]]（默认使用示例 VLAN）、ports-enable[:端口表]、ports-shutdown[:端口表]（默认 Gi0/2）。
    /// </summary>
    public string? UiPlan { get; private set; }

    /// <summary>诊断用：在 Command Preview 弹窗里点一次[提升权限]，验证提权后自动继续执行。</summary>
    public bool UiPlanElevate { get; private set; }

    /// <summary>
    /// 诊断用：像用户一样点一下连接页的权限模式单选（normal / manage），
    /// 用于验证「单选按钮 → 设置 → 连接流程」整条链路真的按选择走。
    /// </summary>
    public string? UiPrivilegeRadio { get; private set; }

    /// <summary>诊断用：在【端口】页勾选端口，验证「直接勾选」真的可用（表格默认只读）。</summary>
    public bool UiPortsSelect { get; private set; }

    /// <summary>
    /// 诊断用：在 CLI 终端上模拟真实按键序列（用 + 分隔），验证“打字 → 设备”。
    /// 记号：SPACE / BACK / ENTER / TAB / ESC / UP / DOWN / DEL，其它按字面文本走 TextInput。
    /// 例：show+SPACE+ver+BACK+rsion+ENTER
    /// </summary>
    public string? UiKeys { get; private set; }

    /// <summary>诊断用：在【LLDP】页选中第一个邻居，用于快照验证详情面板。</summary>
    public bool UiLldpSelect { get; private set; }

    /// <summary>
    /// 诊断用：在【MAC/IP】页刷新完成后，用这段文本跑一次查询（MAC 或 IP），
    /// 用来验证"IP 反查用的是 ARP 表 + DHCP 绑定表 + MAC 表三份数据"这条链路。
    /// 例：`--ui-query=203.0.113.197`。
    /// </summary>
    public string? UiQuery { get; private set; }

    /// <summary>
    /// 诊断用：在【巡检】页勾选这些 IP（逗号分隔）并**立刻跑一轮巡检**，用于验证整页链路。
    /// Community/超时/重试沿用 `--ui-snmp=` 那几个参数。例：`--ui-inspection=127.0.0.1`。
    /// </summary>
    public string? UiInspection { get; private set; }

    /// <summary>
    /// 诊断用：在【定位】页跑一次定位（值是 IP 或 MAC），勾选范围同 <see cref="UiInspection"/>。
    /// 例：`--ui-locate=02:00:00:00:00:04`。
    /// </summary>
    public string? UiLocate { get; private set; }

    /// <summary>诊断用：配合 <see cref="UiLocate"/>，把【定位】页切到逐跳追踪模式。</summary>
    public bool UiLocateTrace { get; private set; }

    /// <summary>
    /// 诊断用：在【备份】页跑一次批量备份，值是逗号分隔的设备 IP。
    /// 凭据沿用 `--demo-user` / `--demo-pass`。例：`--ui-bulk-backup=127.0.0.1`。
    /// </summary>
    public string? UiBulkBackup { get; private set; }

    /// <summary>诊断用：在【备份】页跑一次配置对比（左=最新备份，右=当前设备的 running-config）。</summary>
    public bool UiDiffRun { get; private set; }

    /// <summary>诊断用：在【备份】页跑一次"检查保存状态"（running vs startup，只读）。</summary>
    public bool UiSaveStateRun { get; private set; }

    /// <summary>诊断用：在【SNMP】页跑一次端口流量采样（间隔采两次计数器求差，只读）。</summary>
    public bool UiTrafficRun { get; private set; }

    /// <summary>诊断用：在【QuickPing】页跑一次 ping（值是目标串，支持网段简写）。</summary>
    public string? UiQuickPing { get; private set; }

    /// <summary>诊断用：配合 <see cref="UiQuickPing"/>，把 QuickPing 切到图形模式出快照。</summary>
    public bool UiQuickPingGraph { get; private set; }

    /// <summary>
    /// 诊断用：QuickPing 跑完后，模拟点图形模式里最后一段为 0..255 的某个格子，
    /// 验证「点格子 → 切回表格模式并选中该行」这条链路（用的是真实的 Click 事件）。
    /// </summary>
    public int? UiQuickPingCell { get; private set; }

    /// <summary>
    /// 诊断用：SNMP 页按"完整拉取大表"跑（不设时间预算），用来实测全量 MAC/ARP 需要多久。
    /// </summary>
    public bool UiSnmpFull { get; private set; }

    /// <summary>诊断用：性能测量前先跑一遍空闲预热（对比"预热前/后各页首次打开耗时"）。</summary>
    public bool PerfWarm { get; private set; }

    /// <summary>诊断用：SNMP 查询后自动点 N 次「继续拉取大表」，用来验证增量续拉（每轮行数应增长）。</summary>
    public int? UiSnmpResumeRounds { get; private set; }

    /// <summary>
    /// 诊断用：在 [保存配置]（write）弹窗里走完整流程（按住 1.2 秒 → 真写设备 → 出结果快照）。
    /// 需要配合 --demo-telnet=host:port 使用（设备必须已连接且处于特权模式）。
    /// </summary>
    public bool UiSaveDemo { get; private set; }

    /// <summary>
    /// 诊断用：资源库页面进入后自动选中指定楼栋（配合 --ui-page=resources 出快照），
    /// 用来验证"楼栋筛选同时作用于交换机 / 场所 / VLAN-IP 三张表"。
    /// </summary>
    public string? UiBuilding { get; private set; }

    /// <summary>
    /// 诊断用：覆盖 SNMP 超时/重试（过 UU 隧道时一来一回 2~3 秒，诊断模式默认的 1200ms 会大量超时，
    /// 所以真机验证必须能调大）。用法：`--ui-snmp-timeout=5000 --ui-snmp-retries=2`。
    /// </summary>
    public int? UiSnmpTimeoutMs { get; private set; }

    /// <summary>诊断用：覆盖 SNMP 重试次数，配合 <see cref="UiSnmpTimeoutMs"/>。</summary>
    public int? UiSnmpRetries { get; private set; }

    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions();
        foreach (var arg in args)
        {
            if (arg.Equals("--shell-check", StringComparison.OrdinalIgnoreCase))
            {
                options.ShellCheck = true;
            }
            else if (arg.StartsWith("--ui-snapshot=", StringComparison.OrdinalIgnoreCase))
            {
                options.SnapshotPath = arg["--ui-snapshot=".Length..];
            }
            else if (arg.StartsWith("--ui-page=", StringComparison.OrdinalIgnoreCase))
            {
                options.PageKey = arg["--ui-page=".Length..];
            }
            else if (arg.StartsWith("--resource-db=", StringComparison.OrdinalIgnoreCase))
            {
                options.ResourceDatabasePath = arg["--resource-db=".Length..];
            }
            else if (arg.StartsWith("--demo-telnet=", StringComparison.OrdinalIgnoreCase))
            {
                options.DemoTelnet = arg["--demo-telnet=".Length..];
            }
            else if (arg.StartsWith("--demo-user=", StringComparison.OrdinalIgnoreCase))
            {
                options.DemoUsername = arg["--demo-user=".Length..];
            }
            else if (arg.StartsWith("--demo-pass=", StringComparison.OrdinalIgnoreCase))
            {
                options.DemoPassword = arg["--demo-pass=".Length..];
            }
            else if (arg.StartsWith("--demo-keepalive=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--demo-keepalive=".Length..], out var demoKeepAlive))
            {
                options.DemoKeepAliveSeconds = Math.Clamp(demoKeepAlive, 0, 3600);
            }
            else if (arg.Equals("--perf", StringComparison.OrdinalIgnoreCase))
            {
                options.PerfMode = true;
            }
            else if (arg.StartsWith("--perf-seconds=", StringComparison.OrdinalIgnoreCase))
            {
                options.PerfSeconds = int.TryParse(arg["--perf-seconds=".Length..], out var seconds)
                    ? Math.Clamp(seconds, 1, 60)
                    : 5;
            }
            else if (arg.StartsWith("--perf-report=", StringComparison.OrdinalIgnoreCase))
            {
                options.PerfReportPath = arg["--perf-report=".Length..];
            }
            else if (arg.StartsWith("--ui-size=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg["--ui-size=".Length..].Split('x', 'X');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var width) &&
                    int.TryParse(parts[1], out var height))
                {
                    options.SnapshotWidth = Math.Clamp(width, 800, 3840);
                    options.SnapshotHeight = Math.Clamp(height, 480, 2160);
                }
            }
            else if (arg.Equals("--ui-focus", StringComparison.OrdinalIgnoreCase))
            {
                options.UiFocusMode = true;
            }
            else if (arg.StartsWith("--ui-font=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiFontScale = arg["--ui-font=".Length..];
            }
            else if (arg.StartsWith("--ui-type=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiTypeCommand = arg["--ui-type=".Length..];
            }
            else if (arg.StartsWith("--ui-connect=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiConnectTarget = arg["--ui-connect=".Length..];
            }
            else if (arg.StartsWith("--ui-expand=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiExpand = arg["--ui-expand=".Length..];
            }
            else if (arg.StartsWith("--ui-excel=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiExcelPath = arg["--ui-excel=".Length..];
            }
            else if (arg.Equals("--ui-line-mode", StringComparison.OrdinalIgnoreCase))
            {
                options.UiLineMode = true;
            }
            else if (arg.StartsWith("--ui-serial=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSerialPort = arg["--ui-serial=".Length..];
            }
            else if (arg.StartsWith("--ui-console-cmd=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiConsoleCommand = arg["--ui-console-cmd=".Length..];
            }
            else if (arg.StartsWith("--ui-console-provision=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiConsoleProvision = arg["--ui-console-provision=".Length..];
            }
            else if (arg.StartsWith("--ui-console-create-user=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiConsoleCreateUser = arg["--ui-console-create-user=".Length..];
            }
            else if (arg.StartsWith("--ui-ssh=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSsh = arg["--ui-ssh=".Length..];
            }
            else if (arg.StartsWith("--ui-show=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiShowKey = arg["--ui-show=".Length..];
            }
            else if (arg.StartsWith("--ui-nav=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiNavAfter = arg["--ui-nav=".Length..];
            }
            else if (arg.StartsWith("--ui-hold=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--ui-hold=".Length..], out var holdSeconds))
            {
                options.UiHoldSeconds = Math.Clamp(holdSeconds, 0, 300);
            }
            else if (arg.Equals("--ui-backspace-del", StringComparison.OrdinalIgnoreCase))
            {
                options.UiBackspaceSendsDel = true;
            }
            else if (arg.StartsWith("--ui-keys-delay=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--ui-keys-delay=".Length..], out var keysDelay))
            {
                options.UiKeysDelayMs = Math.Clamp(keysDelay, 5, 500);
            }
            else if (arg.Equals("--ui-backup-current", StringComparison.OrdinalIgnoreCase))
            {
                options.UiBackupCurrent = true;
            }
            else if (arg.StartsWith("--ui-serial-dtr=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSerialDtr = !arg["--ui-serial-dtr=".Length..]
                    .Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            }
            else if (arg.StartsWith("--ui-serial-rts=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSerialRts = !arg["--ui-serial-rts=".Length..]
                    .Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            }
            else if (arg.StartsWith("--ui-snmp=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSnmp = arg["--ui-snmp=".Length..];
            }
            else if (arg.StartsWith("--ui-privilege=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiPrivilegeMode = arg["--ui-privilege=".Length..];
            }
            else if (arg.StartsWith("--ui-plan=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiPlan = arg["--ui-plan=".Length..];
            }
            else if (arg.Equals("--ui-plan-elevate", StringComparison.OrdinalIgnoreCase))
            {
                options.UiPlanElevate = true;
            }
            else if (arg.StartsWith("--ui-privilege-radio=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiPrivilegeRadio = arg["--ui-privilege-radio=".Length..];
            }
            else if (arg.Equals("--ui-ports-select", StringComparison.OrdinalIgnoreCase))
            {
                options.UiPortsSelect = true;
            }
            else if (arg.StartsWith("--ui-keys=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiKeys = arg["--ui-keys=".Length..];
            }
            else if (arg.Equals("--ui-lldp-select", StringComparison.OrdinalIgnoreCase))
            {
                options.UiLldpSelect = true;
            }
            else if (arg.StartsWith("--ui-query=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiQuery = arg["--ui-query=".Length..];
            }
            else if (arg.StartsWith("--ui-inspection=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiInspection = arg["--ui-inspection=".Length..];
            }
            else if (arg.StartsWith("--ui-locate=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiLocate = arg["--ui-locate=".Length..];
            }
            else if (arg.Equals("--ui-locate-trace", StringComparison.OrdinalIgnoreCase))
            {
                options.UiLocateTrace = true;
            }
            else if (arg.StartsWith("--ui-bulk-backup=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiBulkBackup = arg["--ui-bulk-backup=".Length..];
            }
            else if (arg.Equals("--ui-diff-run", StringComparison.OrdinalIgnoreCase))
            {
                options.UiDiffRun = true;
            }
            else if (arg.Equals("--ui-save-state-run", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSaveStateRun = true;
            }
            else if (arg.Equals("--ui-traffic-run", StringComparison.OrdinalIgnoreCase))
            {
                options.UiTrafficRun = true;
            }
            else if (arg.StartsWith("--ui-quickping=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiQuickPing = arg["--ui-quickping=".Length..];
            }
            else if (arg.Equals("--ui-quickping-graph", StringComparison.OrdinalIgnoreCase))
            {
                options.UiQuickPingGraph = true;
            }
            else if (arg.StartsWith("--ui-quickping-cell=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--ui-quickping-cell=".Length..], out var quickPingCell))
            {
                options.UiQuickPingCell = Math.Clamp(quickPingCell, 0, 255);
            }
            else if (arg.Equals("--ui-snmp-full", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSnmpFull = true;
            }
            else if (arg.Equals("--perf-warm", StringComparison.OrdinalIgnoreCase))
            {
                options.PerfWarm = true;
            }
            else if (arg.StartsWith("--ui-snmp-resume=", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(arg["--ui-snmp-resume=".Length..], out var resumeRounds))
            {
                options.UiSnmpResumeRounds = Math.Clamp(resumeRounds, 0, 20);
            }
            else if (arg.Equals("--ui-save-demo", StringComparison.OrdinalIgnoreCase))
            {
                options.UiSaveDemo = true;
            }
            else if (arg.StartsWith("--ui-building=", StringComparison.OrdinalIgnoreCase))
            {
                options.UiBuilding = arg["--ui-building=".Length..];
            }
            else if (arg.StartsWith("--ui-snmp-timeout=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(arg["--ui-snmp-timeout=".Length..], out var snmpTimeout))
            {
                options.UiSnmpTimeoutMs = snmpTimeout;
            }
            else if (arg.StartsWith("--ui-snmp-retries=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(arg["--ui-snmp-retries=".Length..], out var snmpRetries))
            {
                options.UiSnmpRetries = snmpRetries;
            }
        }

        return options;
    }

    public bool IsDiagnosticsMode => ShellCheck || SnapshotPath is not null;
}
