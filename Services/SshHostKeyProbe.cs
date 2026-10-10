using Renci.SshNet;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 只获取候选主机公钥。HostKeyReceived 在 SSH user-auth 之前触发；本探测器始终拒绝该密钥，
/// 等 ConnectAsync 完成失败并释放 socket 后才把候选返回给调用方显示确认框。
/// 探测认证方法使用固定空凭据，真实账号密码不会传入此阶段。
/// </summary>
public sealed class SshHostKeyProbe
{
    public async Task<SshHostKeyInfo> ProbeAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("SSH 主机地址不能为空。", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "SSH 端口必须在 1 到 65535 之间。");
        }

        var username = "235-host-key-probe";
        var connectionInfo = new ConnectionInfo(
            host.Trim(),
            port,
            username,
            new AuthenticationMethod[] { new NoneAuthenticationMethod(username) })
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(2000, timeoutMs)),
        };

        SshHostKeyInfo? candidate = null;
        using var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, e) =>
        {
            candidate = new SshHostKeyInfo(
                SshHostKeyTrustStore.NormalizeHost(host),
                port,
                e.HostKeyName,
                e.KeyLength,
                "SHA256:" + e.FingerPrintSHA256,
                Convert.ToBase64String(e.HostKey),
                DateTimeOffset.UtcNow);
            e.CanTrust = false;
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Math.Max(2000, timeoutMs));
        try
        {
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new SshHostKeyTrustException($"读取 SSH 主机密钥超时（{host}:{port}）。", ex);
        }
        catch (Exception) when (candidate is not null)
        {
            // 预期路径：候选 key 被拒绝，SSH.NET 因此结束握手。此时 socket 已关闭，UI 才会提示用户。
            return candidate;
        }
        catch (Exception ex)
        {
            throw new SshHostKeyTrustException(
                $"无法读取 SSH 主机密钥（{host}:{port}）：{ex.Message}", ex);
        }

        // CanTrust=false 正常应使 ConnectAsync 在认证前失败；若库/服务器行为异常地继续，绝不把结果当作成功探测。
        throw new SshHostKeyTrustException(
            candidate is null
                ? $"SSH 主机 {host}:{port} 未触发主机密钥校验事件，已拒绝继续。"
                : $"SSH 主机 {host}:{port} 在探测阶段未按预期拒绝候选密钥，已关闭连接且不会信任该密钥。");
    }
}
