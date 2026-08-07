using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// PublishBannerEvaluator 纯逻辑测试。
/// 合同：0 个可提交变更时，顶部必须显示 UP TO DATE（"当前没有可提交的变更"），
/// 不得因"针对待发布内容"的 Gate（Build/Image 等）显示 PUBLISH BLOCKED；
/// 但仓库级致命异常（git 不可用 / 非 Git 仓库 / 合并冲突）即使 0 变更也保留 Blocked。
/// </summary>
public static class BannerEvaluatorTests
{
    private static PreflightCheck Chk(string id, CheckStatus status, bool blocksCommit = false, bool blocksPush = false)
        => new() { Id = id, Name = id, Status = status, BlocksCommit = blocksCommit, BlocksPush = blocksPush };

    private static PreflightReport Report(params PreflightCheck[] checks) => new(checks);

    /// <summary>基础健康报告（仓库/状态/Secret/Remote/Build 全 Pass）。</summary>
    private static PreflightReport HealthyReport(params PreflightCheck[] extra)
    {
        var list = new List<PreflightCheck>
        {
            Chk("git_available", CheckStatus.Pass),
            Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
            Chk("status", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
            Chk("secret_scan", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
            Chk("remote", CheckStatus.Pass, blocksPush: true),
            Chk("build", CheckStatus.Pass, blocksPush: true)
        };
        list.AddRange(extra);
        return Report(list.ToArray());
    }

    // ---------- 0 个可提交变更 → UP TO DATE（核心合同） ----------

    [Test]
    public static void B01_NoReport_Hidden()
    {
        Assert.Equal(PublishBannerKind.Hidden, PublishBannerEvaluator.Evaluate(null, 0), "无报告不显示 Banner");
        Assert.Equal(PublishBannerKind.Hidden, PublishBannerEvaluator.Evaluate(null, 3), "无报告不显示 Banner（无论变更数）");
    }

    [Test]
    public static void B02_ZeroChanges_UpToDate()
    {
        var kind = PublishBannerEvaluator.Evaluate(HealthyReport(), 0);
        Assert.Equal(PublishBannerKind.UpToDate, kind, "0 变更 → UP TO DATE");
    }

    [Test]
    public static void B03_ZeroChanges_BuildBlocked_StillUpToDate()
    {
        // self-host 缺陷核心回归：0 变更 + Build Gate Blocked（如发布后 self-build 失败）
        // 顶部必须显示 UP TO DATE，不得 PUBLISH BLOCKED
        var report = HealthyReport(Chk("build", CheckStatus.Blocked, blocksPush: true));
        var kind = PublishBannerEvaluator.Evaluate(report, 0);
        Assert.Equal(PublishBannerKind.UpToDate, kind, "0 变更时 Build Blocked 不得显示 PUBLISH BLOCKED");
    }

    [Test]
    public static void B04_ZeroChanges_ImageGateBlocked_StillUpToDate()
    {
        var report = HealthyReport(Chk("image_privacy", CheckStatus.Warning, blocksPush: true));
        Assert.Equal(PublishBannerKind.UpToDate, PublishBannerEvaluator.Evaluate(report, 0),
            "0 变更时 Image Gate 不得显示 PUBLISH BLOCKED");
    }

    [Test]
    public static void B05_ZeroChanges_CommitBlocked_StillUpToDate()
    {
        var report = HealthyReport(Chk("secret_scan", CheckStatus.Blocked, blocksCommit: true, blocksPush: true));
        Assert.Equal(PublishBannerKind.UpToDate, PublishBannerEvaluator.Evaluate(report, 0),
            "0 变更时 Commit 级 Gate 也不得显示 PUBLISH BLOCKED（没有内容可发布）");
    }

    // ---------- 0 个可提交变更 + 仓库致命异常 → 仍 Blocked ----------

    [Test]
    public static void B06_ZeroChanges_Conflict_StillBlocked()
    {
        var report = HealthyReport(Chk("status", CheckStatus.Blocked, blocksCommit: true, blocksPush: true));
        Assert.Equal(PublishBannerKind.Blocked, PublishBannerEvaluator.Evaluate(report, 0),
            "合并冲突属于真实仓库异常，0 变更也必须保持 BLOCKED");
    }

    [Test]
    public static void B07_ZeroChanges_NotARepo_StillBlocked()
    {
        var report = Report(
            Chk("git_available", CheckStatus.Pass),
            Chk("repo_detected", CheckStatus.Blocked, blocksCommit: true, blocksPush: true));
        Assert.Equal(PublishBannerKind.Blocked, PublishBannerEvaluator.Evaluate(report, 0),
            "非 Git 仓库属于真实异常，0 变更也必须保持 BLOCKED");
    }

    [Test]
    public static void B08_ZeroChanges_GitUnavailable_StillBlocked()
    {
        var report = Report(Chk("git_available", CheckStatus.Blocked, blocksCommit: true, blocksPush: true));
        Assert.Equal(PublishBannerKind.Blocked, PublishBannerEvaluator.Evaluate(report, 0),
            "git 不可用属于环境致命异常，0 变更也必须保持 BLOCKED");
    }

    // ---------- 存在可提交变更 → 安全门禁照常（不削弱） ----------

    [Test]
    public static void B09_ChangesPresent_BuildBlocked_Blocked()
    {
        // 状态 A 合同：存在可提交变更 + Build FAIL → 仍必须 PUBLISH BLOCKED（安全不能削弱）
        var report = HealthyReport(Chk("build", CheckStatus.Blocked, blocksPush: true));
        Assert.Equal(PublishBannerKind.Blocked, PublishBannerEvaluator.Evaluate(report, 3), "有变更 + Build FAIL → BLOCKED");
    }

    [Test]
    public static void B10_ChangesPresent_AllPass_Ready()
    {
        Assert.Equal(PublishBannerKind.Ready, PublishBannerEvaluator.Evaluate(HealthyReport(), 1), "有变更 + 全 Pass → READY TO PUBLISH");
    }

    [Test]
    public static void B11_ChangesPresent_Warning_ReviewRequired()
    {
        var report = HealthyReport(Chk("git_identity", CheckStatus.Warning));
        Assert.Equal(PublishBannerKind.ReviewRequired, PublishBannerEvaluator.Evaluate(report, 1), "有变更 + Warning → REVIEW REQUIRED");
    }

    // ---------- 发布后刷新语义（TEST 6 纯逻辑部分） ----------

    [Test]
    public static void B12_PostPublishZeroChanges_UpToDate()
    {
        // 模拟成功发布后：真实 status=0 变更；即使上一轮报告残留 Build Blocked，也必须 UP TO DATE
        var staleReport = HealthyReport(Chk("build", CheckStatus.Blocked, blocksPush: true));
        Assert.Equal(PublishBannerKind.UpToDate, PublishBannerEvaluator.Evaluate(staleReport, 0),
            "发布后 0 变更 → 不得出现假 PUBLISH BLOCKED");
    }
}