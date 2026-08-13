using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 发布门控决策核心（纯函数，无 UI 依赖，可单测）。
/// 集中定义 CanCommit / CanPush 的全部规则与按钮禁用原因文案。
/// </summary>
public static class PublishGateEvaluator
{
    public sealed record GateResult(bool CanCommit, bool CanPush, string CommitReason, string PushReason);

    /// <summary>
    /// 计算发布门控。
    /// </summary>
    /// <param name="report">最近一次预检报告；null 表示尚未检查。</param>
    /// <param name="committableChanges">当前真正可提交的变更数（非冲突条目）。</param>
    /// <param name="commitMessage">提交说明（未 Trim）。</param>
    /// <param name="busy">是否有运行中的检查/构建/发布流程。</param>
    /// <param name="newImageCount">本次新增/修改图片数（决定图片确认是否需要）。</param>
    /// <param name="imageConfirmed">图片脱敏是否已确认。</param>
    /// <param name="requireImageConfirmation">设置：是否启用图片脱敏人工确认。</param>
    public static GateResult Evaluate(
        PreflightReport? report,
        int committableChanges,
        string commitMessage,
        bool busy,
        int newImageCount,
        bool imageConfirmed,
        bool requireImageConfirmation)
    {
        var hasChanges = committableChanges > 0;
        var messageOk = !string.IsNullOrWhiteSpace(commitMessage);

        // ---- CanCommit 规则（合同第三条）----
        // 1) 已检查（report != null）；2) 无 Commit 级 BLOCKED；3) 提交说明非空；
        // 4) 存在至少 1 个可提交变更；5) 无运行中的检查/构建/发布流程。
        var commitBlocked = report == null || report.HasCommitBlock;
        var canCommit = !commitBlocked && hasChanges && messageOk && !busy;

        // ---- CanPush 规则（合同第三条）----
        // CanCommit 是 CanPush 的必要条件；再叠加 Push Gate 阻断。
        var pushBlocked = report == null || report.HasPushBlock;
        var canPush = canCommit && !pushBlocked;

        // ---- 禁用原因文案（Tooltip，优先级从高到低）----
        var commitReason = busy ? "正在执行检查 / 发布流程，请稍候"
            : !hasChanges ? "当前没有可提交的变更"
            : !messageOk ? "请输入提交说明"
            : commitBlocked ? "存在阻断项，请先处理"
            : string.Empty;

        var pushReason = busy ? "正在执行检查 / 发布流程，请稍候"
            : !hasChanges ? "当前没有可提交的变更"
            : !messageOk ? "请输入提交说明"
            : commitBlocked ? "存在阻断项，请先处理"
            : !canPush ? ComputePushGateReason(report, newImageCount, imageConfirmed, requireImageConfirmation)
            : string.Empty;

        return new GateResult(canCommit, canPush, commitReason, pushReason);
    }

    /// <summary>计算 Push 被阻断的具体原因（仅当 CanCommit 已通过时使用）。</summary>
    private static string ComputePushGateReason(
        PreflightReport? report, int newImageCount, bool imageConfirmed, bool requireImageConfirmation)
    {
        if (report == null) return "尚未完成发布前检查";

        if (requireImageConfirmation && newImageCount > 0 && !imageConfirmed)
        {
            return "新增图片未确认脱敏，禁止 Push";
        }

        var pushCheck = report.Checks.FirstOrDefault(c => c.BlocksPush && c.Status != CheckStatus.Pass && c.Status != CheckStatus.Info);
        if (pushCheck != null)
        {
            return pushCheck.Id switch
            {
                "remote" => "未配置 Remote，无法 Push",
                "build" => "构建未通过，禁止 Push",
                "branch" => "当前分支状态不允许 Push",
                "image_privacy" => "新增图片未确认脱敏，禁止 Push",
                _ => "存在阻断 Push 的检查项，请先处理"
            };
        }

        return "存在阻断项，请先处理";
    }
}
