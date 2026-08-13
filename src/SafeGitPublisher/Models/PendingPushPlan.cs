namespace SafeGitPublisher.Models;

/// <summary>仅上传既有本地提交的准备结果。</summary>
public enum ExistingPushDisposition
{
    /// <summary>完整安全复检通过，可在本次计划有效期内执行仅上传。</summary>
    Ready,

    /// <summary>远端分支已经等于或包含当前本地提交，不得重复 Push。</summary>
    AlreadyUploaded,

    /// <summary>仓库尚无本地提交。</summary>
    NoLocalCommit,

    /// <summary>当前不是明确的本地分支。</summary>
    DetachedHead,

    /// <summary>本地与远端分叉，或远端状态相对安全计划发生变化。</summary>
    RemoteDrift,

    /// <summary>待推送历史触发 Secret、敏感路径、超大文件或图片确认阻断。</summary>
    Blocked,

    /// <summary>网络、Git 输出或对象关系无法可靠确认；禁止盲目重试 Push。</summary>
    Unknown
}

/// <summary>
/// 仅上传既有提交的公开安全计划。该对象可供 UI 展示和确认，但绝不包含精确 Remote URL、
/// URL 凭据或可用于绕过复检的内部状态；PlanId 只在当前服务实例内短期有效且单次消费。
/// </summary>
public sealed class ExistingPushPlan
{
    /// <summary>单次执行票据；仅 Ready 时非空。</summary>
    public string? PlanId { get; init; }

    public ExistingPushDisposition Disposition { get; init; }

    /// <summary>准备阶段确认的规范仓库根目录。</summary>
    public string RepositoryRoot { get; init; } = string.Empty;

    /// <summary>准备阶段锁定的本地分支。</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>准备阶段锁定并完成复检的完整提交 OID。</summary>
    public string? CommitOid { get; init; }

    public string? CommitShortHash => string.IsNullOrWhiteSpace(CommitOid) ? null : CommitOid[..Math.Min(8, CommitOid.Length)];

    /// <summary>准备阶段观察到的远端分支 OID；首次发布时为空。</summary>
    public string? RemoteOid { get; init; }

    /// <summary>已脱敏、可安全显示的 Remote 地址。</summary>
    public string RemoteDisplay { get; init; } = "（未配置）";

    /// <summary>精确 Remote 目标的 SHA-256 指纹；用于执行前比较，不可反推出 URL。</summary>
    public string RemoteTargetFingerprint { get; init; } = string.Empty;

    public int OutgoingCommitCount { get; init; }

    /// <summary>待推送提交树中是否存在常见图片对象；恢复旧版本提交时宁可保守确认。</summary>
    public bool HasOutgoingImages { get; init; }

    /// <summary>本次计划是否要求独立的图片隐私人工确认。</summary>
    public bool RequiresImageConfirmation { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool CanExecute => Disposition == ExistingPushDisposition.Ready && !string.IsNullOrWhiteSpace(PlanId);
}

/// <summary>发现并复检已有本地提交的请求；不会执行 add、commit、reset 或 Push。</summary>
public sealed class ExistingPushPrepareRequest
{
    public required string RepositoryRoot { get; init; }

    /// <summary>是否启用图片隐私确认策略。</summary>
    public bool RequireImageConfirmation { get; init; } = true;

    /// <summary>
    /// 调用方持有构建证明时传入该证明绑定的完整 commit OID。仅上传恢复不会检出或重建旧提交，
    /// 因此服务只接受与自己锁定的 HEAD 精确相同的 OID；空值不构成证明。
    /// </summary>
    public string? BuildVerifiedCommitOid { get; init; }

    /// <summary>true 时，没有当前锁定提交的构建证明即失败关闭。</summary>
    public bool RequireBuildVerification { get; init; }

    /// <summary>仓库总体积阻断阈值（MB，来自设置）。与预检 repo_size 检查同一合同。</summary>
    public double RepoSizeBlockingMB { get; init; } = 1000;
}

/// <summary>执行已经准备好的仅上传计划。</summary>
public sealed class ExistingPushExecuteRequest
{
    public required string PlanId { get; init; }

    public required string CommitOid { get; init; }

    public required string RemoteTargetFingerprint { get; init; }

    /// <summary>执行时再次传入构建验证策略，必须与准备计划一致。</summary>
    public bool RequireBuildVerification { get; init; }

    /// <summary>执行时再次传入策略开关，必须与准备计划一致。</summary>
    public bool RequireImageConfirmation { get; init; } = true;

    /// <summary>只代表用户在本次仅上传确认页作出的确认，不能复用主界面旧状态。</summary>
    public bool ImageConfirmed { get; init; }
}

/// <summary>Push 的最终可确认状态。</summary>
public enum PushDeliveryState
{
    None,

    /// <summary>提交已在本地，尚未开始 Push，可在重新安全准备后上传。</summary>
    Pending,

    /// <summary>执行前的票据、策略或仓库/远端状态校验阻断；没有启动 Push。</summary>
    Blocked,

    Pushed,
    AlreadyUploaded,

    /// <summary>Push 已启动或远端核验失败，是否接收不可确定，必须先 reconcile。</summary>
    Unknown
}
