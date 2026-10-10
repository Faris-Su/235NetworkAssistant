using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 首次信任的可测试编排：探测完成后才请求用户决定，且只在明确同意后写库。
/// 调用方必须在本方法成功返回后才开始真实 SSH 认证。
/// </summary>
public sealed class SshHostKeyTrustWorkflow
{
    private readonly SshHostKeyTrustStore _store;

    public SshHostKeyTrustWorkflow(SshHostKeyTrustStore store) => _store = store;

    public async Task<SshHostKeyInfo> EnsureTrustedAsync(
        string host,
        int port,
        Func<CancellationToken, Task<SshHostKeyInfo>> probe,
        Func<SshHostKeyInfo, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var normalizedHost = SshHostKeyTrustStore.NormalizeHost(host);
        var existing = await _store.FindAsync(normalizedHost, port, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var candidate = await probe(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(normalizedHost, SshHostKeyTrustStore.NormalizeHost(candidate.Host), StringComparison.OrdinalIgnoreCase) ||
            candidate.Port != port)
        {
            throw new SshHostKeyTrustException("候选 SSH 主机密钥的地址与当前连接目标不一致，已拒绝信任。");
        }

        // 再读一次，避免两个窗口同时首次连接时后确认的流程覆盖先建立的信任。
        existing = await _store.FindAsync(normalizedHost, port, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!SshHostKeyTrustStore.Matches(existing, candidate))
            {
                throw new SshHostKeyChangedException(existing, candidate);
            }

            return existing;
        }

        if (!await confirm(candidate).ConfigureAwait(false))
        {
            throw new SshHostKeyTrustCancelledException(normalizedHost, port);
        }

        await _store.TrustAsync(candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }
}
