using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 独立保存 SSH 公钥信任记录。文件只含公开主机密钥，不含账号、密码或私钥。
/// 损坏时拒绝读写，不回退为空列表，避免把已有主机降级为首次信任。
/// </summary>
public sealed class SshHostKeyTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ILogService _log;

    public SshHostKeyTrustStore(ILogService log, string? filePath = null)
    {
        _log = log;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? AppPaths.SshTrustedHostsFile : filePath;
    }

    public string FilePath => _filePath;

    public async Task<IReadOnlyList<SshHostKeyInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadUnsafe();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<SshHostKeyInfo?> FindAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var normalizedHost = NormalizeHost(host);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadUnsafe().SingleOrDefault(x =>
                string.Equals(NormalizeHost(x.Host), normalizedHost, StringComparison.OrdinalIgnoreCase) && x.Port == port);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task TrustAsync(SshHostKeyInfo key, CancellationToken cancellationToken = default)
    {
        Validate(key);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = LoadUnsafe().ToList();
            var index = entries.FindIndex(x =>
                string.Equals(NormalizeHost(x.Host), NormalizeHost(key.Host), StringComparison.OrdinalIgnoreCase) && x.Port == key.Port);
            if (index >= 0)
            {
                entries[index] = key;
            }
            else
            {
                entries.Add(key);
            }

            await SaveUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
            _log.Info($"SSH 主机密钥信任记录已保存：{key.Host}:{key.Port} {key.Algorithm} {key.FingerprintSha256}");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<bool> RemoveAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var normalizedHost = NormalizeHost(host);
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = LoadUnsafe().ToList();
            var removed = entries.RemoveAll(x =>
                string.Equals(NormalizeHost(x.Host), normalizedHost, StringComparison.OrdinalIgnoreCase) && x.Port == port) > 0;
            if (removed)
            {
                await SaveUnsafeAsync(entries, cancellationToken).ConfigureAwait(false);
                _log.Info($"SSH 主机密钥信任记录已删除：{host}:{port}");
            }

            return removed;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public static string NormalizeHost(string host) => host.Trim().Trim('[', ']').TrimEnd('.');

    public static string Fingerprint(byte[] publicKey)
        => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicKey)).TrimEnd('=');

    public static bool Matches(SshHostKeyInfo trusted, SshHostKeyInfo presented)
    {
        if (!string.Equals(trusted.Algorithm, presented.Algorithm, StringComparison.Ordinal) ||
            !string.Equals(NormalizeHost(trusted.Host), NormalizeHost(presented.Host), StringComparison.OrdinalIgnoreCase) ||
            trusted.Port != presented.Port)
        {
            return false;
        }

        try
        {
            var trustedKey = trusted.GetPublicKeyBytes();
            var presentedKey = presented.GetPublicKeyBytes();
            return trustedKey.Length == presentedKey.Length && CryptographicOperations.FixedTimeEquals(trustedKey, presentedKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private List<SshHostKeyInfo> LoadUnsafe()
    {
        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var entries = JsonSerializer.Deserialize<List<SshHostKeyInfo>>(json, JsonOptions)
                ?? throw new InvalidDataException("文件内容为空或根节点不是数组。");

            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry is null)
                {
                    throw new InvalidDataException("信任清单包含空记录。");
                }

                Validate(entry);
                var identity = $"{NormalizeHost(entry.Host)}\n{entry.Port}";
                if (!identities.Add(identity))
                {
                    throw new InvalidDataException($"设备 {entry.Host}:{entry.Port} 存在重复记录。");
                }
            }

            return entries;
        }
        catch (FileNotFoundException)
        {
            return new List<SshHostKeyInfo>();
        }
        catch (DirectoryNotFoundException)
        {
            return new List<SshHostKeyInfo>();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            _log.Error($"SSH 主机密钥信任库损坏或不可读取：{_filePath}", ex);
            throw new SshHostKeyTrustException(
                $"SSH 信任记录无法安全读取，已拒绝连接且没有覆盖原文件。\n文件：{_filePath}\n原因：{ex.Message}", ex);
        }
    }

    private async Task SaveUnsafeAsync(List<SshHostKeyInfo> entries, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new IOException("SSH 信任库路径没有有效目录。");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(temporaryPath); } catch { }
            _log.Error("保存 SSH 主机密钥信任记录失败。", ex);
            throw new SshHostKeyTrustException($"保存 SSH 信任记录失败：{ex.Message}", ex);
        }
    }

    private static void Validate(SshHostKeyInfo entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Host) || entry.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(entry.Algorithm) || entry.KeyLength < 1 ||
            string.IsNullOrWhiteSpace(entry.FingerprintSha256) || string.IsNullOrWhiteSpace(entry.PublicKeyBase64))
        {
            throw new InvalidDataException("记录缺少主机、端口、算法、指纹或公钥字段。");
        }

        var key = entry.GetPublicKeyBytes();
        if (key.Length == 0 || !string.Equals(Fingerprint(key), entry.FingerprintSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"设备 {entry.Host}:{entry.Port} 的公钥与 SHA256 指纹不匹配。");
        }
    }
}
