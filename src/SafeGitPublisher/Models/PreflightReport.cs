namespace SafeGitPublisher.Models;

/// <summary>
/// 发布前检查综合结果：检查项列表 + CanCommit / CanPush 决策。
/// </summary>
public sealed class PreflightReport
{
    public PreflightReport()
    {
    }

    public PreflightReport(IEnumerable<PreflightCheck> checks)
    {
        Checks.AddRange(checks);
    }

    public List<PreflightCheck> Checks { get; } = new();

    /// <summary>是否存在任何 BlocksCommit=true 的检查项（仅 Blocked 状态阻断提交）。</summary>
    public bool HasCommitBlock => Checks.Any(c => c.BlocksCommit && c.Status == CheckStatus.Blocked);

    /// <summary>
    /// 是否存在阻断 Push 的检查项。
    /// 注意：标记了 BlocksPush 且状态非 Pass/Info 的检查都阻断 Push，
    /// 包括 Warning 级但硬性拦截的项（如未配置 origin、图片未确认脱敏）。
    /// </summary>
    public bool HasPushBlock => Checks.Any(c => c.BlocksPush && c.Status != CheckStatus.Pass && c.Status != CheckStatus.Info);

    public bool HasWarning => Checks.Any(c => c.Status == CheckStatus.Warning && !c.BlocksCommit && !c.BlocksPush);

    public bool CanCommit => !HasCommitBlock;

    public bool CanPush => !HasPushBlock;

    public int PassCount => Checks.Count(c => c.Status == CheckStatus.Pass);
    public int InfoCount => Checks.Count(c => c.Status == CheckStatus.Info);
    public int WarningCount => Checks.Count(c => c.Status == CheckStatus.Warning);
    public int BlockedCount => Checks.Count(c => c.Status == CheckStatus.Blocked);

    public PreflightReport Copy()
    {
        var copy = new PreflightReport { };
        copy.Checks.Clear();
        foreach (var c in Checks)
        {
            copy.Checks.Add(new PreflightCheck
            {
                Id = c.Id,
                Name = c.Name,
                Status = c.Status,
                Summary = c.Summary,
                Details = c.Details,
                FixLabel = c.FixLabel,
                BlocksCommit = c.BlocksCommit,
                BlocksPush = c.BlocksPush,
                RequiresConfirmation = c.RequiresConfirmation
            });
        }
        return copy;
    }
}