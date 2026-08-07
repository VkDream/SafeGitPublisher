namespace SafeGitPublisher.Models;

/// <summary>
/// 发布流程执行结果（PublishWorkflowService）。
/// </summary>
public sealed class PublishResult
{
    public bool Committed { get; init; }

    public bool Pushed { get; init; }

    /// <summary>是否因 staged 复检发现 BLOCKED 而执行了 git reset 并中止。</summary>
    public bool UnstagedAfterBlocked { get; init; }

    /// <summary>
    /// true 表示本次中止属于“非异常”提示（如工作区无变更），
    /// UI 应以 INFO 日志与轻提示呈现，不显示 ERROR 红叉。
    /// </summary>
    public bool Informational { get; init; }

    /// <summary>取消原因（用户取消时非空）。</summary>
    public bool Canceled { get; init; }

    /// <summary>失败原因描述。</summary>
    public string? Error { get; init; }

    /// <summary>是否使用了 git push -u 设置 upstream。</summary>
    public bool UsedSetUpstream { get; init; }

    /// <summary>实际提交的短哈希（成功时）。</summary>
    public string? CommitShortHash { get; init; }
}