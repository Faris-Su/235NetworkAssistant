using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Models;

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

public enum SerialFlowControl
{
    None,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff,
}

/// <summary>
/// Console / Serial 参数。默认值只是默认值，不允许假设所有锐捷设备固定 9600。
/// </summary>
public sealed class SerialConnectionSettings : ObservableObject
{
    private string _portName = "COM1";
    private int _baudRate = 9600;
    private int _dataBits = 8;
    private SerialStopBits _stopBits = SerialStopBits.One;
    private SerialParity _parity = SerialParity.None;
    private SerialFlowControl _flowControl = SerialFlowControl.None;
    private string _encoding = "UTF-8";
    private string _lineEnding = LineEndingOption.DefaultSerial;
    private bool _dtrEnable = true;
    private bool _rtsEnable = true;
    private int _commandTimeoutMs = 15000;
    private int _idleQuietMs = 800;

    public string PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set => SetProperty(ref _baudRate, value);
    }

    public int DataBits
    {
        get => _dataBits;
        set => SetProperty(ref _dataBits, value);
    }

    public SerialStopBits StopBits
    {
        get => _stopBits;
        set => SetProperty(ref _stopBits, value);
    }

    public SerialParity Parity
    {
        get => _parity;
        set => SetProperty(ref _parity, value);
    }

    public SerialFlowControl FlowControl
    {
        get => _flowControl;
        set => SetProperty(ref _flowControl, value);
    }

    /// <summary>UTF-8 / ASCII / Latin1（GBK 需要额外代码页支持，见 docs/roadmap.md）。</summary>
    public string Encoding
    {
        get => _encoding;
        set => SetProperty(ref _encoding, value);
    }

    /// <summary>命令行结束符：Console 一般是 CR，个别设备需要 CRLF/LF。</summary>
    public string LineEnding
    {
        get => _lineEnding;
        set => SetProperty(ref _lineEnding, string.IsNullOrEmpty(value) ? LineEndingOption.DefaultSerial : value);
    }

    public int CommandTimeoutMs
    {
        get => _commandTimeoutMs;
        set => SetProperty(ref _commandTimeoutMs, value);
    }

    /// <summary>无新数据后等待多久认为一条命令输出结束。</summary>
    public int IdleQuietMs
    {
        get => _idleQuietMs;
        set => SetProperty(ref _idleQuietMs, value);
    }

    public SerialConnectionSettings Clone() => new()
    {
        PortName = PortName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        StopBits = StopBits,
        Parity = Parity,
        FlowControl = FlowControl,
        Encoding = Encoding,
        LineEnding = LineEnding,
        CommandTimeoutMs = CommandTimeoutMs,
        IdleQuietMs = IdleQuietMs,
        DtrEnable = DtrEnable,
        RtsEnable = RtsEnable,
    };

    /// <summary>
    /// 打开串口时是否拉 DTR。默认开（多数 USB-Serial + 配置线都要）；个别适配器/设备接上 DTR 会复位或不出数据，
    /// 这时关掉它就能通 —— 借鉴 PuTTY 串口页把 DTR/RTS 交给用户的做法。
    /// </summary>
    public bool DtrEnable
    {
        get => _dtrEnable;
        set => SetProperty(ref _dtrEnable, value);
    }

    /// <summary>是否拉 RTS（选硬件流控时由驱动接管，这里会自动让位）。</summary>
    public bool RtsEnable
    {
        get => _rtsEnable;
        set => SetProperty(ref _rtsEnable, value);
    }
}
