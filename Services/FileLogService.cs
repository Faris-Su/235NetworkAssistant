using System.IO;
using System.Text;
using RuijieNetworkAssistant.Helpers;

namespace RuijieNetworkAssistant.Services;

public sealed class FileLogService : ILogService
{
    private const int MaxRecentEntries = 200;
    private readonly object _sync = new();
    private readonly LinkedList<string> _recent = new();
    private readonly string _logFile;

    public FileLogService()
    {
        AppPaths.EnsureCreated();
        _logFile = Path.Combine(AppPaths.LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    public IReadOnlyList<string> RecentEntries
    {
        get
        {
            lock (_sync)
            {
                return _recent.ToList();
            }
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    private void Write(LogLevel level, string message, Exception? exception)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception is not null)
        {
            line += $" :: {exception.GetType().Name}: {exception.Message}";
        }

        lock (_sync)
        {
            _recent.AddLast(line);
            while (_recent.Count > MaxRecentEntries)
            {
                _recent.RemoveFirst();
            }

            try
            {
                File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不能影响主流程。
            }
        }
    }
}
