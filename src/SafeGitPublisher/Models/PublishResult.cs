namespace SafeGitPublisher.Models;

/// <summary>
/// 发布流程执行结果（PublishWorkflowService）。
/// </summary>
public sealed class PublishResult
{
    public bool Committed { get; init; }

    /// <summary>
    /// Git 确认 HEAD 已变化，但该提交未通过完整安全合同或结果不可确定。
    /// UI 必须告警而不得显示“提交成功”。
    /// </summary>
    public bool CommitCreatedButUnverified { get; init; }

    public bool Pushed { get; init; }

    /// <summary>
    /// 兼容旧 UI 的中止标志。新流程不会简单清空暂存区，而是恢复到操作前的 index 快照。
    /// </summary>
    public bool UnstagedAfterBlocked { get; init; }

    /// <summary>安全中止后是否已成功恢复操作前的 index（含用户原有部分暂存状态）。</summary>
    public bool IndexRestoredAfterAbort { get; init; }

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
