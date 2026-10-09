using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RuijieNetworkAssistant.Helpers;
using RuijieNetworkAssistant.Models;

namespace RuijieNetworkAssistant.Services;

/// <summary>设置读写。文件位于 %LocalAppData%\235NetworkAssistant\settings.json。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogService _log;
    private readonly string _filePath;

    /// <param name="log">日志服务。</param>
    /// <param name="filePath">自定义设置文件路径（自动化验证用；为空时用 %LocalAppData%）。</param>
    public SettingsService(ILogService log, string? filePath = null)
    {
        _log = log;
        _filePath = string.IsNullOrWhiteSpace(filePath) ? AppPaths.SettingsFile : filePath;
    }

    /// <summary>当前使用的设置文件路径。</summary>
    public string FilePath => _filePath;

    public async Task<AppSettings> LoadAsync()
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath).ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _log.Warn("读取设置失败，使用默认设置", ex);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(settings, Options);
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("保存设置失败", ex);
        }
    }
}
