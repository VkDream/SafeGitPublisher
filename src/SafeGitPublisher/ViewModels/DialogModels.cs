using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.ViewModels;

/// <summary>
/// 最终确认页（确认提交并上传）的展示数据。
/// </summary>
public sealed class ConfirmPublishData
{
    public required string RepositoryRoot { get; init; }

    public required string ProjectPath { get; init; }

    public string RepoDisplay { get; init; } = "-";

    public string Branch { get; init; } = "-";

    public string RemoteDisplay { get; init; } = "-";

    public string AuthorDisplay { get; init; } = "-";

    public string CommitMessage { get; init; } = string.Empty;

    public int ChangeCount { get; init; }

    public int PassCount { get; init; }

    public int WarningCount { get; init; }

    public int BlockedCount { get; init; }

    public bool ImageConfirmed { get; init; }

    public bool HasNewImages { get; init; }

    public string BuildDisplay { get; init; } = "-";

    /// <summary>分支尚无 upstream，将执行 git push -u origin &lt;branch&gt;。</summary>
    public bool WillSetUpstream { get; init; }

    /// <summary>true=仅提交；false=提交并上传。</summary>
    public bool CommitOnly { get; init; }

    public string ImageConfirmedText => !HasNewImages ? "无新图片" : (ImageConfirmed ? "已确认脱敏" : "未确认脱敏（禁止 Push）");

    public string UpstreamNote => WillSetUpstream
        ? $"分支 {Branch} 尚无 upstream，将执行：git push -u origin {Branch}"
        : "将使用已配置的 upstream 直接推送。";
}

/// <summary>
/// 设置 origin 对话框的数据。
/// </summary>
public sealed class SetOriginData
{
    /// <summary>当前 remote 名称（通常 origin）。</summary>
    public string RemoteName { get; init; } = "origin";

    /// <summary>当前 URL（已存在时显示）。</summary>
    public string? CurrentUrl { get; init; }

    /// <summary>推荐默认值。</summary>
    public string SuggestedUrl { get; init; } = string.Empty;

    /// <summary>用户确认后的 URL。取消时为 null。</summary>
    public string? ResultUrl { get; set; }

    /// <summary>用户是否确认更新已有 origin。</summary>
    public bool ConfirmReplace { get; set; }
}

/// <summary>
/// .gitignore 预览对话框数据。
/// </summary>
public sealed class GitignorePreviewData
{
    public required string RepoRoot { get; init; }

    public required string NewContent { get; init; }

    public bool Confirmed { get; set; }
}

/// <summary>
/// 首次发布向导的计划与结果。
/// </summary>
public sealed class WizardData
{
    public required string ProjectPath { get; init; }

    public string CommitMessage { get; set; } = string.Empty;

    public bool InitGit { get; set; } = true;

    public bool GenerateGitignore { get; set; } = true;

    public bool SetIdentity { get; set; } = true;

    public bool SetOrigin { get; set; } = true;

    public string OriginUrl { get; set; } = string.Empty;

    /// <summary>用户是否确认执行向导（false = 取消）。</summary>
    public bool Confirmed { get; set; }
}

/// <summary>
/// 设置对话框数据（直接绑定 AppSettings）。
/// </summary>
public sealed class SettingsData
{
    public required AppSettings Settings { get; init; }

    public required string SettingsPath { get; init; }

    public bool Saved { get; set; }
}

/// <summary>
/// 详细报告数据。
/// </summary>
public sealed class ReportData
{
    public required PreflightContext Context { get; init; }
}