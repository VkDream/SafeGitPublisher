using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeGitPublisher.Models;

/// <summary>
/// 用户设置。持久化到 %LOCALAPPDATA%\SafeGitPublisher\settings.json。
/// </summary>
public sealed class AppSettings
{
    /// <summary>推荐 Git 作者名。</summary>
    public string RecommendedGitName { get; set; } = "VkDream";

    /// <summary>推荐 Git 作者邮箱（GitHub noreply 邮箱，避免暴露真实邮箱）。</summary>
    public string RecommendedGitEmail { get; set; } = "312913839+VkDream@users.noreply.github.com";

    /// <summary>大文件警告阈值（MB）。</summary>
    public double LargeFileWarningMB { get; set; } = 10;

    /// <summary>大文件高警告阈值（MB）。</summary>
    public double LargeFileHighWarningMB { get; set; } = 50;

    /// <summary>大文件阻断阈值（MB，对应 GitHub 100MB 限制）。</summary>
    public double LargeFileBlockingMB { get; set; } = 100;

    /// <summary>仓库总体积警告阈值（MB）。待提交全部文件合计超过该值时给出警告。</summary>
    public double RepoSizeWarningMB { get; set; } = 500;

    /// <summary>仓库总体积阻断阈值（MB）。待提交全部文件合计超过该值时阻断提交与推送。</summary>
    public double RepoSizeBlockingMB { get; set; } = 1000;

    /// <summary>提交前是否构建。</summary>
    public bool BuildBeforeCommit { get; set; } = true;

    /// <summary>是否要求图片脱敏人工确认。</summary>
    public bool RequireImagePrivacyConfirmation { get; set; } = true;

    /// <summary>复用历史设置：上次确认过的图片（本次会话内确认状态不持久化，占位置保留）。</summary>
    [JsonIgnore]
    public bool ImageConfirmed { get; set; }

    /// <summary>最近项目（最多 10 个）。</summary>
    public List<string> RecentProjects { get; set; } = new();

    /// <summary>
    /// 记录一次最近项目访问，去重并保留最新在前。
    /// </summary>
    public void AddRecentProject(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = path.Trim().TrimEnd('\\', '/');
        RecentProjects.RemoveAll(p => string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, normalized);
        if (RecentProjects.Count > 10) RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }

    /// <summary>内部使用：设置文件路径（便于测试注入）。</summary>
    [JsonIgnore]
    public string? StoragePathOverride { get; set; }

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppSettings>(json)!;
    }
}