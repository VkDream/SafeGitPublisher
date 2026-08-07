using System.IO;
using System.Text.Json;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 用户设置持久化服务。存储到 %LOCALAPPDATA%\SafeGitPublisher\settings.json（不写入程序目录）。
/// </summary>
public sealed class SettingsService
{
    private readonly string _settingsPath;

    /// <summary>默认设置存储路径。</summary>
    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SafeGitPublisher",
            "settings.json");

    /// <param name="pathOverride">测试注入用；为 null 时使用默认路径。</param>
    public SettingsService(string? pathOverride = null)
    {
        _settingsPath = pathOverride ?? DefaultSettingsPath;
        OverridePath = _settingsPath;
    }

    /// <summary>当前设置文件路径（供 UI 展示）。</summary>
    public string SettingsPath => _settingsPath;

    /// <summary>暴露路径（供 AppSettings 使用）。</summary>
    private string OverridePath { get; }

    /// <summary>加载设置。文件不存在或损坏时返回默认值。</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    settings.StoragePathOverride = _settingsPath;
                    return settings;
                }
            }
        }
        catch
        {
            // 损坏时回退默认
        }

        var defaults = new AppSettings { StoragePathOverride = _settingsPath };
        return defaults;
    }

    /// <summary>保存设置。</summary>
    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_settingsPath) ?? string.Empty;
        Directory.CreateDirectory(dir);
        settings.StoragePathOverride = _settingsPath;
        var json = System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json, new System.Text.UTF8Encoding(false));
    }
}