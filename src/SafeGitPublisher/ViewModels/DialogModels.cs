using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.ViewModels;

/// <summary>
/// 最终确认页（确认提交并上传）的展示数据。
/// </summary>
public sealed class ConfirmPublishData : ViewModelBase
{
    public required string RepositoryRoot { get; init; }

    public required string ProjectPath { get; init; }

    public string RepoDisplay { get; init; } = "-";

    public string Branch { get; init; } = "-";

    public string RemoteDisplay { get; init; } = "-";

    /// <summary>实际 Push 目标的安全展示文本（凭据已脱敏）。</summary>
    public string PushUrlDisplay { get; init; } = "（未配置）";

    public string AuthorDisplay { get; init; } = "-";

    public string CommitMessage { get; init; } = string.Empty;

    /// <summary>仅上传已有提交时展示的完整提交 OID；普通提交模式不使用。</summary>
    public string CommitOidDisplay { get; init; } = "-";

    public int ChangeCount { get; init; }

    /// <summary>仅上传已有提交时，本次计划覆盖的待推送提交数。</summary>
    public int OutgoingCommitCount { get; init; }

    public int PassCount { get; init; }

    public int WarningCount { get; init; }

    public int BlockedCount { get; init; }

    private bool _imageConfirmed;

    /// <summary>本次发布对话中的图片脱敏确认；只由当前图片指纹消费。</summary>
    public bool ImageConfirmed
    {
        get => _imageConfirmed;
        set
        {
            if (SetProperty(ref _imageConfirmed, value))
            {
                OnPropertyChanged(nameof(ImageConfirmedText));
            }
        }
    }

    public bool HasNewImages { get; init; }

    /// <summary>最终确认页是否必须要求用户勾选图片脱敏确认。</summary>
    public bool RequiresImageConfirmation { get; init; }

    public string BuildDisplay { get; init; } = "-";

    /// <summary>分支尚无 upstream，将执行 git push -u origin &lt;branch&gt;。</summary>
    public bool WillSetUpstream { get; init; }

    /// <summary>true=仅提交；false=提交并上传。</summary>
    public bool CommitOnly { get; init; }

    /// <summary>
    /// true 表示本次确认只上传已经存在且完成安全复检的提交，
    /// 不读取提交说明，也不会再次执行 add 或 commit。
    /// </summary>
    public bool PushExistingOnly { get; init; }

    /// <summary>确认页标题，明确区分“创建提交”和“只上传已有提交”。</summary>
    public string DialogTitle => PushExistingOnly ? "确认上传已有提交" : "准备发布";

    /// <summary>确认页主操作文案。</summary>
    public string ConfirmButtonText => PushExistingOnly
        ? "确认仅上传"
        : CommitOnly ? "确认提交" : "确认提交并上传";

    /// <summary>本次操作的不可混淆说明。</summary>
    public string ActionSummary => PushExistingOnly
        ? "本次只上传已存在的待推送提交，不会再次暂存文件，也不会创建新提交。"
        : CommitOnly ? "本次只创建本地提交，不上传远端。" : "本次将创建提交并上传到核对后的 Push URL。";

    /// <summary>普通发布显示文件数；只上传模式明确说明不会处理工作区文件。</summary>
    public string ChangeDisplay => PushExistingOnly
        ? $"{OutgoingCommitCount} 个待推送提交；不会重新暂存或提交工作区文件"
        : $"{ChangeCount} 个文件";

    public string CommitMessageLabel => PushExistingOnly ? "操作" : "提交说明";

    public string SafetySectionTitle => PushExistingOnly ? "已有提交安全复检" : "安全检查";

    public bool ShowCheckCounts => !PushExistingOnly;

    public string ExistingPushSafetyNote => PushExistingOnly
        ? "待推送历史已完成安全复检；点击确认后还会再次核对仓库、HEAD、分支、远端目标和远端状态。"
        : string.Empty;

    /// <summary>图片确认文本根据普通发布/已有提交恢复场景使用准确对象描述。</summary>
    public string ImageConfirmationContent => PushExistingOnly
        ? "我已检查待推送历史中的图片，确认不含客户名称、内部项目名、用户名、邮箱或服务器地址。"
        : "我已检查本次新增/修改的图片，确认不含客户名称、内部项目名、用户名、邮箱或服务器地址。";

    public string ImageConfirmedText => !HasNewImages
        ? PushExistingOnly ? "待推送历史无图片" : "无新图片"
        : ImageConfirmed ? "已确认脱敏" : "未确认脱敏（禁止 Push）";

    public string UpstreamNote => WillSetUpstream
        ? $"分支 {Branch} 尚无 upstream，将推送到 origin 并设置 upstream。"
        : PushExistingOnly
            ? "将重新核对 HEAD、分支、远端目标和待推送历史后，只上传上述已有提交。"
            : "将使用已配置的 upstream 推送；请核对上方 Push URL。";
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
