using System.IO;
using System.Text.RegularExpressions;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 安全提交流程。所有状态类 Git 命令均失败关闭；最终扫描读取 index blob；
/// Push 固定为 origin、当前分支和 HEAD，并在执行前复检 origin 目标与待推送历史。
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

        // CommitAndPush 必须在任何 add/commit 前锁定当前分支和 origin push 目标。
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
        if (string.IsNullOrWhiteSpace(snapshotOid))
        {
            var detail = snapshotResult.Success ? "write-tree 未返回 tree OID。" : Describe(snapshotResult);
            return new PublishResult { Canceled = snapshotResult.Canceled, Error = "创建暂存区安全快照失败，已中止：" + detail };
        }

        string? committedOid = null;
        try
        {
            log?.Invoke(LogLevel.Info, "步骤 3/8  git add --all …");
            var add = await _git.AddAllAsync(root, ct);
            if (!add.Success) return await AbortAndRestoreAsync(root, snapshotOid, $"git add 失败：{Describe(add)}", log);
            log?.Invoke(LogLevel.Pass, "git add 完成");

            log?.Invoke(LogLevel.Info, "步骤 4/8  读取已暂存文件…");
            var cached = await _git.DiffCachedNameStatusAsync(root, ct);
            if (!cached.Success) return await AbortAndRestoreAsync(root, snapshotOid, $"读取暂存区失败：{Describe(cached)}", log);
            var staged = GitRepositoryInspector.ParseDiffCachedNameStatus(cached.Stdout);
            if (staged.Count == 0) return await AbortAndRestoreAsync(root, snapshotOid, "没有可提交的变更（暂存区为空）。", log);

            log?.Invoke(LogLevel.Info, "步骤 5/8  扫描已暂存内容…");
            var stagedScan = await ScanIndexAsync(root, staged, ct);
            if (stagedScan.Error != null) return await AbortAndRestoreAsync(root, snapshotOid, stagedScan.Error, log);

            if (stagedScan.Blocks.Count > 0)
            {
                foreach (var block in stagedScan.Blocks) log?.Invoke(LogLevel.Blocked, block);
                return await AbortAndRestoreAsync(root, snapshotOid, "已暂存内容触发安全阻断：\n" + string.Join("\n", stagedScan.Blocks), log);
            }

            // 锁定已完成最终扫描的精确 index tree。pre-commit/commit-msg hook 若在此后
            // 再次 git add，实际 commit tree 将与该 OID 不一致，必须拒绝成功回包与 Push。
            var scannedTreeResult = await _git.WriteIndexTreeAsync(root, ct);
            var scannedTreeOid = scannedTreeResult.Success ? scannedTreeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(scannedTreeOid))
            {
                var detail = scannedTreeResult.Success ? "write-tree 未返回 tree OID。" : Describe(scannedTreeResult);
                return await AbortAndRestoreAsync(root, snapshotOid, "锁定已扫描暂存内容失败：" + detail, log);
            }

            var imageGate = request.RequireImageConfirmation && staged.Any(change => !change.IsDeletedLike() && change.IsImage) && !request.ImageConfirmed;
            if (imageGate && request.Mode == PublishMode.CommitAndPush)
            {
                return await AbortAndRestoreAsync(root, snapshotOid, "本次提交包含图片，请在确认图片已脱敏后再进行“安全提交并上传”。", log);
            }

            var headBeforeResult = await _git.HeadOidResultAsync(root, ct);
            var headBefore = headBeforeResult.Success ? headBeforeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (!headBeforeResult.Success && headBeforeResult.Canceled)
            {
                return await AbortAndRestoreAsync(root, snapshotOid, "读取提交前 HEAD 已取消。", log, canceled: true);
            }
            if (!headBeforeResult.Success && (headBeforeResult.TimedOut || headBeforeResult.ExitCode is null || headBeforeResult.ExitCode != 128))
            {
                return await AbortAndRestoreAsync(root, snapshotOid, "读取提交前 HEAD 失败：" + Describe(headBeforeResult), log);
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
                        CommitShortHash = ShortOid(headAfter),
                        Canceled = commit.Canceled,
                        Error = "git commit 返回失败，但 HEAD 已发生变化。为避免重复提交，已保留新提交并停止后续 Push，请人工复核 hooks 输出：\n" + Describe(commit)
                    };
                }
                return await AbortAndRestoreAsync(root, snapshotOid, "git commit 失败：" + Describe(commit), log);
            }

            if (!createdCommit)
            {
                return await AbortAndRestoreAsync(root, snapshotOid, "git commit 虽返回成功，但 HEAD 未生成新提交；已中止并恢复原暂存区。", log);
            }

            var committedTreeResult = await _git.HeadTreeOidResultAsync(root, CancellationToken.None);
            var committedTreeOid = committedTreeResult.Success ? committedTreeResult.Stdout.FirstOrDefault()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(committedTreeOid))
            {
                return UnverifiedCommitFailure(ShortOid(headAfter), "已生成本地提交，但无法校验实际 commit tree，已拒绝继续 Push：" + Describe(committedTreeResult));
            }
            if (!string.Equals(scannedTreeOid, committedTreeOid, StringComparison.Ordinal))
            {
                return UnverifiedCommitFailure(ShortOid(headAfter),
                    "已生成本地提交，但 hook 在安全扫描后改写了提交内容。该提交未通过安全门禁，已拒绝成功回包与 Push，请人工检查 hooks 及本地 HEAD。");
            }

            var shortHash = ShortOid(headAfter);
            log?.Invoke(LogLevel.Pass, $"已提交：{shortHash}  {message}");
            if (request.Mode == PublishMode.CommitOnly)
            {
                return new PublishResult { Committed = true, CommitShortHash = shortHash };
            }

            // 立即执行前再次验证分支与 origin；不允许配置在确认后漂移。
            var branchNowResult = await _git.CurrentBranchResultAsync(root, ct);
            if (!branchNowResult.Success) return CommittedFailure(shortHash, "已提交，但推送前读取当前分支失败：" + Describe(branchNowResult));
            var branchNow = branchNowResult.Stdout.FirstOrDefault()?.Trim();
            if (!string.Equals(plannedBranch, branchNow, StringComparison.Ordinal))
            {
                return CommittedFailure(shortHash, $"已提交，但当前分支从 {plannedBranch} 变为 {branchNow ?? "detached HEAD"}，已拒绝 Push。");
            }

            var targetNow = await ReadValidatedOriginAsync(root, ct);
            if (targetNow.Error != null) return CommittedFailure(shortHash, "已提交，但 " + targetNow.Error);
            var exactTargetNow = targetNow.Remote!.ExactEffectivePushUrl;
            targetNow.Remote.ClearExactUrls();
            if (!string.Equals(plannedTarget, exactTargetNow, StringComparison.Ordinal))
            {
                return CommittedFailure(shortHash, "已提交，但 origin push 目标在确认后发生变化，已拒绝 Push。当前安全显示：" + targetNow.Remote.EffectivePushDisplay);
            }

            log?.Invoke(LogLevel.Info, "Push 前复检本地未推送历史中的 Secret、敏感路径与超大 blob…");
            var outgoing = await ScanOutgoingHistoryAsync(root, plannedBranch!, exactTargetNow!, ct);
            if (outgoing.Error != null) return CommittedFailure(shortHash, outgoing.Error);
            if (outgoing.Blocks.Count > 0)
            {
                foreach (var block in outgoing.Blocks) log?.Invoke(LogLevel.Blocked, block);
                return CommittedFailure(shortHash, "待推送历史触发安全阻断，提交保留在本地，未执行 Push：\n" + string.Join("\n", outgoing.Blocks));
            }

            var upstream = await _git.UpstreamResultAsync(root, ct);
            var hasUpstream = upstream.Success;
            if (!hasUpstream && upstream.Canceled)
            {
                return CommittedFailure(shortHash, "读取 upstream 状态已取消，未执行 Push。");
            }
            if (!hasUpstream && upstream.ExitCode is null)
            {
                return CommittedFailure(shortHash, "读取 upstream 状态失败，未执行 Push：" + Describe(upstream));
            }
            log?.Invoke(LogLevel.Info, $"执行显式发布：origin / HEAD:refs/heads/{plannedBranch}");
            // 使用已验证的精确 URL，而不是 remote 名称，避免 pushurl 多值或确认后配置竞争改变实际网络目标。
            var push = await _git.PushExplicitTargetAsync(root, exactTargetNow!, plannedBranch!, ct);
            plannedTarget = null;
            exactTargetNow = null;
            if (push.Canceled || push.TimedOut)
            {
                return new PublishResult
                {
                    Committed = true,
                    CommitShortHash = shortHash,
                    Canceled = push.Canceled,
                    Error = "推送被取消或超时，远端是否已接收提交无法确认。提交已保留在本地，请先同步远端状态，勿直接重复 Push。"
                };
            }
            if (!push.Success) return CommittedFailure(shortHash, "git push 失败（提交保留在本地）：\n" + Describe(push));

            if (!hasUpstream)
            {
                var setUpstream = await _git.SetOriginUpstreamAsync(root, plannedBranch!, ct);
                if (!setUpstream.Success)
                {
                    return new PublishResult
                    {
                        Committed = true,
                        Pushed = true,
                        CommitShortHash = shortHash,
                        Error = "Push 已成功，但设置 origin upstream 失败；不会重复 Push，请人工检查分支跟踪配置：" + Describe(setUpstream)
                    };
                }
            }

            log?.Invoke(LogLevel.Pass, "git push 成功");
            return new PublishResult { Committed = true, Pushed = true, CommitShortHash = shortHash, UsedSetUpstream = !hasUpstream };
        }
        catch (OperationCanceledException)
        {
            return await AbortAndRestoreAsync(root, snapshotOid, "发布已取消。", log, canceled: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProcessLaunchException or InvalidDataException or ArgumentException)
        {
            var detail = $"发布流程异常中止（{ex.GetType().Name}）。";
            return committedOid == null
                ? await AbortAndRestoreAsync(root, snapshotOid, detail, log)
                : CommittedFailure(ShortOid(committedOid), detail + "本地提交可能已生成，未执行 Push，请人工复核。");
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

    private async Task<(List<string> Blocks, string? Error)> ScanOutgoingHistoryAsync(string root, string branch, string exactTarget, CancellationToken ct)
    {
        var remoteRef = await _git.RemoteBranchOidAsync(root, exactTarget, branch, ct);
        if (!remoteRef.Success) return (new List<string>(), "无法读取 origin 远端分支状态，已拒绝 Push：" + Describe(remoteRef));
        var remoteRows = remoteRef.Stdout.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        if (remoteRows.Count > 1) return (new List<string>(), "origin 返回多个同名远端分支记录，无法唯一确定待推送范围，已拒绝 Push。");

        string? remoteOid = null;
        if (remoteRows.Count == 1)
        {
            var fields = remoteRows[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0].Length is < 40 or > 64 || !fields[0].All(Uri.IsHexDigit))
            {
                return (new List<string>(), "origin 远端分支 OID 格式异常，已拒绝 Push。");
            }
            remoteOid = fields[0];
        }

        var outgoing = await _git.OutgoingCommitsFromRemoteOidAsync(root, remoteOid, ct);
        if (!outgoing.Success) return (new List<string>(), "无法确定待推送提交范围，已拒绝 Push：" + Describe(outgoing));
        var commits = outgoing.Stdout.Select(line => line.Trim()).Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (commits.Count == 0) return (new List<string>(), null);

        // 首次发布会扫描 HEAD 全历史。设置合理上限，避免 UI 因超大历史长时间假死；
        // 超限不是放行条件，而是 fail-closed，要求先人工审计或缩小历史。
        if (commits.Count > 5000)
        {
            return (new List<string>(), $"待推送提交共 {commits.Count} 个，超过自动安全复检上限 5000，已拒绝 Push。请先人工审计历史。");
        }

        var blocks = new List<string>();
        var seenBlobs = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var commit in commits)
        {
            var tree = await _git.ListCommitBlobsAsync(root, commit, ct);
            if (!tree.Success) return (blocks, $"无法读取待推送提交 {ShortOid(commit)} 的 tree，已拒绝 Push：{Describe(tree)}");

            foreach (var record in SplitNullRecords(tree.Stdout))
            {
                var match = TreeRecordRegex.Match(record);
                if (!match.Success || !long.TryParse(match.Groups["size"].Value, out var size))
                {
                    return (blocks, $"待推送 tree 输出格式异常（提交 {ShortOid(commit)}），已拒绝 Push。");
                }

                var oid = match.Groups["oid"].Value;
                var path = match.Groups["path"].Value;
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
                    return (blocks, $"创建历史 Secret 扫描临时文件失败（{ex.GetType().Name}），已拒绝 Push。");
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
                if (scanError != null) return (blocks, scanError);
            }
        }
        return (blocks.Distinct(StringComparer.Ordinal).ToList(), null);
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

    private static PublishResult CommittedFailure(string? shortHash, string error) =>
        new() { Committed = true, CommitShortHash = shortHash, Error = error };

    private static PublishResult UnverifiedCommitFailure(string? shortHash, string error) =>
        new() { Committed = true, CommitCreatedButUnverified = true, CommitShortHash = shortHash, Error = error };

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
