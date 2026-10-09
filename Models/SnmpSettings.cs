namespace RuijieNetworkAssistant.Models;

/// <summary>
/// SNMP v2c 只读查询设置。Community 等同于密码：
/// **不写入日志、不落盘**（与 Telnet 密码一致，仅本次运行内有效）。
/// </summary>
public sealed class SnmpSettings
{
    public event EventHandler? EnabledChanged;

    private bool _enabled;

    /// <summary>是否启用 SNMP（默认关闭，用户显式开启才查询；SNMP 页面不受此项限制）。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>SNMP 版本：本版本只支持 v2c（保留字段，UI 固定显示 v2c）。</summary>
    public string Version { get; set; } = "v2c";

    /// <summary>最近一次查询过的目标 IP（辅助识别，只记 IP 不记凭据）。</summary>
    public string LastHost { get; set; } = string.Empty;

    /// <summary>自动刷新总开关，默认 OFF（本版本每次启动都是 OFF）。</summary>
    public bool AutoRefreshEnabled { get; set; }

    /// <summary>自动刷新间隔（秒），必须是 5 的整数倍，最小 5。</summary>
    public int AutoRefreshSeconds { get; set; } = 15;

    /// <summary>Community（只读）。不持久化。</summary>
    public string Community { get; set; } = string.Empty;

    /// <summary>UDP 端口，默认 161，可修改。</summary>
    public int Port { get; set; } = 161;

    /// <summary>单次请求超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 1500;

    /// <summary>重试次数。</summary>
    public int Retries { get; set; } = 1;

    /// <summary>
    /// 是否"完整拉取大表"（MAC / ARP 不设时间预算）。
    ///
    /// 默认 false：大型设备的 MAC / ARP 表可能有大量记录，完整查询耗时较长且设备可能限速；
    /// 默认按时间预算先返回部分结果并标注"已截断"，可由用户按需继续拉取。
    /// 用户勾一次就记住（落盘的是这个开关本身，不含任何凭据）。
    /// </summary>
    public bool FullLargeTables { get; set; }
}
