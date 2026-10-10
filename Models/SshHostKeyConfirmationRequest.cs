namespace RuijieNetworkAssistant.Models;

public sealed class SshHostKeyConfirmationRequestEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SshHostKeyConfirmationRequestEventArgs(SshHostKeyInfo candidate, SshHostKeyInfo? previous = null)
    {
        Candidate = candidate;
        Previous = previous;
    }

    public SshHostKeyInfo Candidate { get; }

    public SshHostKeyInfo? Previous { get; }

    /// <summary>若 UI 无法显示确认框，调用方可区分界面故障与用户主动拒绝。</summary>
    public Exception? Failure { get; private set; }

    public Task<bool> Decision => _completion.Task;

    public void Complete(bool trust) => _completion.TrySetResult(trust);

    public void Fail(Exception exception)
    {
        Failure = exception ?? throw new ArgumentNullException(nameof(exception));
        _completion.TrySetResult(false);
    }
}

public sealed class SshHostKeyTrustCancelledException : OperationCanceledException
{
    public SshHostKeyTrustCancelledException(string host, int port)
        : base($"已取消 SSH 主机密钥信任确认（{host}:{port}）；未发送登录凭据，也未保存信任记录。") { }
}
