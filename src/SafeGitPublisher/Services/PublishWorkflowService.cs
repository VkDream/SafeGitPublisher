using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 提交流程执行器。严格按任务书顺序：
/// 1) 重新获取状态 → 2) 关键安全检查（快速复检）→ 3) git add --all →
/// 4) diff --cached --name-status → 5) staged 内容复扫（发现 BLOCKED 则 git reset 中止）→
/// 6) git commit → 7) 仅提交则结束 → 8) push（无 upstream 时 push -u origin 分支）。
/// </summary>
public sealed class PublishWorkflowService
{
    private readonly GitService _git;
    private readonly SensitiveFileScanner _sensitiveScanner;
    private readonly SecretScanner _secretScanner;
    private readonly LargeFileScanner _largeFileScanner;

    public PublishWorkflowService(
        GitService git,
        SensitiveFileScanner sensitiveScanner,
        SecretScanner secretScanner,
        LargeFileScanner largeFileScanner)
    {
        _git = git;
        _sensitiveScanner = sensitiveScanner;
        _secretScanner = secretScanner;
        _largeFileScanner = largeFileScanner;
    }

    public enum PublishMode
    {
        CommitOnly,
        CommitAndPush
    }

    public sealed class PublishRequest
    {
        public required string RepositoryRoot { get; init; }

        public required string CommitMessage { get; init; }

        public PublishMode Mode { get; init; } = PublishMode.CommitAndPush;

        /// <summary>新图片人工确认脱敏状态。</summary>
        public bool ImageConfirmed { get; init; }

        /// <summary>图片隐私确认是否启用（来自设置）。</summary>
        public bool RequireImageConfirmation { get; init; } = true;
    }

    /// <summary>
    /// 执行提交/发布流程。
    /// </summary>
    public async Task<PublishResult> ExecuteAsync(PublishRequest request, Action<LogLevel, string>? log = null, CancellationToken ct = default)
    {
        var root = request.RepositoryRoot;
        var msg = request.CommitMessage.Trim();
        if (string.IsNullOrEmpty(msg))
        {
            return new PublishResult { Error = "Commit Message 不能为空。" };
        }

        // ---------- 1) 重新获取状态 ----------
        log?.Invoke(LogLevel.Info, "步骤 1/8  重新获取工作区状态…");
        var status = await _git.StatusPorcelainAsync(root, ct);
        var changes = GitRepositoryInspector.ParseStatusPorcelain(status.Stdout);
        var conflicts = changes.Where(c => c.IsConflict).ToList();
        if (conflicts.Count > 0)
        {
            return new PublishResult { Error = $"存在未解决的合并冲突：{string.Join("、", conflicts.Select(c => c.Path))}，已中止发布。" };
        }

        // ---------- Zero Change 兜底：实际可提交变更数为 0 时立即停止 ----------
        // 不进入 git add / commit / push；无变更不是异常，按提示处理。
        var committable = changes.Where(c => !c.IsConflict).ToList();
        if (committable.Count == 0)
        {
            log?.Invoke(LogLevel.Info, "当前工作区没有可提交的变更。");
            return new PublishResult { Informational = true, Error = "当前工作区没有可提交的变更。" };
        }

        // ---------- 2) 关键安全检查（快速复检，不重复构建） ----------
        log?.Invoke(LogLevel.Info, "步骤 2/8  重新执行关键安全检查…");
        var safety = await QuickSafetyCheckAsync(root, changes, ct);
        if (safety.Count > 0)
        {
            foreach (var m in safety) log?.Invoke(LogLevel.Blocked, m);
            return new PublishResult { Error = string.Join("\n", safety) };
        }

        // ---------- 3) git add --all ----------
        log?.Invoke(LogLevel.Info, "步骤 3/8  git add --all …");
        var addResult = await _git.AddAllAsync(root, ct);
        if (!addResult.Success)
        {
            return new PublishResult { Error = $"git add 失败：{addResult.StdErrText}" };
        }
        log?.Invoke(LogLevel.Pass, "git add 完成");

        // ---------- 4) 已暂存文件清单 ----------
        log?.Invoke(LogLevel.Info, "步骤 4/8  读取已暂存文件…");
        var cached = await _git.DiffCachedNameStatusAsync(root, ct);
        var staged = GitRepositoryInspector.ParseDiffCachedNameStatus(cached.Stdout);
        if (staged.Count == 0)
        {
            return new PublishResult { Error = "没有可提交的变更（暂存区为空）。" };
        }

        // ---------- 5) 已暂存内容复检（最终闸门） ----------
        log?.Invoke(LogLevel.Info, "步骤 5/8  扫描已暂存内容…");
        var stagedSecret = await _secretScanner.ScanFilesAsync(root, staged.Select(c => c.Path), ct);
        var stagedBlockedSecrets = stagedSecret.Findings.Where(f => f.Severity == ScanSeverity.Blocked).ToList();

        var stagedSensitive = staged
            .Where(c => SensitiveFileRules.IsBlockedPath(c.Path))
            .Select(c => new ScanFinding(c.Path, "sensitive-file", ScanSeverity.Blocked, SensitiveFileRules.BlockReason(c.Path)))
            .ToList();

        var stagedLargeBlocked = _largeFileScanner.Scan(root, staged)
            .Where(f => f.Severity == ScanSeverity.Blocked)
            .ToList();

        // 图片未确认 → 阻断 push；仅提交不受影响
        var imagesStaged = staged.Where(c => c.IsImage).ToList();
        var imageGate = false;
        if (request.RequireImageConfirmation && imagesStaged.Count > 0 && !request.ImageConfirmed)
        {
            imageGate = true;
        }

        if (stagedBlockedSecrets.Count > 0 || stagedSensitive.Count > 0 || stagedLargeBlocked.Count > 0)
        {
            log?.Invoke(LogLevel.Blocked, "已暂存内容存在 BLOCKED 项，执行 git reset 取消暂存并中止。");
            await _git.ResetToUnstageAsync(root, ct);
            var reasons = new List<string>();
            reasons.AddRange(stagedBlockedSecrets.Select(f => $"{f.File}（第{f.Line}行）{f.Preview}"));
            reasons.AddRange(stagedSensitive.Select(f => $"{f.File}：{f.Message}"));
            reasons.AddRange(stagedLargeBlocked.Select(f => $"{f.File}：{f.Message}"));
            return new PublishResult
            {
                UnstagedAfterBlocked = true,
                Error = "已暂存内容触发安全阻断，已执行 git reset 取消暂存：\n" + string.Join("\n", reasons)
            };
        }

        if (imageGate && request.Mode == PublishMode.CommitAndPush)
        {
            log?.Invoke(LogLevel.Blocked, "本次包含新图片且未确认脱敏，禁止 Push。");
            await _git.ResetToUnstageAsync(root, ct);
            return new PublishResult
            {
                UnstagedAfterBlocked = true,
                Error = "本次提交包含图片，请在确认图片已脱敏后再进行“安全提交并上传”。"
            };
        }

        // ---------- 6) git commit ----------
        log?.Invoke(LogLevel.Info, "步骤 6/8  git commit …");
        var commit = await _git.CommitAsync(root, msg, ct);
        if (!commit.Success && commit.ExitCode != 1)
        {
            // 退出码 1 表示“无变更可提交”（理论上不会出现，因为已暂存）
            return new PublishResult { Error = $"git commit 失败：{commit.StdErrText}  {commit.StdOutText}" };
        }
        var shortHash = await _git.HeadShortAsync(root, ct);
        log?.Invoke(LogLevel.Pass, $"已提交：{shortHash}  {msg}");

        if (request.Mode == PublishMode.CommitOnly)
        {
            return new PublishResult { Committed = true, CommitShortHash = shortHash };
        }

        // ---------- 7/8) push ----------
        var branch = await _git.CurrentBranchAsync(root, ct);
        if (string.IsNullOrWhiteSpace(branch))
        {
            return new PublishResult { Committed = true, CommitShortHash = shortHash, Error = "已提交，但当前处于 detached HEAD，跳过推送。" };
        }

        var hasUpstream = await _git.HasUpstreamAsync(root, ct);
        if (!hasUpstream)
        {
            log?.Invoke(LogLevel.Info, $"分支 {branch} 尚无 upstream，将执行：git push -u origin {branch}");
        }
        else
        {
            log?.Invoke(LogLevel.Info, "开始 git push …");
        }

        var pushResult = hasUpstream
            ? await _git.PushAsync(root, ct)
            : await _git.PushSetUpstreamAsync(root, branch, ct);

        if (pushResult.Canceled)
        {
            return new PublishResult { Committed = true, CommitShortHash = shortHash, Canceled = true, Error = "推送已取消（提交已保留在本地）。" };
        }

        if (!pushResult.Success)
        {
            var err = pushResult.StdErrText.Length > 0 ? pushResult.StdErrText : pushResult.StdOutText;
            return new PublishResult { Committed = true, CommitShortHash = shortHash, Error = $"git push 失败（提交保留在本地）：\n{err}" };
        }

        log?.Invoke(LogLevel.Pass, "git push 成功");
        return new PublishResult { Committed = true, Pushed = true, CommitShortHash = shortHash, UsedSetUpstream = !hasUpstream };
    }

    /// <summary>
    /// 快速关键安全检查：冲突、敏感文件、Secret、大文件、图片。
    /// 返回阻断原因列表（空 = 通过）。
    /// </summary>
    private async Task<List<string>> QuickSafetyCheckAsync(string root, IReadOnlyList<GitFileChange> changes, CancellationToken ct)
    {
        var blocks = new List<string>();

        var tracked = GitRepositoryInspector.ParseLsFiles((await _git.LsFilesAsync(root, ct)).Stdout);
        var sensitive = await _sensitiveScanner.ScanAsync(root, changes, tracked, ct);
        blocks.AddRange(sensitive.Findings
            .Where(f => f.Severity == ScanSeverity.Blocked)
            .Select(f => $"敏感文件 {f.File}：{f.Message}"));

        var secretTargets = changes.Where(c => !c.IsDeletedLike()).Select(c => c.Path);
        var secret = await _secretScanner.ScanFilesAsync(root, secretTargets, ct);
        blocks.AddRange(secret.Findings
            .Where(f => f.Severity == ScanSeverity.Blocked)
            .Select(f => $"Secret {f.File}（第{f.Line}行）{f.Preview}"));

        var large = _largeFileScanner.Scan(root, changes);
        blocks.AddRange(large.Where(f => f.Severity == ScanSeverity.Blocked).Select(f => $"大文件 {f.File}：{f.Message}"));

        return blocks;
    }
}