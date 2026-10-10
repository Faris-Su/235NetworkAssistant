using System.IO;
using System.Security.Cryptography;
using RuijieNetworkAssistant.Models;
using RuijieNetworkAssistant.Services;

var root = Path.Combine(Path.GetTempPath(), "235-ssh-hostkey-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;

try
{
    await Run("首次确认后持久化公钥", FirstTrustIsPersistedAsync);
    await Run("已知公钥匹配且跳过探测/确认", KnownKeyIsAcceptedAsync);
    await Run("公钥变化判定并显示新旧指纹", ChangedKeyIsRejectedAsync);
    await Run("取消首次信任不写入记录", CancellationDoesNotTrustAsync);
    await Run("损坏信任库 fail closed 且不覆盖原文件", CorruptStoreFailsClosedAsync);
    await Run("管理操作可更新和删除信任记录", UpdateAndDeleteAsync);
    Console.WriteLine($"SSH 主机密钥测试：{passed}/6 通过。");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

return passed == 6 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    await test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

async Task FirstTrustIsPersistedAsync()
{
    var store = NewStore("first.json");
    var candidate = MakeKey("switch-a", 22, [1, 2, 3, 4]);
    var probeCalls = 0;
    var workflow = new SshHostKeyTrustWorkflow(store);
    var result = await workflow.EnsureTrustedAsync(
        "SWITCH-A.", 22,
        _ => { probeCalls++; return Task.FromResult(candidate); },
        _ => Task.FromResult(true));

    Assert(probeCalls == 1, "未知设备应先探测公钥。");
    Assert(ReferenceEquals(result, candidate), "流程应返回已确认的候选公钥。");
    var stored = await store.FindAsync("switch-a", 22);
    Assert(stored is not null && SshHostKeyTrustStore.Matches(candidate, stored), "确认后公钥应持久化。");
    Assert(!JsonContainsCredential(await File.ReadAllTextAsync(store.FilePath)), "信任库不应出现登录凭据字段。");
}

async Task KnownKeyIsAcceptedAsync()
{
    var store = NewStore("known.json");
    var key = MakeKey("10.0.0.2", 22, [5, 6, 7]);
    await store.TrustAsync(key);
    var probeCalls = 0;
    var confirmCalls = 0;
    var result = await new SshHostKeyTrustWorkflow(store).EnsureTrustedAsync(
        key.Host, key.Port,
        _ => { probeCalls++; return Task.FromResult(key); },
        _ => { confirmCalls++; return Task.FromResult(false); });

    Assert(SshHostKeyTrustPolicy.Evaluate(result, key) == SshHostKeyDecision.Match, "完全相同的公钥应匹配。");
    Assert(probeCalls == 0 && confirmCalls == 0, "已知设备正常连接前不应再探测或弹确认。");
}

Task ChangedKeyIsRejectedAsync()
{
    var oldKey = MakeKey("10.0.0.3", 22, [8, 9, 10]);
    var newKey = MakeKey("10.0.0.3", 22, [10, 9, 8]);
    Assert(SshHostKeyTrustPolicy.Evaluate(oldKey, newKey) == SshHostKeyDecision.Changed, "不同公钥必须判为变化。");
    var error = new SshHostKeyChangedException(oldKey, newKey);
    Assert(error.Message.Contains(oldKey.FingerprintSha256, StringComparison.Ordinal), "报错应包含旧指纹。");
    Assert(error.Message.Contains(newKey.FingerprintSha256, StringComparison.Ordinal), "报错应包含新指纹。");
    Assert(!SshHostKeyTrustStore.Matches(oldKey, newKey), "变化的公钥不得匹配。");
    return Task.CompletedTask;
}

async Task CancellationDoesNotTrustAsync()
{
    var store = NewStore("cancel.json");
    var candidate = MakeKey("10.0.0.4", 22, [11, 12, 13]);
    var authMayStart = false;
    var canceled = false;
    try
    {
        await new SshHostKeyTrustWorkflow(store).EnsureTrustedAsync(
            candidate.Host, candidate.Port,
            _ => Task.FromResult(candidate),
            _ => Task.FromResult(false));
        authMayStart = true;
    }
    catch (SshHostKeyTrustCancelledException)
    {
        canceled = true;
    }

    Assert(canceled && !authMayStart, "取消后不得进入后续真实认证流程。");
    Assert(await store.FindAsync(candidate.Host, candidate.Port) is null, "取消后不得保存信任记录。");
}

async Task CorruptStoreFailsClosedAsync()
{
    var path = Path.Combine(root, "corrupt.json");
    const string original = "{ not valid json";
    await File.WriteAllTextAsync(path, original);
    var store = NewStore("corrupt.json");
    var rejected = false;
    try { await store.GetAllAsync(); }
    catch (SshHostKeyTrustException) { rejected = true; }

    Assert(rejected, "损坏的信任库必须拒绝读取。");
    Assert(await File.ReadAllTextAsync(path) == original, "损坏文件应保留，不得被空库覆盖。");
}

async Task UpdateAndDeleteAsync()
{
    var store = NewStore("manage.json");
    var oldKey = MakeKey("10.0.0.5", 2222, [14, 15, 16]);
    var updated = MakeKey("10.0.0.5", 2222, [16, 15, 14]);
    await store.TrustAsync(oldKey);
    await store.TrustAsync(updated);
    var stored = await store.FindAsync(updated.Host, updated.Port);
    Assert(stored is not null && SshHostKeyTrustStore.Matches(updated, stored), "更新应替换指定主机公钥。");
    Assert(await store.RemoveAsync(updated.Host, updated.Port), "管理操作应能删除记录。");
    Assert(await store.FindAsync(updated.Host, updated.Port) is null, "删除后记录应不存在。");
}

SshHostKeyTrustStore NewStore(string name) => new(NullLog.Instance, Path.Combine(root, name));

static SshHostKeyInfo MakeKey(string host, int port, byte[] key)
    => new(host, port, "ssh-ed25519", 256, SshHostKeyTrustStore.Fingerprint(key), Convert.ToBase64String(key), DateTimeOffset.UtcNow);

static bool JsonContainsCredential(string json)
    => json.Contains("password", StringComparison.OrdinalIgnoreCase) ||
       json.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
       json.Contains("username", StringComparison.OrdinalIgnoreCase);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class NullLog : ILogService
{
    public static NullLog Instance { get; } = new();
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public IReadOnlyList<string> RecentEntries => Array.Empty<string>();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message, Exception? exception = null) { }
    public void Error(string message, Exception? exception = null) { }
}
