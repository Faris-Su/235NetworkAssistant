namespace RuijieNetworkAssistant.Models;

/// <summary>SSH 服务端公开主机密钥信息；不包含任何登录凭据。</summary>
public sealed record SshHostKeyInfo(
    string Host,
    int Port,
    string Algorithm,
    int KeyLength,
    string FingerprintSha256,
    string PublicKeyBase64,
    DateTimeOffset ObservedAt)
{
    public byte[] GetPublicKeyBytes() => Convert.FromBase64String(PublicKeyBase64);
}

public class SshHostKeyTrustException : InvalidOperationException
{
    public SshHostKeyTrustException(string message) : base(message) { }

    public SshHostKeyTrustException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class SshHostKeyChangedException : SshHostKeyTrustException
{
    public SshHostKeyChangedException(SshHostKeyInfo trusted, SshHostKeyInfo presented)
        : base($"SSH 主机密钥与已信任记录不一致，已拒绝连接。\n" +
               $"设备：{presented.Host}:{presented.Port}\n" +
               $"原指纹：{trusted.FingerprintSha256}（{trusted.Algorithm}）\n" +
               $"新指纹：{presented.FingerprintSha256}（{presented.Algorithm}）\n" +
               "请通过 Console 或其他可信渠道核验设备密钥，再到【连接 → SSH → 管理已信任设备】更新记录。")
    {
        Trusted = trusted;
        Presented = presented;
    }

    public SshHostKeyInfo Trusted { get; }

    public SshHostKeyInfo Presented { get; }
}
