using System.IO;
using System.Text;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 原始 CLI 输出留档。终端显示缓冲会被裁剪，但这里把所有收到的原始文本按顺序写入
/// 本地会话文件（%LocalAppData%\235NetworkAssistant\work\session-*.log），
/// 因此“保存输出”始终能拿到完整原始输出，同时内存不会随会话增长。
/// 该文件属于终端数据，不写入普通日志。
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter? _writer;
    private bool _disposed;

    public SessionRecorder(string sessionId)
    {
        Helpers.AppPaths.EnsureCreated();
        var safeId = string.IsNullOrWhiteSpace(sessionId) ? DateTime.Now.ToString("yyyyMMdd-HHmmss") : sessionId;
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            safeId = safeId.Replace(ch, '_');
        }

        FilePath = Path.Combine(Helpers.AppPaths.WorkingDirectory, $"session-{safeId}.log");
        try
        {
            _writer = new StreamWriter(
                new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: false),
                new UTF8Encoding(false))
            {
                AutoFlush = false,
            };
        }
        catch (Exception ex)
        {
            // 写不了日志文件（磁盘满、目录被策略锁住、杀软拦截……）不能让整个 CLI 页面打不开：
            // 终端照常可用，只是不能导出原始输出，由界面提示用户。
            FailureReason = ex.Message;
        }
    }

    public string FilePath { get; }

    /// <summary>是否成功建立了留档文件。false 时终端仍可用，但无法导出原始输出。</summary>
    public bool IsRecording => _writer is not null;

    /// <summary>无法留档时的原因（用于界面提示）。</summary>
    public string? FailureReason { get; }

    public long TotalChars { get; private set; }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer?.Write(text);
            TotalChars += text.Length;
        }
    }

    /// <summary>把缓冲区刷到磁盘（由批量刷新定时器周期性调用，不逐字符写盘）。</summary>
    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer?.Flush();
        }
    }

    /// <summary>把完整原始输出另存到用户指定路径。</summary>
    public string SaveCopy(string destinationPath)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException(
                $"本次会话没有可导出的原始输出：{FailureReason ?? "留档文件未建立"}");
        }

        lock (_sync)
        {
            _writer.Flush();
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(FilePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // 释放阶段忽略刷新异常。
            }

            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 释放阶段忽略异常。
            }
        }
    }
}
