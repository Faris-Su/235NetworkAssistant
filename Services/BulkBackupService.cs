using System.Diagnostics;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>
/// 批量备份：按资源库选一批设备，**顺序**逐台 Telnet 连上、读 running-config、存文件、断开。
///
/// 为什么必须顺序：<see cref="ConnectionService"/> 一次只持有一条连接，而且**同时连几十台设备**本身
/// 就是运维大忌（设备侧 vty 会被占满，现场连自己的会话都挤不进去）。顺序执行慢一点，但稳。
///
/// 为什么要有"先试一台"：用户名/密码是**整批共用**的。如果密码填错，
/// 顺序跑下去就是拿一个错密码去撞几百台设备 —— 很多设备有登录失败锁定，
/// 后果是**把运维账号在几百台设备上锁掉**。所以第一台失败就整批停下，让人先确认凭据。
/// </summary>
public sealed class BulkBackupService
{
    private readonly ConnectionService _connections;
    private readonly BackupService _backup;
    private readonly ILogService? _log;

    public BulkBackupService(ConnectionService connections, BackupService backup, ILogService? log = null)
    {
        _connections = connections;
        _backup = backup;
        _log = log;
    }

    /// <summary>
    /// 备份一台。返回结果而不是抛异常 —— 批量场景里"某一台失败"是常态，不能中断整批
    /// （唯一的例外是**第一台就失败**，由调用方决定要不要继续）。
    /// </summary>
    public async Task<BulkBackupItem> BackupOneAsync(
        DeviceTarget target,
        TelnetConnectionSettings settings,
        string enablePassword,
        string directory,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            // 每台都要用自己的 IP；**不能复用上一台的会话**（那会把配置存成上一台的文件名）
            settings.Host = target.Ip;
            var privilege = new PrivilegeRequest(AppServices.Settings.PrivilegeMode, enablePassword);

            await _connections.ConnectTelnetAsync(settings, cancellationToken, privilege).ConfigureAwait(false);
            if (!_connections.IsConnected)
            {
                watch.Stop();
                return Failed(target, "连不上", _connections.LastError ?? "连接没有建立", watch);
            }

            // running-config 是**特权模式**命令：普通模式必然被拒，这里如实报出来而不是当成"读取失败"
            if (_connections.PrivilegeLevel != DevicePrivilegeLevel.Privileged)
            {
                watch.Stop();
                return Failed(target, "权限不足", "只进到普通模式（>）：请确认 Enable 密码，或该账号是否有权限。", watch);
            }

            var outcome = await _backup.BackupRunningConfigAsync(directory, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            return new BulkBackupItem
            {
                Ip = target.Ip,
                Name = target.Name,
                Building = target.Building,
                Status = "成功",
                FilePath = outcome.FilePath,
                IncompleteReason = outcome.PossiblyIncomplete ? outcome.IncompleteReason ?? "内容可能不完整" : string.Empty,
                ElapsedMs = watch.ElapsedMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            watch.Stop();
            _log?.Warn($"批量备份：{target.Ip} 失败", ex);
            return Failed(target, "连不上", ex.Message, watch);
        }
        finally
        {
            try
            {
                await _connections.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Warn($"批量备份：断开 {target.Ip} 时出错（不影响已完成的备份）", ex);
            }
        }
    }

    private static BulkBackupItem Failed(DeviceTarget target, string status, string error, Stopwatch watch) =>
        new()
        {
            Ip = target.Ip,
            Name = target.Name,
            Building = target.Building,
            Status = status,
            Error = error,
            ElapsedMs = watch.ElapsedMilliseconds,
        };
}
