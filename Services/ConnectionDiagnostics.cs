using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Renci.SshNet.Common;

namespace RuijieNetworkAssistant.Services;

/// <summary>自检结论等级。</summary>
public enum ConnectionCheckLevel
{
    Ok,
    Warning,
    Fail,
}

/// <summary>一条自检结论。</summary>
public sealed record ConnectionCheck(string Title, ConnectionCheckLevel Level, string Message)
{
    /// <summary>界面显示行，例如「✗ Telnet 端口 23 · 对方拒绝连接…」。</summary>
    public string ToLine() => (Level switch
    {
        ConnectionCheckLevel.Ok => "✓",
        ConnectionCheckLevel.Warning => "!",
        _ => "✗",
    }) + " " + Title + " · " + Message;
}

/// <summary>
/// 连接诊断：把 .NET 的原始异常翻译成运维人员能直接照做的中文提示，
/// 并提供【连接】页的连通性自检（串口枚举 / 地址解析 / 网段 / Ping / 端口探测）。
/// 不依赖 WPF，界面与命令行诊断共用。
/// </summary>
public static class ConnectionDiagnostics
{
    /// <summary>当前系统实际存在的串口名（枚举失败时返回空数组，绝不抛异常）。</summary>
    public static string[] SafePortNames()
    {
        try
        {
            return SerialPort.GetPortNames()
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 统一的报错格式：编号 + 结论 + 原因 + 怎么修 + 现场环境 + 原始错误。
    /// 编号用于对照 README 的「报错对照表」，也方便用户直接把整段发回来。
    /// </summary>
    public static string Format(
        string code,
        string title,
        string reason,
        IReadOnlyList<string>? steps = null,
        string? environment = null,
        string? detail = null)
    {
        var builder = new StringBuilder();
        builder.Append('[').Append(code).Append("] ").Append(title).Append('\n');
        builder.Append("原因：").Append(reason);

        if (steps is { Count: > 0 })
        {
            builder.Append("\n怎么修：");
            for (var i = 0; i < steps.Count; i++)
            {
                builder.Append("\n  ").Append(i + 1).Append(". ").Append(steps[i]);
            }
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            builder.Append("\n环境：").Append(environment);
        }

        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.Append("\n原始错误：").Append(detail);
        }

        return builder.ToString();
    }

    /// <summary>串口连接失败说明（含编号、修复步骤、本机实际可用串口）。</summary>
    public static string DescribeSerialFailure(string? portName, Exception exception)
    {
        var port = string.IsNullOrWhiteSpace(portName) ? "（未选择串口）" : portName.Trim();
        var available = SafePortNames();
        var environment = available.Length == 0
            ? "本机当前没有检测到任何串口。"
            : "本机当前可用串口：" + string.Join("、", available) + "。";

        return exception switch
        {
            FileNotFoundException => Format(
                "COM-02",
                "串口不存在",
                $"这台电脑上没有 {port} 这个串口。",
                new[]
                {
                    "插好 Console 线（USB 转串口线），确认交换机已上电。",
                    "装驱动：设备管理器 → 端口(COM 和 LPT) → 没有 COM 口就装 CH340 / PL2303 / FTDI 驱动。",
                    "回到【连接】页点[刷新串口]，在下拉框里选实际出现的那个口（笔记本上 COM1 基本不存在）。",
                },
                environment,
                exception.Message),

            UnauthorizedAccessException => Format(
                "COM-03",
                "串口被占用或没有权限",
                $"{port} 存在，但打不开。",
                new[]
                {
                    "关掉可能占用串口的程序：SecureCRT / PuTTY / 串口助手 / 设备管理器里的“端口属性”窗口。",
                    "拔插一次 USB 转串口线，再点[刷新串口]重试。",
                    "仍然不行就用管理员身份运行本程序。",
                },
                environment,
                exception.Message),

            ArgumentException => Format(
                "COM-04",
                "串口名不合法",
                $"{port} 不是一个有效的串口名。",
                new[]
                {
                    "点[刷新串口]，从下拉框里选择，不要手工乱填。",
                },
                environment,
                exception.Message),

            PlatformNotSupportedException => Format(
                "COM-05",
                "当前系统不支持串口",
                exception.Message,
                new[] { "确认是在 Windows 10/11 x64 上运行本程序。" },
                environment),

            _ => Format(
                "COM-05",
                "串口打开失败",
                $"{port} 打开失败（多半被别的程序占用，或线缆/驱动异常）。",
                new[]
                {
                    "关掉占用串口的程序后重试。",
                    "拔插 USB 转串口线，点[刷新串口]重新选择端口。",
                    "换一个 USB 口或换一根线再试。",
                },
                environment,
                exception.Message),
        };
    }

    /// <summary>Telnet 连接失败说明（按 SocketError 分类给出编号与修复步骤）。</summary>
    public static string DescribeTelnetFailure(string? host, int port, int timeoutMs, Exception exception)
    {
        var hostText = string.IsNullOrWhiteSpace(host) ? "（未填地址）" : host.Trim();
        var target = $"{hostText}:{port}";
        var seconds = Math.Max(1, timeoutMs / 1000);
        IPAddress.TryParse(hostText, out var targetAddress);

        switch (exception)
        {
            case OperationCanceledException:
                return Format(
                    "NET-06",
                    "连接超时",
                    $"{target} 在 {seconds} 秒内没有任何响应。",
                    new[]
                    {
                        "点[连通性自检]，看「本机网段」那一行的结论。",
                        "若显示“与目标不同网段”：给连接交换机的那块网卡配同网段静态 IP（例：目标 192.0.2.49 → 本机 192.0.2.50，掩码 255.255.255.0）。",
                        "若网段相同仍超时：换一根网线 / 换交换机上另一个口，确认接的是管理口。",
                        "确认交换机管理 IP 没变（地址簿里的资料可能已过期）。",
                    },
                    LocalSubnetSummary(targetAddress),
                    exception.Message);

            case SocketException socket:
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => Format(
                        "NET-05",
                        "对方拒绝连接",
                        $"{target} 网络是通的，但端口上没有服务。",
                        new[]
                        {
                            "在交换机上开启 Telnet 服务（锐捷：enable service telnet），并确认 vty 允许 telnet。",
                            "确认端口号：默认 23，有些设备改成了其它端口。",
                            "如果这台设备只能用 Console，请改用本页左侧的 Console 连接。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    SocketError.TimedOut or SocketError.OperationAborted or SocketError.Interrupted => Format(
                        "NET-06",
                        "连接超时",
                        $"{target} 在 {seconds} 秒内没有任何响应。",
                        new[]
                        {
                            "点[连通性自检]：重点看「Telnet 端口」那一行（它真的去连了一次 TCP），「本机网段」只是参考。",
                            "跨网段（校园网/办公网很常见）时先查路由与放行：ping 网关 → 网关/三层设备是否能到目标网段 → "
                            + "中间防火墙或 ACL 是否放行 TCP " + port + "。（不同网段本身不会导致连不上。）",
                            "同网段直连（网线直连交换机管理口）时再查：网线 / 口对不对 / 网卡是否配了同网段 IP。",
                            "确认交换机管理 IP 没变、且已开启 Telnet（锐捷：show service 或 enable service telnet）。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    SocketError.HostNotFound or SocketError.NoData => Format(
                        "NET-02",
                        "地址无法解析",
                        $"{target} 不是有效地址。",
                        new[]
                        {
                            "检查 IP 是否输错（例如把 192.0.2.49 写成 192.0.2.499）。",
                            "或从上方地址簿选中交换机，点[填入 Telnet 参数]自动填。",
                        },
                        environment: null,
                        detail: socket.Message),

                    SocketError.NetworkUnreachable => Format(
                        "NET-03",
                        "本机网络不可达",
                        $"本机没有能到达 {target} 的网络。",
                        new[]
                        {
                            "这是路由层面的不可达（不是「网段不同」）：先看本机有没有默认网关、能不能上到目标网段。",
                            "跨网段/校园网：确认默认网关可达（先 ping 网关），必要时换回能上校园网的那块网卡。",
                            "同网段直连：给连接交换机的那块网卡配同网段静态 IP（网卡可能在“未连接/被禁用”状态），"
                            + "并确认网线插在网卡上、指示灯亮。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    SocketError.HostUnreachable => Format(
                        "NET-04",
                        "目标主机不可达",
                        $"中间链路到不了 {target}。",
                        new[]
                        {
                            "跨网段：路由/三层设备到不了目标网段，或中间防火墙回了 ICMP 不可达 —— 按路由顺序查网关。",
                            "同网段直连：确认接的是交换机的管理口，网线/水晶头没有松动。",
                            "确认目标 IP 写对了（地址簿里的资料可能已过期）。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    SocketError.AccessDenied => Format(
                        "NET-07",
                        "被本机拦截",
                        $"本机拒绝了对 {target} 的连接。",
                        new[]
                        {
                            "在 Windows 防火墙里允许「235修网助手」通过（专用/公用网络都勾上）。",
                            "暂时关闭第三方安全软件再试一次。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    SocketError.AddressNotAvailable => Format(
                        "NET-03",
                        "本机没有可用地址",
                        $"本机没有可用于访问 {target} 的 IP。",
                        new[]
                        {
                            "给网卡配置 IP（或启用被禁用的网卡）。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),

                    _ => Format(
                        "NET-08",
                        "连接失败",
                        $"{target} 连接失败：{socket.Message}",
                        new[]
                        {
                            "点[连通性自检]查看串口/地址/网段/端口四项结论。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message),
                };

            case InvalidOperationException:
                return Format(
                    "NET-01",
                    "没有填写交换机地址",
                    exception.Message,
                    new[]
                    {
                        "在【连接】页填写交换机管理 IP。",
                        "或在上方地址簿里选中交换机，点[填入 Telnet 参数]。",
                    });

            default:
                return Format(
                    "NET-08",
                    "连接失败",
                    $"{target} 连接失败：{exception.Message}",
                    new[]
                    {
                        "点[连通性自检]查看串口/地址/网段/端口四项结论。",
                    },
                    LocalSubnetSummary(targetAddress),
                    exception.Message);
        }
    }

    /// <summary>
    /// 本机网卡与目标是否同网段的一句话结论 —— **只作为环境说明**，不参与"能不能连"的判断，
    /// 更不会拦连接（Telnet 一律直接对 目标IP:端口 发起 TCP 连接，见 TelnetDeviceConnection.ConnectCoreAsync）。
    ///
    /// ⚠️ 2026-09-25 用户反馈：校园网 Wi-Fi 下连交换机，报错里出现"与目标不同网段（这是连不上的常见原因）"，
    /// 被读成"软件认定不同网段就不能 Telnet"。这句话必须改成**如实描述 + 正确的排查顺序**，
    /// 并且把虚拟网卡（VMware / Hyper-V / Clash-TUN / VPN）标出来 —— 多网卡机器上它们最容易把人带偏。
    /// </summary>
    public static string? LocalSubnetSummary(IPAddress? target)
    {
        try
        {
            var locals = new List<(string Text, bool IsVirtual)>();
            string? matchedAdapter = null;
            var matchedVirtual = false;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var isVirtual = LocalNetworkInfoService.LooksVirtual(nic.Description)
                                || LocalNetworkInfoService.LooksVirtual(nic.Name);

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var mask = address.IPv4Mask;
                    locals.Add(($"{address.Address}/{PrefixLength(mask)}", isVirtual));
                    if (target is not null && mask is not null && IsInSameSubnet(target, address.Address, mask))
                    {
                        // 只记第一个命中的（物理网卡优先：后面对虚拟网卡命中会覆盖物理命中，所以留了判断）
                        if (matchedAdapter is null || (matchedVirtual && !isVirtual))
                        {
                            matchedAdapter = nic.Name;
                            matchedVirtual = isVirtual;
                        }
                    }
                }
            }

            if (locals.Count == 0)
            {
                return "本机没有可用的 IPv4 地址（网卡未连或未配 IP）。";
            }

            // 回环地址（隧道 / 端口映射最常见：127.0.0.1:<mapped-port>）压根不存在"网段"这回事，
            // 别在这里说"不同网段" —— 那会把用隧道的人吓一跳（2026-09-25 自检里发现）。
            if (target is not null && IPAddress.IsLoopback(target))
            {
                return $"目标是本机回环地址（{target}），走的是本机隧道/端口映射，不存在网段问题。";
            }

            // 物理网卡排前面：4 个上限之外被截掉的应该是虚拟网卡，不是能上网的那块
            var orderedLocals = locals.OrderBy(l => l.IsVirtual).ToList();
            string Format((string Text, bool IsVirtual) item) =>
                item.IsVirtual ? item.Text + "（虚拟）" : item.Text;
            var text = orderedLocals.Count > 4
                ? "本机网卡（前 4 个）：" + string.Join("、", orderedLocals.Take(4).Select(Format)) + $" 等 {orderedLocals.Count} 个"
                : "本机网卡：" + string.Join("、", orderedLocals.Select(Format));

            if (target is null)
            {
                return text + "。";
            }

            if (matchedAdapter is not null)
            {
                return matchedVirtual
                    ? $"{text}；与目标 {target} 同网段，但匹配到的是虚拟网卡（{matchedAdapter}）——"
                      + "隧道/虚拟网络不一定真能到设备。"
                    : $"{text}；与目标 {target} 同网段（匹配网卡：{matchedAdapter}）。";
            }

            // 跨网段 ≠ 不能连：只要能路由到目标、中间放行 TCP、设备开着服务，跨网段 Telnet 完全正常。
            // （文案别用 Markdown 星号：这些字符串是直接显示在 WPF 里的，星号会原样显示出来。）
            return $"{text}；与目标 {target} 不同网段 —— 这不影响 Telnet（跨网段只要路由可达就能连）。"
                 + "真连不上时按顺序查：① 网关/路由（先 ping 网关）② 中间防火墙、ACL 是否放行目标端口 ③ 交换机是否开启了 Telnet 服务。";
        }
        catch (Exception ex)
        {
            return "读取网卡信息失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 连通性自检：串口枚举 → 地址解析 → 本机网段 → Ping → Telnet 端口探测。
    /// 不改动任何设置，只做只读探测，供用户在现场一键判断卡在哪一步。
    /// </summary>
    public static async Task<IReadOnlyList<ConnectionCheck>> RunSelfCheckAsync(
        string? serialPortName,
        string? telnetHost,
        int telnetPort,
        int timeoutMs,
        CancellationToken cancellationToken,
        int sshPort = 22)
    {
        var results = new List<ConnectionCheck>();

        // 1) 串口
        var ports = SafePortNames();
        if (ports.Length == 0)
        {
            results.Add(new ConnectionCheck(
                "串口",
                ConnectionCheckLevel.Fail,
                "没有检测到任何 COM 口：USB-Serial 驱动没装或线没插好（设备管理器 → 端口(COM 和 LPT)）。"));
        }
        else if (string.IsNullOrWhiteSpace(serialPortName))
        {
            results.Add(new ConnectionCheck(
                "串口",
                ConnectionCheckLevel.Warning,
                "检测到 " + string.Join("、", ports) + "，但当前没有选择端口。"));
        }
        else if (!ports.Contains(serialPortName.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new ConnectionCheck(
                "串口",
                ConnectionCheckLevel.Fail,
                $"当前选择的 {serialPortName.Trim()} 不存在；可用的是 {string.Join("、", ports)}。"));
        }
        else
        {
            results.Add(new ConnectionCheck(
                "串口",
                ConnectionCheckLevel.Ok,
                $"{serialPortName.Trim()} 存在（共检测到 {ports.Length} 个串口）。"));
        }

        // 2) 地址
        if (string.IsNullOrWhiteSpace(telnetHost))
        {
            results.Add(new ConnectionCheck(
                "Telnet 地址",
                ConnectionCheckLevel.Fail,
                "没有填写交换机管理 IP。"));
            return results;
        }

        var host = telnetHost.Trim();
        if (!IPAddress.TryParse(host, out var target))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception ex)
            {
                results.Add(new ConnectionCheck(
                    "Telnet 地址",
                    ConnectionCheckLevel.Fail,
                    $"无法解析 {host}：{ex.Message}"));
                return results;
            }

            if (target is null)
            {
                results.Add(new ConnectionCheck(
                    "Telnet 地址",
                    ConnectionCheckLevel.Fail,
                    $"{host} 没有解析到 IPv4 地址。"));
                return results;
            }

            results.Add(new ConnectionCheck("Telnet 地址", ConnectionCheckLevel.Ok, $"{host} → {target}"));
        }
        else
        {
            results.Add(new ConnectionCheck("Telnet 地址", ConnectionCheckLevel.Ok, target.ToString()));
        }

        // 3) 本机网段
        results.Add(DescribeSubnet(target));

        // 4) Ping（很多交换机会禁 ping，所以不通只作为提示）
        results.Add(await PingAsync(target, timeoutMs, cancellationToken).ConfigureAwait(false));

        // 5) 端口探测
        results.Add(await ProbeTcpAsync(target, telnetPort, timeoutMs, cancellationToken).ConfigureAwait(false));

        // 6) SSH 端口（默认 22）：Telnet 通不代表 SSH 开着，现场经常要两条都比一下。
        if (sshPort is > 0 and <= 65535 && sshPort != telnetPort)
        {
            results.Add(await ProbeTcpAsync(target, sshPort, timeoutMs, cancellationToken, "SSH 端口", isSsh: true)
                .ConfigureAwait(false));
        }

        return results;
    }

    private static ConnectionCheck DescribeSubnet(IPAddress target)
    {
        var summary = LocalSubnetSummary(target);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new ConnectionCheck("本机网段", ConnectionCheckLevel.Warning, "读取网卡信息失败。");
        }

        var sameSubnet = summary.Contains("同网段", StringComparison.Ordinal) &&
                         !summary.Contains("不同网段", StringComparison.Ordinal);
        // ⚠️ 标题写"（参考）"、不同网段只给 Warning：这是**辅助诊断**，不是"不能连"的判定。
        //    Telnet 能不能连由下面那条「Telnet 端口」探测（真的去连一次 TCP）说了算。
        return new ConnectionCheck(
            "本机网段（参考）",
            sameSubnet ? ConnectionCheckLevel.Ok : ConnectionCheckLevel.Warning,
            summary);
    }

    private static async Task<ConnectionCheck> PingAsync(IPAddress target, int timeoutMs, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, Math.Clamp(timeoutMs, 500, 5000))
                .WaitAsync(cancellationToken).ConfigureAwait(false);

            return reply.Status == IPStatus.Success
                ? new ConnectionCheck("Ping", ConnectionCheckLevel.Ok, $"{target} 可 ping 通（{reply.RoundtripTime} ms）。")
                : new ConnectionCheck(
                    "Ping",
                    ConnectionCheckLevel.Warning,
                    $"{target} ping 不通（{reply.Status}）。很多交换机会禁用 ping，这本身不代表 Telnet 一定不通。");
        }
        catch (Exception ex)
        {
            return new ConnectionCheck("Ping", ConnectionCheckLevel.Warning, "Ping 未完成：" + ex.Message);
        }
    }

    private static async Task<ConnectionCheck> ProbeTcpAsync(
        IPAddress target,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken,
        string label = "Telnet 端口",
        bool isSsh = false)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Math.Clamp(timeoutMs, 500, 15000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await client.ConnectAsync(target, port, cts.Token).ConfigureAwait(false);
            watch.Stop();
            return new ConnectionCheck(
                $"{label} {port}",
                ConnectionCheckLevel.Ok,
                $"可以连接（{watch.ElapsedMilliseconds} ms）。");
        }
        catch (Exception ex)
        {
            watch.Stop();
            var detail = isSsh
                ? DescribeSshFailure(target.ToString(), port, timeoutMs, ex)
                : DescribeTelnetFailure(target.ToString(), port, timeoutMs, ex);
            return new ConnectionCheck(
                $"{label} {port}",
                ConnectionCheckLevel.Fail,
                detail.Split('\n')[0]);
        }
    }

    /// <summary>
    /// SSH 连接失败的用户可读说明。与 Telnet 的差别：
    ///   · 认证失败（账号 / 密码 / 账号没有 SSH 权限）是这里最常见的一类；
    ///   · 还要覆盖“算法不匹配”：老固件可能只支持 group1-sha1 / ssh-rsa 这类老算法，
    ///     新固件可能反过来只支持新算法 —— 两头都可能握手失败，需要提示是设备固件问题。
    /// </summary>
    public static string DescribeSshFailure(string? host, int port, int timeoutMs, Exception exception)
    {
        var hostText = string.IsNullOrWhiteSpace(host) ? "（未填地址）" : host.Trim();
        var target = $"{hostText}:{port}";
        var seconds = Math.Max(1, timeoutMs / 1000);
        IPAddress.TryParse(hostText, out var targetAddress);

        foreach (var ex in Flatten(exception))
        {
            switch (ex)
            {
                case SshAuthenticationException:
                    return Format(
                        "SSH-01",
                        "SSH 认证失败",
                        $"{target} 能连上，但用户名或密码被拒绝。",
                        new[]
                        {
                            "核对账号密码（注意锐捷设备分“登录账号”和 enable 密码，两者不是一回事）。",
                            "确认该账号允许 SSH 登录：锐捷上需要 `username <账号> privilege 15 password <密码>`，并在 vty 下放行 ssh。",
                            "如果设备只允许公钥登录，本工具当前版本只支持密码登录。",
                            "连续失败多次可能被设备锁定（login block），先在别的终端确认账号能用。",
                        },
                        LocalSubnetSummary(targetAddress),
                        ex.Message);

                case SshConnectionException:
                    // 实测（2026-09-23）：设备主机密钥只有 512 位时，现代客户端会直接判 KeyExchangeFailed。
                    // 老锐捷 `crypto key generate rsa` 的默认位数正好是 512，所以这条要单独点出来。
                    var keyExchangeFailed = ex.Message.Contains("KeyExchange", StringComparison.OrdinalIgnoreCase) ||
                                            ex.Message.Contains("key exchange", StringComparison.OrdinalIgnoreCase);
                    return Format(
                        "SSH-02",
                        keyExchangeFailed ? "SSH 握手失败（密钥/算法不匹配）" : "SSH 握手失败",
                        $"{target} 的 TCP 端口是通的，但 SSH 协议协商没有谈成。",
                        keyExchangeFailed
                            ? new[]
                            {
                                "最常见原因：设备主机密钥位数太小（锐捷 `crypto key generate rsa` 默认 512 位，现代客户端会拒绝）。",
                                "在交换机上重新生成 2048 位密钥：`configure terminal` → `crypto key generate rsa`（提示位数时输 2048）→ `crypto key generate dsa` → `end`。",
                                "生成后 `show ip ssh` 应显示 “SSH Enable”；若仍 Disable，再执行 `enable service ssh-server`。",
                                "确认这个端口上跑的确实是 SSH（端口填错也会是这个现象）。",
                            }
                            : new[]
                        {
                            "确认这个端口上跑的确实是 SSH（不是 Telnet/HTTP 等别的服务）——端口填错时就是这个现象。",
                            "老固件的算法/密钥位数可能对不上：先确认 `show ip ssh` 是 Enable，再确认主机密钥位数 ≥1024。",
                            "设备正在重启 / SSH 服务刚起来时也可能握手失败，稍后重试。",
                        },
                        LocalSubnetSummary(targetAddress),
                        ex.Message);

                case SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused:
                    return Format(
                        "SSH-03",
                        "对方拒绝连接",
                        $"{target} 网络是通的，但端口上没有 SSH 服务。",
                        new[]
                        {
                            "在交换机上开启 SSH 服务：锐捷 RGOS 一般是 `enable service ssh-server`（部分版本还要生成密钥：`crypto key generate`）。",
                            "确认端口号：SSH 默认 22，有些设备改成了其它端口。",
                            "确认 vty 下允许 SSH：`line vty 0 4` + `transport input ssh`（或 all）。",
                            "如果设备只提供了 Console/Telnet，请改用对应的连接方式。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket.Message);

                case OperationCanceledException:
                case SshOperationTimeoutException:
                    return Format(
                        "SSH-04",
                        "连接超时",
                        $"{target} 在 {seconds} 秒内没有完成 TCP + SSH 握手。",
                        new[]
                        {
                            "点[连通性自检]，看「本机网段」和「Ping」两行。",
                            "跨网段/过隧道时 SSH 握手比 Telnet 慢，可把“连接超时”调到 15000 ms 再试。",
                            "确认交换机管理 IP 没变、SSH 服务处于运行状态。",
                        },
                        LocalSubnetSummary(targetAddress),
                        ex.Message);

                case SocketException socket2
                    when socket2.SocketErrorCode is SocketError.TimedOut or SocketError.OperationAborted or SocketError.Interrupted:
                    return Format(
                        "SSH-04",
                        "连接超时",
                        $"{target} 在 {seconds} 秒内没有响应。",
                        new[]
                        {
                            "点[连通性自检]，看「本机网段」那一行的结论。",
                            "确认交换机管理 IP 没变（地址簿里的资料可能已过期）。",
                        },
                        LocalSubnetSummary(targetAddress),
                        socket2.Message);
            }
        }

        return Format(
            "SSH-99",
            "SSH 连接失败",
            exception.Message,
            new[]
            {
                "先点[连通性自检]确认网络与端口；再到交换机上确认 SSH 服务与账号权限。",
                "完整报错可点[复制报错]发给开发者。",
            },
            LocalSubnetSummary(targetAddress),
            exception.GetType().Name);
    }

    /// <summary>把异常链摊平（SSH.NET 常把真实原因包在 InnerException 里）。</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static int PrefixLength(IPAddress? mask)
    {
        if (mask is null)
        {
            return 32;
        }

        var bits = 0;
        foreach (var b in mask.GetAddressBytes())
        {
            for (var i = 7; i >= 0; i--)
            {
                if ((b & (1 << i)) != 0)
                {
                    bits++;
                }
            }
        }

        return bits;
    }

    private static bool IsInSameSubnet(IPAddress target, IPAddress local, IPAddress mask)
    {
        var t = target.GetAddressBytes();
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        if (t.Length != 4 || l.Length != 4 || m.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if ((t[i] & m[i]) != (l[i] & m[i]))
            {
                return false;
            }
        }

        return true;
    }
}
