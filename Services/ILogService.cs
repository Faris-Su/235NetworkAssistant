namespace RuijieNetworkAssistant.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 轻量日志：写入本地文件 + 保留最近若干条内存记录。
/// CLI 原始输出属于终端数据，禁止写入普通日志。
/// </summary>
public interface ILogService
{
    LogLevel MinimumLevel { get; set; }

    IReadOnlyList<string> RecentEntries { get; }

    void Debug(string message);

    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}
