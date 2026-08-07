using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>顶部发布状态 Banner 的展示种类。</summary>
public enum PublishBannerKind
{
    /// <summary>无报告（不显示 Banner）。</summary>
    Hidden,
    /// <summary>存在阻断性问题（真实待发布内容或仓库致命异常）。</summary>
    Blocked,
    /// <summary>无待提交内容（0 个可提交变更）——不是安全阻断。</summary>
    UpToDate,
    /// <summary>有待确认项（Warning），复核后可继续。</summary>
    ReviewRequired,
    /// <summary>可以安全提交。</summary>
    Ready
}

/// <summary>
/// 修复合同：0 个可提交变更时，顶部不得因"针对待发布内容"的 Gate（Build/Image/Commit Message 等）
/// 显示 PUBLISH BLOCKED；但仓库级致命异常（git 不可用 / 非 Git 仓库 / 合并冲突）即使 0 变更也保留 Blocked。
/// 纯函数，无 UI 依赖，可单元测试。
/// </summary>
public static class PublishBannerEvaluator
{
    /// <summary>仓库级致命检查项：即使 0 个可提交变更也必须以错误状态提示用户。</summary>
    private static readonly string[] RepoFatalCheckIds = { "git_available", "repo_detected", "status" };

    /// <summary>
    /// 计算发布状态 Banner。
    /// </summary>
    /// <param name="report">最近一次预检报告；null 表示不显示 Banner。</param>
    /// <param name="committableChangeCount">当前真正可提交的变更数（非冲突）。</param>
    public static PublishBannerKind Evaluate(PreflightReport? report, int committableChangeCount)
    {
        if (report == null) return PublishBannerKind.Hidden;

        // 1) 仓库级致命异常优先（即使 0 变更也属于真实异常，需要用户看到）
        if (report.Checks.Any(c => RepoFatalCheckIds.Contains(c.Id) && c.Status == CheckStatus.Blocked))
        {
            return PublishBannerKind.Blocked;
        }

        // 2) 0 个可提交变更 → UP TO DATE（“没有东西需要发布”，不是“被安全阻断”）
        if (committableChangeCount <= 0)
        {
            return PublishBannerKind.UpToDate;
        }

        // 3) 只有存在待发布内容时，才用安全 Gate 判定阻断
        if (report.HasCommitBlock || report.HasPushBlock) return PublishBannerKind.Blocked;
        if (report.HasWarning) return PublishBannerKind.ReviewRequired;

        return PublishBannerKind.Ready;
    }
}