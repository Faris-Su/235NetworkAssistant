namespace RuijieNetworkAssistant.Models;

/// <summary>
/// 命令行结束符。Console 通常用 CR，Telnet（NVT）用 CRLF，个别设备需要 LF，
/// 因此做成可选而不是写死。
/// </summary>
public sealed record LineEndingOption(string Display, string Value)
{
    public static IReadOnlyList<LineEndingOption> All { get; } = new[]
    {
        new LineEndingOption("回车 + 换行 (CRLF)", "\r\n"),
        new LineEndingOption("仅回车 (CR)", "\r"),
        new LineEndingOption("仅换行 (LF)", "\n"),
    };

    public static string DefaultSerial => "\r";

    public static string DefaultTelnet => "\r\n";

    /// <summary>SSH 终端按“回车”发的是单个 CR（PuTTY / OpenSSH 都是这样），所以默认 CR。</summary>
    public static string DefaultSsh => "\r";
}
