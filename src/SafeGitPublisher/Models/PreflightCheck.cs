namespace SafeGitPublisher.Models;

/// <summary>
/// 发布前检查的单个检查项。
/// </summary>
public sealed class PreflightCheck
{
    /// <summary>稳定标识，用于 UI 按钮回调和日志记录。</summary>
    public required string Id { get; init; }

    /// <summary>检查项名称（中文，显示用）。</summary>
    public required string Name { get; init; }

    /// <summary>检查状态。</summary>
    public CheckStatus Status { get; set; } = CheckStatus.Info;

    /// <summary>一行摘要，例如 “3 个变更”“2 张图片需确认”。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>详细说明，用于详细报告。</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>建议修复操作的按钮文字。为空则界面不显示修复按钮。</summary>
    public string FixLabel { get; set; } = string.Empty;

    /// <summary>该检查阻止提交（git add/commit）。</summary>
    public bool BlocksCommit { get; set; }

    /// <summary>该检查阻止 Push。不会阻止“仅提交”。</summary>
    public bool BlocksPush { get; set; }

    /// <summary>是否属于必须人工确认才能放行的类别（如图片脱敏）。</summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>状态中文文本（UI 徽章显示；颜色与图标由转换器按 Status 提供）。</summary>
    public string StatusText => Status switch
    {
        CheckStatus.Pass => "通过",
        CheckStatus.Warning => "警告",
        CheckStatus.Blocked => "阻断",
        _ => "提示"
    };
}