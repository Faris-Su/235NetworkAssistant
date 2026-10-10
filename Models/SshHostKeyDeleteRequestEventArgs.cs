namespace RuijieNetworkAssistant.Models;

public sealed class SshHostKeyDeleteRequestEventArgs : EventArgs
{
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SshHostKeyDeleteRequestEventArgs(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    public Task<bool> Decision => _completion.Task;

    public void Complete(bool delete) => _completion.TrySetResult(delete);
}
