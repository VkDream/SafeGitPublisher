using System.IO;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 发布前检查的完整上下文（结果载体）。
/// </summary>
public sealed class PreflightContext
{
    /// <summary>用户选择的路径。</summary>
    public required string ProjectPath { get; init; }

    /// <summary>仓库根目录（git rev-parse --show-toplevel）。非仓库时为 null。</summary>
    public string? RepositoryRoot { get; set; }

    public required AppSettings Settings { get; init; }

    public string GitVersion { get; set; } = string.Empty;

    public GitIdentityInfo? Identity { get; set; }

    public RemoteInfo? Remote { get; set; }

    public string? Branch { get; set; }

    public bool HasUpstream { get; set; }

    public List<GitFileChange> Changes { get; } = new();

    public List<ScanFinding> SecretFindings { get; } = new();

    public List<ScanFinding> SensitiveFindings { get; } = new();

    public List<ScanFinding> LargeFindings { get; } = new();

    public List<string> IgnoredSafePaths { get; } = new();

    /// <summary>已安全忽略总数（可能数百个，用于摘要显示）。</summary>
    public int IgnoredSafePathTotal => IgnoredSafePaths.Count;

    /// <summary>已安全忽略展示子集（最多 50 条，避免生成文件刷爆 UI）。</summary>
    public IReadOnlyList<string> IgnoredSafePathsDisplay => IgnoredSafePaths.Take(50).ToList();

    public BuildResult? Build { get; set; }

    public List<GitFileChange> NewImages { get; } = new();

    public PreflightReport Report { get; set; } = new();

    public bool DotNetProject { get; set; }

    /// <summary>图片确认状态（由 UI 维护，检查时读取）。</summary>
    public bool ImageConfirmed { get; set; }
}

/// <summary>
/// 发布前检查编排：依次执行各检查并汇总为 PreflightReport。
/// 判断逻辑全部集中在此，UI 不散落判断。
/// </summary>
public sealed class PreflightService
{
    private readonly GitService _git;
    private readonly SensitiveFileScanner _sensitiveScanner;
    private readonly SecretScanner _secretScanner;
    private readonly LargeFileScanner _largeFileScanner;
    private readonly DotNetBuildService _buildService;

    public PreflightService(
        GitService git,
        SensitiveFileScanner sensitiveScanner,
        SecretScanner secretScanner,
        LargeFileScanner largeFileScanner,
        DotNetBuildService buildService)
    {
        _git = git;
        _sensitiveScanner = sensitiveScanner;
        _secretScanner = secretScanner;
        _largeFileScanner = largeFileScanner;
        _buildService = buildService;
    }

    /// <summary>
    /// 执行全部发布前检查。
    /// </summary>
    public async Task<PreflightContext> RunAsync(
        string projectPath,
        AppSettings settings,
        Action<LogLevel, string>? log = null,
        bool imageConfirmed = false,
        CancellationToken ct = default)
    {
        var ctx = new PreflightContext
        {
            ProjectPath = projectPath,
            Settings = settings,
            ImageConfirmed = imageConfirmed
        };

        var report = ctx.Report;

        void Add(string id, string name, CheckStatus status, string summary, string? fix = null, bool? blocksCommit = null, bool? blocksPush = null, bool requiresConfirmation = false, string details = "")
        {
            report.Checks.Add(new PreflightCheck
            {
                Id = id,
                Name = name,
                Status = status,
                Summary = summary,
                FixLabel = fix ?? string.Empty,
                BlocksCommit = blocksCommit ?? false,
                BlocksPush = blocksPush ?? false,
                RequiresConfirmation = requiresConfirmation,
                Details = details
            });
        }

        // ---------- 0) git 可用性 ----------
        var gitVersion = await _git.GetVersionAsync(ct);
        if (gitVersion == null)
        {
            Add("git_available", "Git 环境", CheckStatus.Blocked,
                "Git CLI 未找到，请安装 Git for Windows 并加入 PATH。",
                blocksCommit: true, blocksPush: true);
            log?.Invoke(LogLevel.Blocked, "Git CLI 未找到");
            return ctx;
        }

        ctx.GitVersion = gitVersion;
        Add("git_available", "Git 环境", CheckStatus.Pass, $"git {gitVersion} 已找到");
        log?.Invoke(LogLevel.Pass, $"Git CLI 可用（{gitVersion}）");

        // ---------- 1) 仓库检测 ----------
        var topLevel = await _git.GetTopLevelAsync(projectPath, ct);
        if (topLevel == null)
        {
            ctx.RepositoryRoot = null;
            Add("repo_detected", "Git 仓库", CheckStatus.Blocked,
                "当前目录不是 Git 仓库。",
                fix: "初始化 Git 仓库", blocksCommit: true, blocksPush: true,
                details: "可点击“初始化 Git 仓库”执行 git init（仅初始化，不会自动 add/commit）。");
            log?.Invoke(LogLevel.Blocked, "当前目录不是 Git 仓库");
            return ctx;
        }

        ctx.RepositoryRoot = topLevel;
        var isInside = !string.Equals(
            topLevel.TrimEnd('\\', '/'),
            projectPath.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

        if (isInside)
        {
            Add("repo_detected", "Git 仓库", CheckStatus.Info,
                $"位于仓库内部，根目录：{topLevel}");
        }
        else
        {
            Add("repo_detected", "Git 仓库", CheckStatus.Pass, "已识别仓库根目录");
        }
        log?.Invoke(isInside ? LogLevel.Info : LogLevel.Pass, isInside
            ? $"所选路径在仓库内部，实际根目录：{topLevel}"
            : "Git 仓库检测通过");

        var root = topLevel;

        // ---------- 2) 工作区状态 ----------
        var status = await _git.StatusPorcelainAsync(root, ct);
        if (status.Canceled)
        {
            log?.Invoke(LogLevel.Info, "检查已取消");
            ctx.Report = report;
            return ctx;
        }

        var changes = GitRepositoryInspector.ParseStatusPorcelain(status.Stdout);
        ctx.Changes.Clear();
        ctx.Changes.AddRange(changes);

        var conflicts = changes.Where(c => c.Risk == RiskLevel.Blocked && c.StatusLabel == "冲突").ToList();
        if (conflicts.Count > 0)
        {
            Add("status", "工作区状态", CheckStatus.Blocked,
                $"存在 {conflicts.Count} 个合并冲突，请先解决。",
                blocksCommit: true, blocksPush: true,
                details: string.Join("\n", conflicts.Select(c => c.Path)));
            log?.Invoke(LogLevel.Blocked, $"发现合并冲突：{conflicts[0].Path} 等");
        }
        else
        {
            Add("status", "工作区状态", CheckStatus.Pass,
                $"{changes.Count} 个变更（新增/修改/删除/未跟踪）");
            log?.Invoke(LogLevel.Pass, $"工作区状态检查通过：{changes.Count} 个变更");
        }

        // 变更文件大小（供显示）
        _largeFileScanner.PopulateSizes(root, changes);

        // ---------- 3) .gitignore ----------
        var gitignorePath = Path.Combine(root, ".gitignore");
        if (!File.Exists(gitignorePath))
        {
            Add("gitignore", ".gitignore", CheckStatus.Warning,
                "缺少 .gitignore，建议生成推荐规则。",
                fix: "生成推荐 .gitignore",
                details: "生成将只追加缺失规则，不会覆盖已有内容。");
            log?.Invoke(LogLevel.Warn, ".gitignore 不存在，建议生成");
        }
        else
        {
            var existing = await File.ReadAllTextAsync(gitignorePath, ct);
            var missing = GitIgnoreService.ComputeMissingRules(existing, GitIgnoreService.RequiredRules);
            if (missing.Count == 0)
            {
                Add("gitignore", ".gitignore", CheckStatus.Pass, "推荐规则已覆盖");
            }
            else
            {
                Add("gitignore", ".gitignore", CheckStatus.Warning,
                    $"存在，但缺少 {missing.Count} 条推荐规则",
                    fix: "补充推荐规则",
                    details: string.Join("\n", missing));
                log?.Invoke(LogLevel.Warn, $".gitignore 缺少 {missing.Count} 条推荐规则");
            }
        }

        // ---------- 4) 敏感文件 ----------
        var tracked = GitRepositoryInspector.ParseLsFiles((await _git.LsFilesAsync(root, ct)).Stdout);
        var sensitive = await _sensitiveScanner.ScanAsync(root, changes, tracked, ct);
        ctx.SensitiveFindings.AddRange(sensitive.Findings);
        ctx.IgnoredSafePaths.AddRange(sensitive.IgnoredSafePaths);

        foreach (var f in sensitive.Findings)
        {
            var change = changes.FirstOrDefault(c => c.Path.Equals(f.File, StringComparison.OrdinalIgnoreCase));
            if (change != null) change.Risk = RiskLevel.Blocked;
        }

        var blockedSensitive = sensitive.Findings.Where(f => f.Severity == ScanSeverity.Blocked).ToList();
        if (blockedSensitive.Count > 0)
        {
            Add("sensitive_files", "敏感文件", CheckStatus.Blocked,
                $"发现 {blockedSensitive.Count} 个敏感文件（数据库/密钥/本地输出等）",
                blocksCommit: true, blocksPush: true,
                details: string.Join("\n", blockedSensitive.Select(f => $"{f.File}：{f.Message}")));
            log?.Invoke(LogLevel.Blocked, $"敏感文件阻断：{blockedSensitive[0].File}");
        }
        else
        {
            var ignoredNote = sensitive.IgnoredSafePaths.Count > 0
                ? $"（{sensitive.IgnoredSafePaths.Count} 个危险文件已被 .gitignore 排除：{string.Join("、", sensitive.IgnoredSafePaths.Take(5))}）"
                : string.Empty;
            Add("sensitive_files", "敏感文件", CheckStatus.Pass, "未发现提交风险" + ignoredNote);
            if (sensitive.IgnoredSafePaths.Count > 0)
            {
                log?.Invoke(LogLevel.Pass, $"{sensitive.IgnoredSafePaths[0]} 已被 .gitignore 排除");
            }
            else
            {
                log?.Invoke(LogLevel.Pass, "敏感文件检查通过");
            }
        }

        // ---------- 5) Secret 扫描 ----------
        var secretTargets = changes
            .Where(c => !c.IsDeletedLike())
            .Select(c => c.Path)
            .ToList();

        var secretResult = await _secretScanner.ScanFilesAsync(root, secretTargets, ct);
        ctx.SecretFindings.AddRange(secretResult.Findings);

        var secrets = secretResult.Findings;
        var blockedSecrets = secrets.Where(f => f.Severity == ScanSeverity.Blocked).ToList();
        var highSecrets = secrets.Where(f => f.Severity == ScanSeverity.High).ToList();
        var warnSecrets = secrets.Where(f => f.Severity == ScanSeverity.Warning).ToList();

        if (blockedSecrets.Count > 0)
        {
            Add("secret_scan", "Secret 扫描", CheckStatus.Blocked,
                $"发现 {blockedSecrets.Count} 处疑似真实 Token（已脱敏）",
                blocksCommit: true, blocksPush: true,
                details: string.Join("\n", blockedSecrets.Select(f => $"{f.File} (第{f.Line}行) {f.Preview}")));
            log?.Invoke(LogLevel.Blocked, $"Secret 扫描阻断：{blockedSecrets[0].File}");
        }
        else if (highSecrets.Count > 0)
        {
            Add("secret_scan", "Secret 扫描", CheckStatus.Warning,
                $"发现 {highSecrets.Count} 处疑似明文凭据，请人工确认",
                details: string.Join("\n", highSecrets.Select(f => $"{f.File} {f.Preview}")));
            log?.Invoke(LogLevel.Warn, $"Secret 扫描发现 {highSecrets.Count} 处高危疑似凭据");
        }
        else if (warnSecrets.Count > 0)
        {
            Add("secret_scan", "Secret 扫描", CheckStatus.Warning,
                $"发现 {warnSecrets.Count} 处需注意项（内网地址等）",
                details: string.Join("\n", warnSecrets.Select(f => $"{f.File} {f.Preview}")));
            log?.Invoke(LogLevel.Warn, $"Secret 扫描发现 {warnSecrets.Count} 处警告项");
        }
        else
        {
            Add("secret_scan", "Secret 扫描", CheckStatus.Pass, "未发现凭据（共扫描 " + secretTargets.Count + " 个文件）");
            log?.Invoke(LogLevel.Pass, "Secret 扫描通过");
        }

        // ---------- 6) 大文件 ----------
        var largeFindings = _largeFileScanner.Scan(root, changes);
        ctx.LargeFindings.AddRange(largeFindings);
        var largeBlocked = largeFindings.Where(f => f.Severity == ScanSeverity.Blocked).ToList();
        var largeWarn = largeFindings.Where(f => f.Severity == ScanSeverity.Warning).ToList();
        if (largeBlocked.Count > 0)
        {
            Add("large_files", "大文件检查", CheckStatus.Blocked,
                $"存在 {largeBlocked.Count} 个超大文件（> {settings.LargeFileBlockingMB:F0} MB）",
                blocksCommit: true, blocksPush: true,
                details: string.Join("\n", largeBlocked.Select(f => $"{f.File} {f.Message}")));
            log?.Invoke(LogLevel.Blocked, $"大文件阻断：{largeBlocked[0].File}");
        }
        else if (largeWarn.Count > 0)
        {
            Add("large_files", "大文件检查", CheckStatus.Warning,
                $"存在 {largeWarn.Count} 个大文件需注意",
                details: string.Join("\n", largeWarn.Select(f => $"{f.File} {f.Message}")));
            log?.Invoke(LogLevel.Warn, $"大文件警告：{largeWarn.Count} 个");
        }
        else
        {
            Add("large_files", "大文件检查", CheckStatus.Pass, "未发现异常");
            log?.Invoke(LogLevel.Pass, "大文件检查通过");
        }

        // ---------- 7) Git 身份 ----------
        var identity = await new GitIdentityService(_git)
            .GetIdentityAsync(root, settings.RecommendedGitName, settings.RecommendedGitEmail, ct);
        ctx.Identity = identity;

        if (identity.HasIssue)
        {
            var issueDetail = new List<string>();
            if (!identity.NameMatches) issueDetail.Add($"name：当前 {identity.NameDisplay}（{identity.NameSourceDisplay}），推荐 {settings.RecommendedGitName}");
            if (!identity.EmailMatches) issueDetail.Add($"email：当前 {identity.EmailDisplay}（{identity.EmailSourceDisplay}），推荐 {settings.RecommendedGitEmail}");
            Add("git_identity", "Git 作者", CheckStatus.Warning,
                "身份与推荐配置不一致",
                fix: "修正为推荐身份",
                details: string.Join("\n", issueDetail));
            log?.Invoke(LogLevel.Warn, "Git 作者身份与推荐配置不一致");
        }
        else
        {
            Add("git_identity", "Git 作者", CheckStatus.Pass,
                $"{identity.Name} <{identity.Email}>（{identity.NameSourceDisplay}）");
            log?.Invoke(LogLevel.Pass, $"Git 作者检查通过：{identity.Name}");
        }

        // ---------- 8) Remote ----------
        var remoteInfo = GitRepositoryInspector.ParseRemoteV((await _git.RemoteVAsync(root, ct)).Stdout);
        ctx.Remote = remoteInfo;

        if (!remoteInfo.HasRemote)
        {
            Add("remote", "Remote", CheckStatus.Warning,
                "未配置 origin，无法 Push",
                fix: "设置 origin",
                blocksPush: true,
                details: "可在设置 origin 对话框中输入 https://github.com/你的账号/仓库名.git");
            log?.Invoke(LogLevel.Warn, "未配置 origin，Push 不可用");
        }
        else if (remoteInfo.IsMalformed)
        {
            Add("remote", "Remote", CheckStatus.Blocked,
                $"remote 地址异常：{remoteInfo.MalformedReason}",
                fix: "修复 remote 地址",
                blocksPush: true,
                details: $"当前：{remoteInfo.FetchUrl}\n建议：{remoteInfo.SuggestedUrl}");
            log?.Invoke(LogLevel.Blocked, $"remote 地址异常：{remoteInfo.FetchUrl}");
        }
        else
        {
            Add("remote", "Remote", CheckStatus.Pass,
                $"origin → {remoteInfo.DisplayName}");
            log?.Invoke(LogLevel.Pass, $"Remote 检查通过：{remoteInfo.FetchUrl}");
        }

        // ---------- 9) 分支 ----------
        var branch = await _git.CurrentBranchAsync(root, ct);
        ctx.Branch = branch;
        ctx.HasUpstream = await _git.HasUpstreamAsync(root, ct);

        if (string.IsNullOrWhiteSpace(branch))
        {
            Add("branch", "分支", CheckStatus.Info, "当前处于 detached HEAD，建议切回分支");
        }
        else if (branch.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            Add("branch", "分支", CheckStatus.Info,
                $"当前分支：{branch}（推荐 main；不自动改名）");
            log?.Invoke(LogLevel.Info, $"当前分支 {branch}（推荐 main）");
        }
        else
        {
            Add("branch", "分支", CheckStatus.Pass, $"当前分支：{branch}");
            log?.Invoke(LogLevel.Pass, $"分支：{branch}");
        }

        // ---------- 10) 图片隐私 ----------
        var newImages = changes
            .Where(c => c.IsImage && !c.IsDeletedLike())
            .ToList();
        ctx.NewImages.Clear();
        ctx.NewImages.AddRange(newImages);
        foreach (var img in newImages)
        {
            if (img.Risk != RiskLevel.Blocked) img.Risk = RiskLevel.Warning;
        }

        if (newImages.Count > 0)
        {
            var confirmed = settings.RequireImagePrivacyConfirmation && imageConfirmed;
            Add("image_privacy", "图片脱敏确认", CheckStatus.Warning,
                $"{newImages.Count} 张新增/修改图片需要人工确认脱敏",
                blocksPush: settings.RequireImagePrivacyConfirmation && !imageConfirmed,
                requiresConfirmation: true,
                details: "请确认图片中不存在客户名称、内部项目名、真实用户名、邮箱、服务器地址等敏感信息。");
            log?.Invoke(LogLevel.Warn, $"{newImages.Count} 张新图片，需确认已脱敏");
        }
        else
        {
            Add("image_privacy", "图片脱敏确认", CheckStatus.Pass, "本次无新增图片");
        }

        // ---------- 11) 构建 ----------
        // 合同（V1.0.0 self-host 缺陷修复后修正）：
        // Build Gate 只针对“存在可提交变更”的发布候选。
        // 0 个可提交变更 → 不存在即将 commit/push 的代码，无需执行发布前构建门禁。
        // 这不等于 Build PASS，而是 Not Required / Skipped，原因写入 SkipReason。
        var committableCount = changes.Count(c => !c.IsConflict);
        if (committableCount == 0)
        {
            ctx.DotNetProject = DotNetBuildService.IsDotNetProject(root);
            ctx.Build = new BuildResult
            {
                BuildRun = false,
                TargetKind = BuildTargetKind.None,
                SkipReason = "当前无可提交变更（0 个可提交变更），跳过构建门禁（Not Required）。"
            };
            Add("build", "项目构建", CheckStatus.Info, "当前无可提交变更，跳过构建验证",
                details: "当前不存在即将提交/推送的代码，因此无需执行发布前 Build Gate。");
            log?.Invoke(LogLevel.Info, "当前无可提交变更，跳过构建验证");
        }
        else if (DotNetBuildService.IsDotNetProject(root))
        {
            ctx.DotNetProject = true;
            var build = await _buildService.BuildRepositoryAsync(root, !settings.BuildBeforeCommit, ct);
            ctx.Build = build;
            if (build.CleanupFailed)
            {
                log?.Invoke(LogLevel.Warn, $"隔离构建临时目录清理失败（不影响构建结果）：{build.IsolationRoot}");
            }
            if (build.TargetKind == BuildTargetKind.Ambiguous)
            {
                // 合同：多候选且无法自动确定 → Warning + 需要用户明确选择，不猜测
                Add("build", "项目构建", CheckStatus.Warning,
                    "存在多个构建目标，无法自动确定（需人工选择）",
                    details: $"{build.SkipReason}\n本次检查已跳过构建，请确认实际构建目标后重新检查。");
                log?.Invoke(LogLevel.Warn, "构建目标歧义，已跳过构建，需人工选择");
            }
            else if (build.BuildRun)
            {
                var target = build.TargetDisplay ?? "?";
                if (build.Succeeded && build.WarningCount == 0)
                {
                    Add("build", "项目构建", CheckStatus.Pass,
                        $"Build succeeded（{target}，{build.Duration.TotalSeconds:F1}s）");
                    log?.Invoke(LogLevel.Pass, $"dotnet build {target} 通过");
                }
                else if (build.Succeeded)
                {
                    Add("build", "项目构建", CheckStatus.Warning,
                        $"Build succeeded with {build.WarningCount} warnings（{target}）");
                    log?.Invoke(LogLevel.Warn, $"dotnet build {target} 通过，但有 {build.WarningCount} 个警告");
                }
                else
                {
                    var err = build.ErrorLines.Count > 0
                        ? string.Join("\n", build.ErrorLines)
                        : build.Summary;
                    Add("build", "项目构建", CheckStatus.Blocked,
                        $"Build failed，禁止发布（{target}）",
                        blocksPush: true,
                        details: $"Target: {target}\nExit Code: {build.ExitCode}\n{err}");
                    // log 仅输出关键摘要（ExitCode + 前 3 条错误行，不刷大量构建输出）
                    if (build.ErrorLines.Count > 0)
                    {
                        log?.Invoke(LogLevel.Blocked, $"dotnet build {target} 失败（ExitCode={build.ExitCode}）");
                        foreach (var e in build.ErrorLines.Take(3))
                        {
                            log?.Invoke(LogLevel.Error, e);
                        }
                    }
                    else
                    {
                        log?.Invoke(LogLevel.Blocked, $"dotnet build {target} 失败（ExitCode={build.ExitCode}）{build.Summary}");
                    }
                }
            }
            else
            {
                Add("build", "项目构建", CheckStatus.Warning, build.SkipReason);
                log?.Invoke(LogLevel.Warn, build.SkipReason);
            }
        }
        else
        {
            ctx.DotNetProject = false;
            Add("build", "项目构建", CheckStatus.Info, "非 .NET 项目，跳过构建");
            log?.Invoke(LogLevel.Info, "非 .NET 项目，跳过构建");
        }

        // ---------- 汇总 ----------
        ctx.Report = report;

        var hasBlocked = report.HasCommitBlock || report.HasPushBlock;
        if (hasBlocked)
        {
            log?.Invoke(LogLevel.Blocked, "存在阻断项，禁止发布");
        }
        else if (report.HasWarning)
        {
            log?.Invoke(LogLevel.Warn, "存在警告项，请在最终确认页复核");
        }
        else
        {
            log?.Invoke(LogLevel.Pass, "全部检查通过，可以发布");
        }

        return ctx;
    }
}

internal static class GitFileChangeEx
{
    /// <summary>是否属于“不会新增内容”的变更（删除/仅重命名删除侧）。</summary>
    public static bool IsDeletedLike(this GitFileChange c)
    {
        return c.StatusCode.StartsWith("D", StringComparison.Ordinal);
    }
}