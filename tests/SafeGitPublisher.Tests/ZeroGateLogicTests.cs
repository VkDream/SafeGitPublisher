using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// Zero Change Gate 纯逻辑测试（PublishGateEvaluator，无 git/UI 依赖）。
/// 合同：0 个可提交变更时，无论提交说明是否填写，CanCommit=CanPush=false。
/// 覆盖验收场景：0 变更 + "test:" 不得解锁（人工验收 Bug 回归）。
/// </summary>
public static class ZeroGateLogicTests
{
    private static PreflightCheck Chk(string id, CheckStatus status, bool blocksCommit = false, bool blocksPush = false)
        => new() { Id = id, Name = id, Status = status, BlocksCommit = blocksCommit, BlocksPush = blocksPush };

    /// <summary>全部通过的报告（commit/push 均可）。</summary>
    private static PreflightReport OkReport() => new(new[]
    {
        Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("gitignore", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("secrets", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("remote", CheckStatus.Pass, blocksPush: true),
        Chk("build", CheckStatus.Pass, blocksPush: true),
        Chk("large_files", CheckStatus.Pass)
    });

    /// <summary>remote 未配置（阻断 Push 但不阻断 Commit）。</summary>
    private static PreflightReport NoRemoteReport() => new(new[]
    {
        Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("secrets", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("remote", CheckStatus.Blocked, blocksPush: true),
        Chk("build", CheckStatus.Pass, blocksPush: true)
    });

    /// <summary>存在 Commit 级阻断（secrets）。</summary>
    private static PreflightReport CommitBlockedReport() => new(new[]
    {
        Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("secrets", CheckStatus.Blocked, blocksCommit: true, blocksPush: true),
        Chk("remote", CheckStatus.Pass, blocksPush: true)
    });

    /// <summary>构建未通过（阻断 Push）。</summary>
    private static PreflightReport BuildBlockedReport() => new(new[]
    {
        Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("secrets", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("remote", CheckStatus.Pass, blocksPush: true),
        Chk("build", CheckStatus.Blocked, blocksPush: true)
    });

    /// <summary>带图片脱敏闸门的报告。</summary>
    private static PreflightReport ImageGateReport(CheckStatus status, bool blocksPush) => new(new[]
    {
        Chk("repo_detected", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("secrets", CheckStatus.Pass, blocksCommit: true, blocksPush: true),
        Chk("remote", CheckStatus.Pass, blocksPush: true),
        Chk("build", CheckStatus.Pass, blocksPush: true),
        Chk("image_privacy", status, blocksPush: blocksPush)
    });

    private static PublishGateEvaluator.GateResult Eval(
        PreflightReport? report, int changes, string message,
        bool busy = false, int newImages = 0, bool imageConfirmed = true, bool requireImageConfirmation = true)
        => PublishGateEvaluator.Evaluate(report, changes, message, busy, newImages, imageConfirmed, requireImageConfirmation);

    [Test]
    public static void ZG01_ZeroChanges_EmptyMessage_BlocksBoth()
    {
        var g = Eval(OkReport(), 0, string.Empty);
        Assert.True(!g.CanCommit, "0 变更 + 空说明 → CanCommit 必须 false");
        Assert.True(!g.CanPush, "0 变更 + 空说明 → CanPush 必须 false");
        Assert.Equal("当前没有可提交的变更", g.CommitReason);
    }

    [Test]
    public static void ZG02_ZeroChanges_NonEmptyMessage_StillBlocksBoth()
    {
        // 人工验收回归核心：0 变更时输入 "test: 1" 也不得解锁
        var g = Eval(OkReport(), 0, "test: 1");
        Assert.True(!g.CanCommit, "0 变更 + 非空说明 → CanCommit 必须 false");
        Assert.True(!g.CanPush, "0 变更 + 非空说明 → CanPush 必须 false");
        Assert.Equal("当前没有可提交的变更", g.CommitReason, "禁用原因应优先提示无变更");
    }

    [Test]
    public static void ZG03_WhitespaceMessage_Blocks()
    {
        var g = Eval(OkReport(), 1, "   ");
        Assert.True(!g.CanCommit, "全空白说明 → CanCommit 必须 false");
        Assert.Equal("请输入提交说明", g.CommitReason);
    }

    [Test]
    public static void ZG04_OneChange_ValidMessage_AllowsCommit()
    {
        var g = Eval(OkReport(), 1, "feat: x");
        Assert.True(g.CanCommit, "1 变更 + 有效说明 → CanCommit true");
        Assert.True(g.CanPush, "全部通过 → CanPush true");
        Assert.Equal(string.Empty, g.CommitReason);
        Assert.Equal(string.Empty, g.PushReason);
    }

    [Test]
    public static void ZG05_NoRemote_BlocksPushOnly()
    {
        var g = Eval(NoRemoteReport(), 1, "feat: x");
        Assert.True(g.CanCommit, "remote 仅阻断 Push，CanCommit 应为 true");
        Assert.True(!g.CanPush, "未配置 remote → CanPush false");
        Assert.True(g.PushReason.Contains("Remote"), $"PushReason 应提示 Remote，实际：{g.PushReason}");
    }

    [Test]
    public static void ZG06_NoReport_BlocksBoth()
    {
        var g = Eval(null, 1, "feat: x");
        Assert.True(!g.CanCommit, "未检查 → CanCommit false");
        Assert.True(!g.CanPush, "未检查 → CanPush false");
    }

    [Test]
    public static void ZG07_CommitBlock_BlocksBoth()
    {
        var g = Eval(CommitBlockedReport(), 1, "feat: x");
        Assert.True(!g.CanCommit, "Commit 级阻断 → CanCommit false");
        Assert.True(!g.CanPush, "Commit 级阻断 → CanPush false");
        Assert.Equal("存在阻断项，请先处理", g.CommitReason);
    }

    [Test]
    public static void ZG08_Busy_BlocksBoth()
    {
        var g = Eval(OkReport(), 1, "feat: x", busy: true);
        Assert.True(!g.CanCommit, "忙碌 → CanCommit false");
        Assert.True(!g.CanPush, "忙碌 → CanPush false");
        Assert.True(g.CommitReason.Contains("正在执行"), $"忙碌原因应提示稍候，实际：{g.CommitReason}");
    }

    [Test]
    public static void ZG09_ImageUnconfirmed_BlocksPushOnly()
    {
        var g = Eval(ImageGateReport(CheckStatus.Warning, blocksPush: true), 1, "feat: 图片",
            newImages: 2, imageConfirmed: false, requireImageConfirmation: true);
        Assert.True(g.CanCommit, "图片未确认仅阻断 Push，CanCommit 应为 true");
        Assert.True(!g.CanPush, "图片未确认 → CanPush false");
        Assert.True(g.PushReason.Contains("图片"), $"PushReason 应提示图片确认，实际：{g.PushReason}");
    }

    [Test]
    public static void ZG10_ImageConfirmed_AllowsPush()
    {
        // 模拟 VM 将确认状态写回报告（status=Pass、解除 BlocksPush）后的输入
        var g = Eval(ImageGateReport(CheckStatus.Pass, blocksPush: false), 1, "feat: 图片",
            newImages: 2, imageConfirmed: true, requireImageConfirmation: true);
        Assert.True(g.CanPush, "图片已确认 → CanPush true");
    }

    [Test]
    public static void ZG11_ImageConfirmationDisabled_NoBlocker()
    {
        var g = Eval(ImageGateReport(CheckStatus.Pass, blocksPush: false), 1, "feat: 图片",
            newImages: 2, imageConfirmed: false, requireImageConfirmation: false);
        Assert.True(g.CanPush, "设置关闭图片确认 → 不构成 Push 阻断");
    }

    [Test]
    public static void ZG12_BuildBlocked_BlocksPush()
    {
        var g = Eval(BuildBlockedReport(), 1, "feat: x");
        Assert.True(g.CanCommit, "构建仅阻断 Push，CanCommit 应为 true");
        Assert.True(!g.CanPush, "构建未通过 → CanPush false");
        Assert.True(g.PushReason.Contains("构建"), $"PushReason 应提示构建，实际：{g.PushReason}");
    }
}
