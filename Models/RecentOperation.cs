namespace RuijieNetworkAssistant.Models;

/// <summary>最近操作记录（概览页展示）。不记录 CLI 原始输出，避免日志膨胀。</summary>
public sealed class RecentOperation
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public bool Succeeded { get; init; } = true;

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public string ResultText => Succeeded ? "成功" : "失败";
}
