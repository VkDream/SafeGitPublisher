using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 安全提交流程。所有状态类 Git 命令均失败关闭；最终扫描读取 index blob；
/// Push 固定为 origin、当前分支和已扫描提交 OID，并在执行前复检 origin 目标与待推送历史。
/// </summary>
public sealed class PublishWorkflowService
{
    // 与 SecretScanner 的完整文本扫描上限保持一致。超过 100 MiB 会先被大文件硬门禁阻断，
    // 不能再用旧的 2 MiB 阈值静默跳过较大文本中的 Secret。
    private const long MaxSecretScanBytes = 100L * 1024 * 1024;
    private static readonly Regex TreeRecordRegex = new(
        @"^(?<mode>\d+)\s+(?<type>\w+)\s+(?<oid>[0-9a-fA-F]+)\s+(?<size>\d+|-)\t(?<path>.*)$",
        RegexOptions.CultureInvariant);
    private readonly GitService _git;
    private readonly SensitiveFileScanner _sensitiveScanner;
    private readonly SecretScanner _secretScanner;
    private readonly LargeFileScanner _largeFileScanner;
    private readonly ConcurrentDictionary<string, ExistingPushTicket> _existingPushTickets = new(StringComparer.Ordinal);

    private sealed record ExistingPushTicket(string RepositoryRoot, string Branch, string CommitOid,
            string? RemoteOid, string TargetFingerprint, bool RequireImageConfirmation,
        bool HasOutgoingImages, bool RequireBuildVerification, string? BuildVerifiedCommitOid,
        DateTimeOffset ExpiresAt);

    private sealed record RemoteBranchSnapshot(string? Oid, string? Error, bool Canceled = false, bool TimedOut = false);

    private sealed record OutgoingScanResult(List<string> Blocks, string? Error, int CommitCount, bool HasImages);

    public PublishWorkflowService(GitService git, SensitiveFileScanner sensitiveScanner, SecretScanner secretScanner, LargeFileScanner largeFileScanner)
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

    /// <summary>执行提交或提交并上传；任何安全信息读取失败都中止，不使用不完整结果继续。</summary>
    public async Task<PublishResult> ExecuteAsync(PublishRequest request, Action<LogLevel, string>? log = null, CancellationToken ct = default)
    {
        var root = request.RepositoryRoot;
        var message = request.CommitMessage.Trim();
        if (message.Length == 0) return new PublishResult { Error = "Commit Message 不能为空。" };

        // CommitAndPush 在触碰 index 前先确认 origin 网络路径；工作区状态/安全扫描只读，
        // 必须放在远端门禁之后，避免已知离线时仍执行可能很重的本地扫描。
        string? plannedBranch = null;
        string? plannedTarget = null;
        if (request.Mode == PublishMode.CommitAndPush)
        {
            var branchResult = await _git.CurrentBranchResultAsync(root, ct);
            if (!branchResult.Success) return CommandFailure("读取当前分支", branchResult);
            plannedBranch = branchResult.Stdout.FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(plannedBranch))
            {
                return new PublishResult { Error = "当前处于 detached HEAD；为避免生成无法按合同发布的本地提交，已在暂存前中止。" };
            }

            var targetCheck = await ReadValidatedOriginAsync(root, ct);
            if (targetCheck.Error != null) return new PublishResult { Error = targetCheck.Error };
            plannedTarget = targetCheck.Remote!.ExactEffectivePushUrl;
            var plannedDisplay = targetCheck.Remote.EffectivePushDisplay;
            targetCheck.Remote.ClearExactUrls();
            log?.Invoke(LogLevel.Info, $"发布目标已锁定：origin / {plannedBranch} / {plannedDisplay}");

            var reachability = await ReadRemoteBranchSnapshotAsync(root, plannedTarget!, plannedBranch, ct);
            if (reachability.Error != null)
            {
                plannedTarget = null;
                return new PublishResult
                {
                    Canceled = reachability.Canceled,
                    Error = "发布前无法通过 git.exe 验证 origin 网络路径或认证状态，已在生成本地提交前中止：" + reachability.Error
                };
            }
        }

        log?.Invoke(LogLevel.Info, "步骤 1/8  重新获取工作区状态…");
        var status = await _git.StatusPorcelainAsync(root, ct);
        if (!status.Success) return CommandFailure("读取工作区状态", status);

        var changes = GitRepositoryInspector.ParseStatusPorcelain(status.Stdout);
        var conflicts = changes.Where(change => change.IsConflict).ToList();
        if (conflicts.Count > 0)
        {
            return new PublishResult { Error = $"存在未解决的合并冲突：{string.Join("、", conflicts.Select(change => change.Path))}，已中止发布。" };
        }

        if (changes.Count == 0)
        {
            log?.Invoke(LogLevel.Info, "当前工作区没有可提交的变更。");
            return new PublishResult { Informational = true, Error = "当前工作区没有可提交的变更。" };
        }

        log?.Invoke(LogLevel.Info, "步骤 2/8  重新执行关键安全检查…");
        var quick = await QuickSafetyCheckAsync(root, changes, ct);
        if (quick.Error != null) return new PublishResult { Error = quick.Error };
        if (quick.Blocks.Count > 0)
        {
            foreach (var block in quick.Blocks) log?.Invoke(LogLevel.Blocked, block);
            return new PublishResult { Error = string.Join("\n", quick.Blocks) };
        }

        // write-tree/read-tree 只快照并恢复 index，不触碰用户工作区；比 reset 更能保护原有部分暂存状态。
        var snapshotResult = await _git.WriteIndexTreeAsync(root, ct);
        var snapshotOid = snapshotResult.Success ? snapshotResult.Stdout.FirstOrDefault()?.Trim() : null;
        if (!IsFullObjectId(snapshotOid))
        {
            var detail = snapshotResult.Success ? "write-tree 未返回 tree OID。" : Describe(snapshotResult);
            return new PublishResult { Canceled = snapshotResult.Canceled, Error = "创建暂存区安全快照失败，已中止：" + detail };
        }
        // IsFullObjectId 已证明非空且为完整 tree OID；用非空局部值把该运行时合同同步给编译器。
        var verifiedSnapshotOid = snapshotOid!;

        string? committedOid = null;
        var pushStarted = false;
        try
        {
            log?.Invoke(LogLevel.Info, "步骤 3/8  git add --all …");
            var add = await _git.AddAllAsync(root, ct);
            if (!add.Success) return await AbortAndRestoreAsync(root, verifiedSnapshotOid, $"git add 失败：{Describe(add)}", log);
            log?.Invoke(LogLevel.Pass, "git add 完成");

            log?.Invoke(LogLevel.Info, "步骤 4/8  读取已暂存文件…");
            var cached = await _git.DiffCachedNameStatusAsync(root, ct);
            if (!cached.Success) return await AbortAndRestoreAsync(root, verifiedSnapshotOid, $"读取暂存区失败：{Describe(cached)}", log);
            var staged = GitRepositoryInspector.ParseDiffCachedNameStatus(cached.Stdout);
            if (staged.Count == 0) return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "没有可提交的变更（暂存区为空）。", log);

            log?.Invoke(LogLevel.Info, "步骤 5/8  扫描已暂存内容…");
            var stagedScan = await ScanIndexAsync(root, staged, ct);
            if (stagedScan.Error != null) return await AbortAndRestoreAsync(root, verifiedSnapshotOid, stagedScan.Error, log);

            if (stagedScan.Blocks.Count > 0)
            {
                foreach (var block in stagedScan.Blocks) log?.Invoke(LogLevel.Blocked, block);
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "已暂存内容触发安全阻断：\n" + string.Join("\n", stagedScan.Blocks), log);
            }

            // 锁定已完成最终扫描的精确 index tree。pre-commit/commit-msg hook 若在此后
            // 再次 git add，实际 commit tree 将与该 OID 不一致，必须拒绝成功回包与 Push。
            var scannedTreeResult = await _git.WriteIndexTreeAsync(root, ct);
            var scannedTreeOid = scannedTreeResult.Success ? scannedTreeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (!IsFullObjectId(scannedTreeOid))
            {
                var detail = scannedTreeResult.Success ? "write-tree 未返回 tree OID。" : Describe(scannedTreeResult);
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "锁定已扫描暂存内容失败：" + detail, log);
            }

            var imageGate = request.RequireImageConfirmation && staged.Any(change => !change.IsDeletedLike() && change.IsImage) && !request.ImageConfirmed;
            if (imageGate && request.Mode == PublishMode.CommitAndPush)
            {
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "本次提交包含图片，请在确认图片已脱敏后再进行“安全提交并上传”。", log);
            }

            var headBeforeResult = await _git.HeadOidResultAsync(root, ct);
            var headBefore = headBeforeResult.Success ? headBeforeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (!headBeforeResult.Success && headBeforeResult.Canceled)
            {
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "读取提交前 HEAD 已取消。", log, canceled: true);
            }
            if (!headBeforeResult.Success && (headBeforeResult.TimedOut || headBeforeResult.ExitCode is null || headBeforeResult.ExitCode != 128))
            {
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "读取提交前 HEAD 失败：" + Describe(headBeforeResult), log);
            }
            log?.Invoke(LogLevel.Info, "步骤 6/8  git commit …");
            var commit = await _git.CommitAsync(root, message, ct);
            var headAfterResult = await _git.HeadOidResultAsync(root, CancellationToken.None);
            var headAfter = headAfterResult.Success ? headAfterResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (!headAfterResult.Success)
            {
                return new PublishResult
                {
                    Canceled = commit.Canceled,
                    Error = "git commit 后无法读取 HEAD，提交结果不确定。为避免破坏可能已经生成的提交，未恢复 index、未执行 Push，请人工复核：" + Describe(headAfterResult)
                };
            }
            var createdCommit = !string.IsNullOrWhiteSpace(headAfter) && !string.Equals(headBefore, headAfter, StringComparison.Ordinal);
            if (createdCommit) committedOid = headAfter;
            if (!commit.Success)
            {
                if (createdCommit)
                {
                    return new PublishResult
                    {
                        Committed = true,
                        CommitCreatedButUnverified = true,
                        CommitOid = headAfter,
                        CommitShortHash = ShortOid(headAfter),
                        PushState = PushDeliveryState.None,
                        Canceled = commit.Canceled,
                        Error = "git commit 返回失败，但 HEAD 已发生变化。为避免重复提交，已保留新提交并停止后续 Push，请人工复核 hooks 输出：\n" + Describe(commit)
                    };
                }
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "git commit 失败：" + Describe(commit), log);
            }

            if (!createdCommit)
            {
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "git commit 虽返回成功，但 HEAD 未生成新提交；已中止并恢复原暂存区。", log);
            }

            var committedTreeResult = await _git.HeadTreeOidResultAsync(root, CancellationToken.None);
            var committedTreeOid = committedTreeResult.Success ? committedTreeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (!IsFullObjectId(committedTreeOid))
            {
                return UnverifiedCommitFailure(headAfter, "已生成本地提交，但无法校验实际 commit tree，已拒绝继续 Push：" + Describe(committedTreeResult));
            }
            if (!string.Equals(scannedTreeOid, committedTreeOid, StringComparison.Ordinal))
            {
                return UnverifiedCommitFailure(headAfter,
                    "已生成本地提交，但 hook 在安全扫描后改写了提交内容。该提交未通过安全门禁，已拒绝成功回包与 Push，请人工检查 hooks 及本地 HEAD。");
            }

            var lockedCommitOid = headAfter!;
            if (!IsFullObjectId(lockedCommitOid))
            {
                return UnverifiedCommitFailure(lockedCommitOid,
                    "已生成本地提交，但 git.exe 未返回规范的完整 commit OID，已拒绝继续 Push。");
            }
            var shortHash = ShortOid(lockedCommitOid);
            log?.Invoke(LogLevel.Pass, $"已提交：{shortHash}  {message}");
            if (request.Mode == PublishMode.CommitOnly)
            {
                return new PublishResult { Committed = true, CommitOid = lockedCommitOid, CommitShortHash = shortHash };
            }

            // 立即执行前再次验证分支与 origin；不允许配置在确认后漂移。
            var branchNowResult = await _git.CurrentBranchResultAsync(root, ct);
            if (!branchNowResult.Success) return CommittedFailure(lockedCommitOid, "已提交，但推送前读取当前分支失败：" + Describe(branchNowResult));
            var branchNow = branchNowResult.Stdout.FirstOrDefault()?.Trim();
            if (!string.Equals(plannedBranch, branchNow, StringComparison.Ordinal))
            {
                return CommittedFailure(lockedCommitOid, $"已提交，但当前分支从 {plannedBranch} 变为 {branchNow ?? "detached HEAD"}，已拒绝 Push。");
            }

            var targetNow = await ReadValidatedOriginAsync(root, ct);
            if (targetNow.Error != null) return CommittedFailure(lockedCommitOid, "已提交，但 " + targetNow.Error);
            var exactTargetNow = targetNow.Remote!.ExactEffectivePushUrl;
            var targetDisplayNow = targetNow.Remote.EffectivePushDisplay;
            targetNow.Remote.ClearExactUrls();
            if (!string.Equals(plannedTarget, exactTargetNow, StringComparison.Ordinal))
            {
                return CommittedFailure(lockedCommitOid, "已提交，但 origin push 目标在确认后发生变化，已拒绝 Push。当前安全显示：" + targetDisplayNow);
            }

            log?.Invoke(LogLevel.Info, "Push 前复检本地未推送历史中的 Secret、敏感路径与超大 blob…");
            var remoteBeforeScan = await ReadRemoteBranchSnapshotAsync(root, exactTargetNow!, plannedBranch!, ct);
            if (remoteBeforeScan.Error != null)
            {
                return CommittedFailure(lockedCommitOid, "无法读取 origin 远端分支状态，已拒绝 Push：" + remoteBeforeScan.Error);
            }
            var outgoing = await ScanOutgoingHistoryAsync(root, remoteBeforeScan.Oid, lockedCommitOid, ct);
            if (outgoing.Error != null) return CommittedFailure(lockedCommitOid, outgoing.Error);
            if (outgoing.Blocks.Count > 0)
            {
                foreach (var block in outgoing.Blocks) log?.Invoke(LogLevel.Blocked, block);
                return CommittedFailure(lockedCommitOid, "待推送历史触发安全阻断，提交保留在本地，未执行 Push：\n" + string.Join("\n", outgoing.Blocks));
            }

            var upstream = await _git.UpstreamResultAsync(root, ct);
            var hasUpstream = upstream.Success;
            if (!hasUpstream && upstream.Canceled)
            {
                return CommittedFailure(lockedCommitOid, "读取 upstream 状态已取消，未执行 Push。");
            }
            if (!hasUpstream && upstream.ExitCode is null)
            {
                return CommittedFailure(lockedCommitOid, "读取 upstream 状态失败，未执行 Push：" + Describe(upstream));
            }
            // 扫描后再次确认 HEAD/分支/目标/远端基线。即便核验后本地 HEAD 再变化，显式 OID refspec
            // 也只会上传已扫描的 lockedCommitOid；远端并发前进则由普通非快进保护拒绝。
            var branchOidNow = await _git.BranchOidResultAsync(root, plannedBranch!, ct);
            var currentBranchOid = branchOidNow.Success ? branchOidNow.Stdout.FirstOrDefault()?.Trim() : null;
            if (!IsFullObjectId(currentBranchOid) || !string.Equals(lockedCommitOid, currentBranchOid, StringComparison.Ordinal))
            {
                return CommittedFailure(lockedCommitOid, "待推送分支在安全扫描后发生变化，已拒绝 Push。请重新检查待推送提交。");
            }
            var remoteAfterScan = await ReadRemoteBranchSnapshotAsync(root, exactTargetNow!, plannedBranch!, ct);
            if (remoteAfterScan.Error != null || !string.Equals(remoteBeforeScan.Oid, remoteAfterScan.Oid, StringComparison.Ordinal))
            {
                return CommittedFailure(lockedCommitOid, "origin 远端分支在安全扫描后无法核验或已发生变化，已拒绝 Push。");
            }
            log?.Invoke(LogLevel.Info, $"执行显式发布：origin / {shortHash}:refs/heads/{plannedBranch}");
            pushStarted = true;
            var push = await _git.PushExplicitTargetAsync(root, exactTargetNow!, lockedCommitOid, plannedBranch!, ct);
            // Push 进程一旦启动，非零退出也可能发生在远端已接收之后（例如响应链路中断）。
            // 因此任何非成功结果都必须先重新核验远端，不能按“未推送”直接重试。
            if (!push.Success)
            {
                return UnknownPush(lockedCommitOid,
                    "git push 未返回可确认的成功结果，远端是否已接收提交未知。禁止直接重试，请先使用仅上传恢复入口核验远端：\n" + Describe(push),
                    push.Canceled, committed: true);
            }

            var pushedSnapshot = await ReadRemoteBranchSnapshotAsync(root, exactTargetNow!, plannedBranch!, CancellationToken.None);
            if (pushedSnapshot.Error != null || !string.Equals(pushedSnapshot.Oid, lockedCommitOid, StringComparison.Ordinal))
            {
                return new PublishResult
                {
                    Committed = true,
                    CommitOid = lockedCommitOid,
                    CommitShortHash = shortHash,
                    PushState = PushDeliveryState.Unknown,
                    RequiresPushReconciliation = true,
                    Error = "git push 返回成功，但无法确认 origin 已指向本次提交。禁止自动重推，请使用仅上传恢复入口先重新核验远端。"
                };
            }

            if (!hasUpstream)
            {
                var setUpstream = await _git.SetOriginUpstreamAsync(root, plannedBranch!, ct);
                if (!setUpstream.Success)
                {
                    return new PublishResult
                    {
                        Committed = true,
                        Pushed = true,
                        PushState = PushDeliveryState.Pushed,
                        CommitOid = lockedCommitOid,
                        CommitShortHash = shortHash,
                        Error = "Push 已成功，但设置 origin upstream 失败；不会重复 Push，请人工检查分支跟踪配置：" + Describe(setUpstream)
                    };
                }
            }

            log?.Invoke(LogLevel.Pass, "git push 成功");
            return new PublishResult { Committed = true, Pushed = true, PushState = PushDeliveryState.Pushed, CommitOid = lockedCommitOid, CommitShortHash = shortHash, UsedSetUpstream = !hasUpstream };
        }
        catch (OperationCanceledException)
        {
            if (committedOid == null)
            {
                return await AbortAndRestoreAsync(root, verifiedSnapshotOid, "发布已取消。", log, canceled: true);
            }
            return pushStarted
                ? UnknownPush(committedOid, "Push 已启动后操作被取消，远端是否接收提交未知。禁止直接重试，请先重新检查待推送提交。", canceled: true, committed: true)
                : new PublishResult
                {
                    Committed = true,
                    CommitOid = committedOid,
                    CommitShortHash = ShortOid(committedOid),
                    Canceled = true,
                    PushState = PushDeliveryState.Pending,
                    RequiresPushReconciliation = true,
                    Error = "本地提交已生成，后续安全复检被取消；未执行 Push。可使用仅上传恢复入口重新检查。"
                };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessLaunchException or InvalidDataException or ArgumentException)
        {
            var detail = $"发布流程异常中止（{ex.GetType().Name}）。";
            return committedOid == null
                ? await AbortAndRestoreAsync(root, verifiedSnapshotOid, detail, log)
                : pushStarted
                    ? UnknownPush(committedOid, detail + "Push 已启动，远端结果未知；请先重新核验远端。", committed: true)
                    : CommittedFailure(committedOid, detail + "本地提交已生成，未执行 Push；请使用仅上传恢复入口重新检查。");
        }
    }

    private async Task<(List<string> Blocks, string? Error)> QuickSafetyCheckAsync(string root, IReadOnlyList<GitFileChange> changes, CancellationToken ct)
    {
        var trackedResult = await _git.LsFilesAsync(root, ct);
        if (!trackedResult.Success) return (new List<string>(), "读取已跟踪文件失败，已按安全策略中止：" + Describe(trackedResult));

        var blocks = new List<string>();
        var tracked = GitRepositoryInspector.ParseLsFiles(trackedResult.Stdout);
        var sensitive = await _sensitiveScanner.ScanAsync(root, changes, tracked, ct);
        blocks.AddRange(sensitive.Findings.Where(finding => finding.Severity == ScanSeverity.Blocked).Select(finding => $"敏感文件 {finding.File}：{finding.Message}"));

        var secret = await _secretScanner.ScanFilesAsync(root, changes.Where(change => !change.IsDeletedLike()).Select(change => change.Path), ct);
        blocks.AddRange(secret.Findings.Where(IsSecretGateFinding).Select(FormatSecret));
        if (!secret.IsComplete) blocks.Add("Secret 快速复检未完整覆盖：" + DescribeIncompleteScan(secret));
        blocks.AddRange(_largeFileScanner.Scan(root, changes).Where(finding => finding.Severity == ScanSeverity.Blocked).Select(finding => $"大文件 {finding.File}：{finding.Message}"));
        return (blocks, null);
    }

    private async Task<(List<string> Blocks, string? Error)> ScanIndexAsync(string root, IReadOnlyList<GitFileChange> staged, CancellationToken ct)
    {
        var blocks = new List<string>();
        foreach (var change in staged)
        {
            ct.ThrowIfCancellationRequested();
            if (change.IsDeletedLike()) continue;
            if (SensitiveFileRules.IsBlockedPath(change.Path))
            {
                blocks.Add($"敏感文件 {change.Path}：{SensitiveFileRules.BlockReason(change.Path)}");
            }

            var sizeResult = await _git.IndexBlobSizeAsync(root, change.Path, ct);
            if (!sizeResult.Success) return (blocks, $"读取暂存 blob 大小失败（{change.Path}），已按安全策略中止：{Describe(sizeResult)}");
            if (!long.TryParse(sizeResult.Stdout.FirstOrDefault()?.Trim(), out var size) || size < 0)
            {
                return (blocks, $"暂存 blob 大小格式异常（{change.Path}），已按安全策略中止。");
            }

            change.SizeBytes = size;
            var (risk, description) = _largeFileScanner.Classify(size);
            change.Risk = risk;
            if (risk == RiskLevel.Blocked) blocks.Add($"大文件 {change.Path}：{description}");

            if (size > MaxSecretScanBytes) continue; // >100 MiB 已由大文件门禁阻断。
            string temporaryPath;
            try
            {
                temporaryPath = CreateBlobTempPath();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return (blocks, $"创建 Secret 扫描临时文件失败（{ex.GetType().Name}），已按安全策略中止。");
            }
            string? scanError = null;
            try
            {
                var blob = await _git.WriteIndexBlobToFileAsync(root, change.Path, temporaryPath, ct);
                if (!blob.Success)
                {
                    scanError = $"读取暂存 blob 原始字节失败（{change.Path}），已按安全策略中止：{Describe(blob)}";
                }
                else
                {
                    // 不按扩展名预先跳过；即使名为 .png/.dll，也必须由原始字节内容探测决定是否二进制。
                    var scan = await _secretScanner.ScanRawBlobFileAsync(temporaryPath, change.Path, ct);
                    if (!scan.IsComplete)
                    {
                        scanError = $"暂存 blob Secret 扫描未完整覆盖（{change.Path}），已按安全策略中止：{DescribeIncompleteScan(scan)}";
                    }
                    else
                    {
                        blocks.AddRange(scan.Findings.Where(IsSecretGateFinding).Select(FormatSecret));
                    }
                }
            }
            finally
            {
                if (!TryDeleteBlobTempFile(temporaryPath))
                {
                    scanError = "Secret 扫描临时文件清理失败，已拒绝继续发布。请关闭可能占用该文件的程序后重试。";
                }
            }
            if (scanError != null) return (blocks, scanError);
        }
        return (blocks, null);
    }

    private async Task<OutgoingScanResult> ScanOutgoingHistoryAsync(string root, string? remoteOid, string lockedCommitOid, CancellationToken ct)
    {
        var outgoing = await _git.OutgoingCommitsFromRemoteOidAsync(root, remoteOid, lockedCommitOid, ct);
        if (!outgoing.Success) return new OutgoingScanResult(new List<string>(), "无法确定待推送提交范围，已拒绝 Push：" + Describe(outgoing), 0, false);
        var commits = outgoing.Stdout.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (commits.Any(commit => !IsFullObjectId(commit)))
        {
            return new OutgoingScanResult(new List<string>(), "待推送提交列表包含非规范完整 OID，已拒绝 Push。", commits.Count, false);
        }
        if (commits.Count == 0) return new OutgoingScanResult(new List<string>(), null, 0, false);

        // 首次发布会扫描 HEAD 全历史。设置合理上限，避免 UI 因超大历史长时间假死；
        // 超限不是放行条件，而是 fail-closed，要求先人工审计或缩小历史。
        if (commits.Count > 5000)
        {
            return new OutgoingScanResult(new List<string>(), $"待推送提交共 {commits.Count} 个，超过自动安全复检上限 5000，已拒绝 Push。请先人工审计历史。", commits.Count, false);
        }

        var blocks = new List<string>();
        var seenBlobs = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var hasImages = false;
        foreach (var commit in commits)
        {
            var tree = await _git.ListCommitBlobsAsync(root, commit, ct);
            if (!tree.Success) return new OutgoingScanResult(blocks, $"无法读取待推送提交 {ShortOid(commit)} 的 tree，已拒绝 Push：{Describe(tree)}", commits.Count, hasImages);

            foreach (var record in SplitNullRecords(tree.Stdout))
            {
                var match = TreeRecordRegex.Match(record);
                if (!match.Success || !long.TryParse(match.Groups["size"].Value, out var size))
                {
                    return new OutgoingScanResult(blocks, $"待推送 tree 输出格式异常（提交 {ShortOid(commit)}），已拒绝 Push。", commits.Count, hasImages);
                }

                var oid = match.Groups["oid"].Value;
                var path = match.Groups["path"].Value;
                if (GitFileChange.IsImagePath(path)) hasImages = true;
                if (seenPaths.Add(path) && SensitiveFileRules.IsBlockedPath(path))
                {
                    blocks.Add($"历史敏感文件 {path}（提交 {ShortOid(commit)}）：{SensitiveFileRules.BlockReason(path)}");
                }
                var firstBlob = seenBlobs.Add(oid);
                if (firstBlob)
                {
                    var (risk, description) = _largeFileScanner.Classify(size);
                    if (risk == RiskLevel.Blocked) blocks.Add($"历史大文件 {path}（提交 {ShortOid(commit)}）：{description}");
                }

                if (size > MaxSecretScanBytes || !firstBlob) continue;
                string temporaryPath;
                try
                {
                    temporaryPath = CreateBlobTempPath();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return new OutgoingScanResult(blocks, $"创建历史 Secret 扫描临时文件失败（{ex.GetType().Name}），已拒绝 Push。", commits.Count, hasImages);
                }
                string? scanError = null;
                try
                {
                    var blob = await _git.WriteBlobObjectToFileAsync(root, oid, temporaryPath, ct);
                    if (!blob.Success)
                    {
                        scanError = $"无法读取待推送 blob {oid[..Math.Min(8, oid.Length)]} 原始字节，已拒绝 Push：{Describe(blob)}";
                    }
                    else
                    {
                        // 后缀名不是安全证据；所有 <=100 MiB blob 都进入原始字节内容探测。
                        var scan = await _secretScanner.ScanRawBlobFileAsync(temporaryPath, path, ct);
                        if (!scan.IsComplete)
                        {
                            scanError = $"待推送 blob Secret 扫描未完整覆盖（{path}，{oid[..Math.Min(8, oid.Length)]}），已拒绝 Push：{DescribeIncompleteScan(scan)}";
                        }
                        else
                        {
                            blocks.AddRange(scan.Findings.Where(IsSecretGateFinding)
                                .Select(finding => $"历史 {FormatSecret(finding)}（提交 {ShortOid(commit)}）"));
                        }
                    }
                }
                finally
                {
                    if (!TryDeleteBlobTempFile(temporaryPath))
                    {
                        scanError = "历史 Secret 扫描临时文件清理失败，已拒绝 Push。请关闭可能占用该文件的程序后重试。";
                    }
                }
                if (scanError != null) return new OutgoingScanResult(blocks, scanError, commits.Count, hasImages);
            }
        }
        return new OutgoingScanResult(blocks.Distinct(StringComparer.Ordinal).ToList(), null, commits.Count, hasImages);
    }

    /// <summary>
    /// 发现并完整复检当前分支已有但尚未上传的提交。该方法不会修改 index、工作区或历史，
    /// 也不会执行 Push；返回的公开计划不含精确 Remote URL。
    /// </summary>
    public async Task<ExistingPushPlan> PrepareExistingPushAsync(ExistingPushPrepareRequest request,
        Action<LogLevel, string>? log = null, CancellationToken ct = default)
    {
        PurgeExpiredExistingPushTickets();
        var root = await ResolveCanonicalRootAsync(request.RepositoryRoot, ct);
        if (root == null) return ExistingPlan(ExistingPushDisposition.Unknown, message: "无法确认规范仓库根目录，已拒绝仅上传。");

        var branchResult = await _git.CurrentBranchResultAsync(root, ct);
        if (!branchResult.Success) return ExistingPlan(ExistingPushDisposition.Unknown, root, message: "无法读取当前分支：" + Describe(branchResult));
        var branch = branchResult.Stdout.FirstOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(branch)) return ExistingPlan(ExistingPushDisposition.DetachedHead, root, message: "当前处于 detached HEAD，禁止仅上传。");

        var headResult = await _git.HeadOidResultAsync(root, ct);
        var lockedOid = headResult.Success ? headResult.Stdout.FirstOrDefault()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(lockedOid))
        {
            return ExistingPlan(headResult.ExitCode == 128 ? ExistingPushDisposition.NoLocalCommit : ExistingPushDisposition.Unknown,
                root, branch, message: headResult.ExitCode == 128 ? "当前仓库尚无本地提交。" : "无法读取当前 HEAD：" + Describe(headResult));
        }
        if (!IsFullObjectId(lockedOid))
        {
            return ExistingPlan(ExistingPushDisposition.Unknown, root, branch,
                message: "git.exe 未返回规范的完整 HEAD OID，已拒绝仅上传。");
        }
        var branchOidResult = await _git.BranchOidResultAsync(root, branch, ct);
        var branchOid = branchOidResult.Success ? branchOidResult.Stdout.FirstOrDefault()?.Trim() : null;
        if (!IsFullObjectId(branchOid) || !string.Equals(lockedOid, branchOid, StringComparison.Ordinal))
        {
            return ExistingPlan(ExistingPushDisposition.RemoteDrift, root, branch, lockedOid, message: "当前分支与 HEAD 未能绑定到同一提交，已拒绝仅上传。");
        }
        if (request.RequireBuildVerification &&
            (string.IsNullOrWhiteSpace(request.BuildVerifiedCommitOid) || !FixedEquals(lockedOid, request.BuildVerifiedCommitOid)))
        {
            return ExistingPlan(ExistingPushDisposition.Blocked, root, branch, lockedOid,
                message: "当前设置要求构建验证，但仅上传恢复没有与锁定提交绑定的有效构建证明，已拒绝 Push。请先按项目要求完成构建验证。");
        }

        var targetCheck = await ReadValidatedOriginAsync(root, ct);
        if (targetCheck.Error != null) return ExistingPlan(ExistingPushDisposition.Blocked, root, branch, lockedOid, message: targetCheck.Error);
        var exactTarget = targetCheck.Remote!.ExactEffectivePushUrl!;
        var remoteDisplay = targetCheck.Remote.EffectivePushDisplay;
        var targetFingerprint = FingerprintTarget(exactTarget);
        targetCheck.Remote.ClearExactUrls();

        var remote = await ReadRemoteBranchSnapshotAsync(root, exactTarget, branch, ct);
        if (remote.Error != null)
        {
            return ExistingPlan(ExistingPushDisposition.Unknown, root, branch, lockedOid, remoteDisplay: remoteDisplay,
                targetFingerprint: targetFingerprint, message: "无法核验 origin 远端分支；未执行 Push：" + remote.Error);
        }

        var relation = await ClassifyRemoteRelationAsync(root, remote.Oid, lockedOid, ct);
        if (relation.Disposition != ExistingPushDisposition.Ready)
        {
            return ExistingPlan(relation.Disposition, root, branch, lockedOid, remote.Oid, remoteDisplay,
                targetFingerprint, message: relation.Message);
        }

        log?.Invoke(LogLevel.Info, $"正在复检待推送历史：{ShortOid(lockedOid)} / {remoteDisplay}");
        var scan = await ScanOutgoingHistoryAsync(root, remote.Oid, lockedOid, ct);
        if (scan.Error != null)
        {
            return ExistingPlan(ExistingPushDisposition.Unknown, root, branch, lockedOid, remote.Oid, remoteDisplay,
                targetFingerprint, scan.CommitCount, scan.HasImages, request.RequireImageConfirmation && scan.HasImages, scan.Error);
        }
        if (scan.Blocks.Count > 0)
        {
            foreach (var block in scan.Blocks) log?.Invoke(LogLevel.Blocked, block);
            return ExistingPlan(ExistingPushDisposition.Blocked, root, branch, lockedOid, remote.Oid, remoteDisplay,
                targetFingerprint, scan.CommitCount, scan.HasImages, request.RequireImageConfirmation && scan.HasImages,
                "待推送历史触发安全阻断：\n" + string.Join("\n", scan.Blocks));
        }

        // 扫描可能耗时；创建票据前再次绑定当前分支/HEAD、目标和远端基线。
        var branchAfter = await _git.CurrentBranchResultAsync(root, ct);
        var branchNameAfter = branchAfter.Success ? branchAfter.Stdout.FirstOrDefault()?.Trim() : null;
        var branchOidAfter = await _git.BranchOidResultAsync(root, branch, ct);
        var oidAfter = branchOidAfter.Success ? branchOidAfter.Stdout.FirstOrDefault()?.Trim() : null;
        if (!string.Equals(branch, branchNameAfter, StringComparison.Ordinal) || !IsFullObjectId(oidAfter) ||
            !string.Equals(lockedOid, oidAfter, StringComparison.Ordinal))
        {
            return ExistingPlan(ExistingPushDisposition.RemoteDrift, root, branch, lockedOid, remote.Oid,
                remoteDisplay, targetFingerprint, scan.CommitCount, scan.HasImages,
                request.RequireImageConfirmation && scan.HasImages, "本地分支在安全扫描后发生变化，请重新检查待推送提交。");
        }
        var targetAfter = await ReadValidatedOriginAsync(root, ct);
        if (targetAfter.Error != null) return ExistingPlan(ExistingPushDisposition.RemoteDrift, root, branch, lockedOid, message: targetAfter.Error);
        var exactTargetAfter = targetAfter.Remote!.ExactEffectivePushUrl!;
        targetAfter.Remote.ClearExactUrls();
        if (!string.Equals(exactTarget, exactTargetAfter, StringComparison.Ordinal))
        {
            return ExistingPlan(ExistingPushDisposition.RemoteDrift, root, branch, lockedOid, remote.Oid,
                remoteDisplay, targetFingerprint, scan.CommitCount, scan.HasImages,
                request.RequireImageConfirmation && scan.HasImages, "origin push 目标在安全扫描后发生变化，请重新检查。");
        }
        var remoteAfterScan = await ReadRemoteBranchSnapshotAsync(root, exactTarget, branch, ct);
        if (remoteAfterScan.Error != null || !string.Equals(remote.Oid, remoteAfterScan.Oid, StringComparison.Ordinal))
        {
            return ExistingPlan(ExistingPushDisposition.RemoteDrift, root, branch, lockedOid, remoteAfterScan.Oid,
                remoteDisplay, targetFingerprint, scan.CommitCount, scan.HasImages,
                request.RequireImageConfirmation && scan.HasImages, "origin 远端分支在安全扫描后无法核验或已发生变化，请重新检查。");
        }

        var planId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _existingPushTickets[planId] = new ExistingPushTicket(root, branch, lockedOid, remote.Oid,
            targetFingerprint, request.RequireImageConfirmation, scan.HasImages, request.RequireBuildVerification,
            request.RequireBuildVerification ? lockedOid : null, DateTimeOffset.UtcNow.AddMinutes(10));
        return new ExistingPushPlan
        {
            PlanId = planId,
            Disposition = ExistingPushDisposition.Ready,
            RepositoryRoot = root,
            Branch = branch,
            CommitOid = lockedOid,
            RemoteOid = remote.Oid,
            RemoteDisplay = remoteDisplay,
            RemoteTargetFingerprint = targetFingerprint,
            OutgoingCommitCount = scan.CommitCount,
            HasOutgoingImages = scan.HasImages,
            RequiresImageConfirmation = request.RequireImageConfirmation && scan.HasImages,
            Message = request.RequireImageConfirmation && scan.HasImages
                ? "安全复检通过；待推送历史包含图片，请在本次仅上传确认页人工确认已脱敏。"
                : "安全复检通过，可以仅上传已有本地提交。"
        };
    }

    /// <summary>
    /// 执行单次仅上传安全计划。执行前重核仓库、分支、HEAD、Remote 指纹和远端 OID；
    /// 不调用 add、commit、reset/read-tree。票据无论成功失败均单次消费，重试必须重新 Prepare。
    /// </summary>
    public async Task<PublishResult> ExecuteExistingPushAsync(ExistingPushExecuteRequest request,
        Action<LogLevel, string>? log = null, CancellationToken ct = default)
    {
        PurgeExpiredExistingPushTickets();
        if (string.IsNullOrWhiteSpace(request.PlanId) || string.IsNullOrWhiteSpace(request.CommitOid) ||
            string.IsNullOrWhiteSpace(request.RemoteTargetFingerprint))
        {
            return new PublishResult
            {
                PushState = PushDeliveryState.Blocked,
                Error = "仅上传确认数据不完整，请重新检查待推送提交。"
            };
        }
        if (!_existingPushTickets.TryRemove(request.PlanId, out var ticket))
        {
            return new PublishResult { PushState = PushDeliveryState.Blocked, Error = "仅上传安全计划不存在、已过期或已被消费，请重新检查待推送提交。" };
        }
        if (!FixedEquals(ticket.CommitOid, request.CommitOid) || !FixedEquals(ticket.TargetFingerprint, request.RemoteTargetFingerprint))
        {
            return PendingFailure(ticket.CommitOid, "仅上传确认数据与安全计划不一致，已拒绝 Push。");
        }
        if (ticket.RequireImageConfirmation != request.RequireImageConfirmation)
        {
            return PendingFailure(ticket.CommitOid, "图片确认策略在准备后发生变化，请重新检查待推送提交。");
        }
        if (ticket.RequireBuildVerification != request.RequireBuildVerification)
        {
            return PendingFailure(ticket.CommitOid, "构建验证策略在准备后发生变化，请重新检查待推送提交。");
        }
        if (ticket.RequireBuildVerification && !FixedEquals(ticket.CommitOid, ticket.BuildVerifiedCommitOid ?? string.Empty))
        {
            return PendingFailure(ticket.CommitOid, "仅上传计划缺少与锁定提交绑定的构建证明，已拒绝 Push。");
        }
        if (ticket.RequireImageConfirmation && ticket.HasOutgoingImages && !request.ImageConfirmed)
        {
            return PendingFailure(ticket.CommitOid, "待推送历史包含图片，必须在本次仅上传确认页确认已脱敏后才能 Push。");
        }
        var root = await ResolveCanonicalRootAsync(ticket.RepositoryRoot, ct);
        if (!string.Equals(root, ticket.RepositoryRoot, StringComparison.OrdinalIgnoreCase)) return PendingFailure(ticket.CommitOid, "仓库根目录在确认后发生变化，已拒绝 Push。");
        var branchResult = await _git.CurrentBranchResultAsync(root!, ct);
        var branch = branchResult.Success ? branchResult.Stdout.FirstOrDefault()?.Trim() : null;
        var headResult = await _git.HeadOidResultAsync(root!, ct);
        var headOid = headResult.Success ? headResult.Stdout.FirstOrDefault()?.Trim() : null;
        var branchOidResult = await _git.BranchOidResultAsync(root!, ticket.Branch, ct);
        var branchOid = branchOidResult.Success ? branchOidResult.Stdout.FirstOrDefault()?.Trim() : null;
        if (!string.Equals(branch, ticket.Branch, StringComparison.Ordinal) ||
            !IsFullObjectId(headOid) || !string.Equals(headOid, ticket.CommitOid, StringComparison.Ordinal) ||
            !IsFullObjectId(branchOid) || !string.Equals(branchOid, ticket.CommitOid, StringComparison.Ordinal))
        {
            return PendingFailure(ticket.CommitOid, "分支或本地提交在确认后发生变化，已拒绝 Push。请重新检查待推送提交。");
        }

        var targetCheck = await ReadValidatedOriginAsync(root!, ct);
        if (targetCheck.Error != null) return PendingFailure(ticket.CommitOid, targetCheck.Error);
        var exactTargetNow = targetCheck.Remote!.ExactEffectivePushUrl!;
        var fingerprintNow = FingerprintTarget(exactTargetNow);
        targetCheck.Remote.ClearExactUrls();
        // 票据只持有 SHA-256 指纹，既不公开也不在内存中跨确认页保留精确 URL/凭据。
        // 执行阶段重读、校验当前 exact target，并只在本次 git 调用期间使用。
        if (!FixedEquals(ticket.TargetFingerprint, fingerprintNow))
        {
            return PendingFailure(ticket.CommitOid, "origin push 目标在确认后发生变化，已拒绝 Push。");
        }

        var remoteNow = await ReadRemoteBranchSnapshotAsync(root!, exactTargetNow, ticket.Branch, ct);
        if (remoteNow.Error != null) return PendingFailure(ticket.CommitOid, "执行前无法核验 origin 远端分支，已在 Push 启动前阻断：" + remoteNow.Error);
        if (!string.Equals(ticket.RemoteOid, remoteNow.Oid, StringComparison.Ordinal))
        {
            if (string.Equals(ticket.CommitOid, remoteNow.Oid, StringComparison.Ordinal))
            {
                return new PublishResult { Pushed = true, PushState = PushDeliveryState.AlreadyUploaded, CommitOid = ticket.CommitOid, CommitShortHash = ShortOid(ticket.CommitOid), Informational = true };
            }
            return PendingFailure(ticket.CommitOid, "origin 远端分支在确认后发生变化，已拒绝 Push。请重新检查待推送提交。");
        }

        log?.Invoke(LogLevel.Info, $"仅上传已锁定提交：{ShortOid(ticket.CommitOid)}:refs/heads/{ticket.Branch}");
        var push = await _git.PushExplicitTargetAsync(root!, exactTargetNow, ticket.CommitOid, ticket.Branch, ct);
        if (push.Canceled || push.TimedOut)
        {
            return UnknownPush(ticket.CommitOid, "Push 已取消或超时，远端是否接收提交未知。禁止直接重试，请先重新检查待推送提交。", push.Canceled);
        }
        if (!push.Success)
        {
            return UnknownPush(ticket.CommitOid,
                "git push 未返回可确认的成功结果，远端是否已接收提交未知。禁止直接重试，请先重新检查待推送提交：\n" + Describe(push),
                push.Canceled);
        }

        var remoteAfter = await ReadRemoteBranchSnapshotAsync(root!, exactTargetNow, ticket.Branch, CancellationToken.None);
        if (remoteAfter.Error != null || !string.Equals(remoteAfter.Oid, ticket.CommitOid, StringComparison.Ordinal))
        {
            return UnknownPush(ticket.CommitOid, "git push 返回成功，但无法确认 origin 已接收锁定提交。禁止直接重试，请先重新核验远端。");
        }
        return new PublishResult { Pushed = true, PushState = PushDeliveryState.Pushed, CommitOid = ticket.CommitOid, CommitShortHash = ShortOid(ticket.CommitOid) };
    }

    private async Task<(RemoteInfo? Remote, string? Error)> ReadValidatedOriginAsync(string root, CancellationToken ct)
    {
        var result = await _git.RemoteVAsync(root, ct);
        if (!result.Success) return (null, "读取 origin 配置失败，已按安全策略中止：" + Describe(result));
        var remote = GitRepositoryInspector.ParseRemoteV(result.Stdout);
        if (!remote.HasRemote) return (null, "未配置 origin；其他 remote 名称不满足安全发布合同。");
        if (remote.IsMalformed || string.IsNullOrWhiteSpace(remote.ExactEffectivePushUrl))
        {
            return (null, "origin push 目标异常，已中止：" + (remote.MalformedReason.Length > 0 ? remote.MalformedReason : "缺少可用 URL。"));
        }
        return (remote, null);
    }

    private async Task<PublishResult> AbortAndRestoreAsync(string root, string snapshotOid, string reason, Action<LogLevel, string>? log, bool canceled = false)
    {
        var restore = await _git.RestoreIndexTreeAsync(root, snapshotOid, CancellationToken.None);
        if (!restore.Success)
        {
            var error = reason + "\n严重：恢复操作前暂存区失败，当前 index 状态不可确认，请勿继续发布：" + Describe(restore);
            log?.Invoke(LogLevel.Blocked, error);
            return new PublishResult { Canceled = canceled, Error = error };
        }

        log?.Invoke(LogLevel.Info, "已恢复操作前的暂存区快照（工作区文件未改动）。");
        return new PublishResult
        {
            Canceled = canceled,
            UnstagedAfterBlocked = true,
            IndexRestoredAfterAbort = true,
            Error = reason + "\n已恢复操作前的暂存状态，未清空用户原有暂存内容。"
        };
    }

    private static PublishResult CommandFailure(string operation, CommandResult result) =>
        new() { Canceled = result.Canceled, Error = operation + "失败，已按安全策略中止：" + Describe(result) };

    private static PublishResult CommittedFailure(string? commitOid, string error) =>
        new()
        {
            Committed = true,
            CommitOid = commitOid,
            CommitShortHash = ShortOid(commitOid),
            PushState = PushDeliveryState.Pending,
            RequiresPushReconciliation = true,
            Error = error
        };

    private static PublishResult UnverifiedCommitFailure(string? commitOid, string error) =>
        new()
        {
            Committed = true,
            CommitCreatedButUnverified = true,
            CommitOid = commitOid,
            CommitShortHash = ShortOid(commitOid),
            PushState = PushDeliveryState.None,
            Error = error
        };

    private static PublishResult PendingFailure(string commitOid, string error) =>
        new()
        {
            CommitOid = commitOid,
            CommitShortHash = ShortOid(commitOid),
            PushState = PushDeliveryState.Blocked,
            Error = error
        };

    private static PublishResult UnknownPush(string commitOid, string error, bool canceled = false, bool committed = false) =>
        new()
        {
            Committed = committed,
            CommitOid = commitOid,
            CommitShortHash = ShortOid(commitOid),
            Canceled = canceled,
            PushState = PushDeliveryState.Unknown,
            RequiresPushReconciliation = true,
            Error = error
        };

    private async Task<string?> ResolveCanonicalRootAsync(string requestedRoot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot)) return null;
        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!Directory.Exists(candidate)) return null;
        var topLevel = await _git.GetTopLevelAsync(candidate, ct);
        if (string.IsNullOrWhiteSpace(topLevel)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(topLevel));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<RemoteBranchSnapshot> ReadRemoteBranchSnapshotAsync(string root, string exactTarget, string branch, CancellationToken ct)
    {
        var result = await _git.RemoteBranchOidAsync(root, exactTarget, branch, ct);
        if (!result.Success)
        {
            return new RemoteBranchSnapshot(null, Describe(result), result.Canceled, result.TimedOut);
        }
        var rows = result.Stdout.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (rows.Count == 0) return new RemoteBranchSnapshot(null, null);
        if (rows.Count != 1) return new RemoteBranchSnapshot(null, "origin 返回多个同名远端分支记录，无法唯一确认目标。");
        var fields = rows[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || fields[0].Length is not (40 or 64) || !fields[0].All(Uri.IsHexDigit) ||
            !string.Equals(fields[1], $"refs/heads/{branch}", StringComparison.Ordinal))
        {
            return new RemoteBranchSnapshot(null, "origin 远端分支输出格式异常。");
        }
        return new RemoteBranchSnapshot(fields[0], null);
    }

    private async Task<(ExistingPushDisposition Disposition, string Message)> ClassifyRemoteRelationAsync(
        string root, string? remoteOid, string localOid, CancellationToken ct)
    {
        if (remoteOid == null) return (ExistingPushDisposition.Ready, "远端分支尚不存在，可执行首次 Push。");
        if (string.Equals(remoteOid, localOid, StringComparison.Ordinal))
        {
            return (ExistingPushDisposition.AlreadyUploaded, "origin 已指向当前本地提交，无需重复 Push。");
        }

        var remoteIsAncestor = await _git.IsAncestorAsync(root, remoteOid, localOid, ct);
        if (remoteIsAncestor.Success) return (ExistingPushDisposition.Ready, "origin 是当前本地提交的祖先，可执行快进 Push。");
        if (remoteIsAncestor.Canceled || remoteIsAncestor.TimedOut || remoteIsAncestor.ExitCode != 1)
        {
            return (ExistingPushDisposition.Unknown, "无法确认远端提交与本地提交的祖先关系：" + Describe(remoteIsAncestor));
        }

        var localIsAncestor = await _git.IsAncestorAsync(root, localOid, remoteOid, ct);
        if (localIsAncestor.Success)
        {
            return (ExistingPushDisposition.AlreadyUploaded, "origin 已包含当前本地提交；请同步远端，不要重复 Push。");
        }
        if (localIsAncestor.Canceled || localIsAncestor.TimedOut || localIsAncestor.ExitCode != 1)
        {
            return (ExistingPushDisposition.Unknown, "无法确认本地提交是否已被远端包含：" + Describe(localIsAncestor));
        }
        return (ExistingPushDisposition.RemoteDrift, "本地与 origin 已分叉，禁止仅上传；请先同步并人工处理历史。");
    }

    private static ExistingPushPlan ExistingPlan(ExistingPushDisposition disposition, string root = "",
        string branch = "", string? commitOid = null, string? remoteOid = null, string remoteDisplay = "（未配置）",
        string targetFingerprint = "", int outgoingCommitCount = 0, bool hasImages = false,
        bool requiresImageConfirmation = false, string message = "") => new()
        {
            Disposition = disposition,
            RepositoryRoot = root,
            Branch = branch,
            CommitOid = commitOid,
            RemoteOid = remoteOid,
            RemoteDisplay = remoteDisplay,
            RemoteTargetFingerprint = targetFingerprint,
            OutgoingCommitCount = outgoingCommitCount,
            HasOutgoingImages = hasImages,
            RequiresImageConfirmation = requiresImageConfirmation,
            Message = message
        };

    private static string FingerprintTarget(string exactTarget) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exactTarget)));

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsFullObjectId(string? value) =>
        value != null && value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private void PurgeExpiredExistingPushTickets()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _existingPushTickets)
        {
            if (pair.Value.ExpiresAt <= now) _existingPushTickets.TryRemove(pair.Key, out _);
        }
    }

    private static string Describe(CommandResult result)
    {
        if (result.Canceled) return "操作已取消。";
        if (result.TimedOut) return "操作超时。";
        var text = result.StdErrText.Length > 0 ? result.StdErrText : result.StdOutText;
        text = GitRemoteService.RedactOutput(text);
        return text.Length > 0 ? text : $"ExitCode={result.ExitCode?.ToString() ?? "未启动"}";
    }

    private static string FormatSecret(ScanFinding finding) =>
        $"Secret {finding.File}{(finding.Line > 0 ? $"（第{finding.Line}行）" : string.Empty)}{finding.Preview}";

    private static bool IsSecretGateFinding(ScanFinding finding) =>
        finding.Severity is ScanSeverity.Blocked or ScanSeverity.High;

    private static string? ShortOid(string? oid) => string.IsNullOrWhiteSpace(oid) ? null : oid[..Math.Min(8, oid.Length)];

    private static string CreateBlobTempPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SafeGitPublisher", "blob-scan");
        Directory.CreateDirectory(directory);
        while (true)
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".blob");
            if (!File.Exists(path)) return path;
        }
    }

    private static bool TryDeleteBlobTempFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeIncompleteScan(SecretScanner.ScanResult scan) =>
        string.Join("；", scan.FileOutcomes
            .Where(outcome => outcome.Disposition == SecretScanner.ScanFileDisposition.Error)
            .Select(outcome => outcome.Detail));

    private static IEnumerable<string> SplitNullRecords(IEnumerable<string> lines) =>
        lines.SelectMany(line => line.Split('\0', StringSplitOptions.RemoveEmptyEntries));
}
