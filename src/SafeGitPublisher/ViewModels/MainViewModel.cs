using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using SafeGitPublisher.Converters;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.ViewModels;

/// <summary>
/// 主窗口 ViewModel：项目选择、检查编排、发布流程、日志、设置。
/// 所有 git/dotnet 子进程均异步执行，UI 不会阻塞。
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    // ---------- 服务 ----------
    private readonly ProcessRunner _runner = new();
    private readonly GitService _git;
    private readonly SensitiveFileScanner _sensitiveScanner;
    private readonly SecretScanner _secretScanner = new();
    private readonly LargeFileScanner _largeScanner;
    private readonly DotNetBuildService _buildService;
    private readonly PreflightService _preflight;
    private readonly PublishWorkflowService _publish;
    private readonly SettingsService _settingsService;

    private AppSettings _settings;
    private CancellationTokenSource? _cts;

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
        _git = new GitService(_runner);
        _sensitiveScanner = new SensitiveFileScanner(_git);
        _largeScanner = new LargeFileScanner(
            _settings.LargeFileWarningMB,
            _settings.LargeFileHighWarningMB,
            _settings.LargeFileBlockingMB);
        _buildService = new DotNetBuildService(_runner);
        _preflight = new PreflightService(_git, _sensitiveScanner, _secretScanner, _largeScanner, _buildService);
        _publish = new PublishWorkflowService(_git, _sensitiveScanner, _secretScanner, _largeScanner);

        foreach (var r in _settings.RecentProjects) RecentProjects.Add(r);

        CommitPrefixes.Add(string.Empty);
        CommitPrefixes.Add("feat: ");
        CommitPrefixes.Add("fix: ");
        CommitPrefixes.Add("docs: ");
        CommitPrefixes.Add("refactor: ");
        CommitPrefixes.Add("chore: ");
        CommitPrefixes.Add("test: ");
        SelectedCommitPrefix = string.Empty;

        BrowseProjectCommand = new AsyncRelayCommand(_ => BrowseProjectAsync(), onException: ex => HandleError(ex));
        RunChecksCommand = new AsyncRelayCommand(_ => RunChecksAsync(), onException: ex => HandleError(ex));
        CancelCommand = new RelayCommand(_ => CancelCurrentOperation());
        FixCheckCommand = new AsyncRelayCommand(p => HandleFixAsync(p as string), onException: ex => HandleError(ex));
        // 命令级 Zero Change 防护：CanExecute 直接读取门控状态，
        // 即使按钮被绕过（快捷键 / 直接调用 Execute）也无法越权执行。
        CommitOnlyCommand = new AsyncRelayCommand(_ => PublishAsync(commitOnly: true), _ => CanCommit, onException: ex => HandleError(ex));
        SafeCommitAndPushCommand = new AsyncRelayCommand(_ => PublishAsync(commitOnly: false), _ => CanPush, onException: ex => HandleError(ex));
        SyncRemoteCommand = new AsyncRelayCommand(_ => SyncRemoteAsync(), onException: ex => HandleError(ex));
        ShowReportCommand = new RelayCommand(_ => ShowReportRequested?.Invoke(new ReportData { Context = LastContext ?? new PreflightContext { ProjectPath = string.Empty, Settings = _settings } }));
        ShowSettingsCommand = new AsyncRelayCommand(_ => ShowSettingsAsync(), onException: ex => HandleError(ex));

        _ = LoadEnvironmentAsync();
    }

    // ---------- 事件（由 MainWindow 订阅并展示对话框） ----------
    public event Func<Task<string?>>? BrowseFolderRequested;
    public event Func<ConfirmPublishData, Task<bool>>? ConfirmPublishRequested;
    public event Func<SetOriginData, Task<SetOriginData?>>? SetOriginRequested;
    public event Func<GitignorePreviewData, Task<bool>>? GitignorePreviewRequested;
    public event Func<WizardData, Task<WizardData?>>? WizardRequested;
    public event Action<ReportData>? ShowReportRequested;
    public event Func<SettingsData, Task<bool>>? SettingsRequested;
    public event Action<string, bool>? ShowMessageRequested;

    // ---------- 命令 ----------
    public AsyncRelayCommand BrowseProjectCommand { get; }
    public AsyncRelayCommand RunChecksCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand FixCheckCommand { get; }
    public AsyncRelayCommand CommitOnlyCommand { get; }
    public AsyncRelayCommand SafeCommitAndPushCommand { get; }
    public AsyncRelayCommand SyncRemoteCommand { get; }
    public RelayCommand ShowReportCommand { get; }
    public AsyncRelayCommand ShowSettingsCommand { get; }

    // ---------- 集合 ----------
    public ObservableCollection<PreflightCheck> Checks { get; } = new();
    public ObservableCollection<GitFileChange> Changes { get; } = new();
    public ObservableCollection<LogEntry> Logs { get; } = new();
    public ObservableCollection<string> RecentProjects { get; } = new();
    public ObservableCollection<string> CommitPrefixes { get; } = new();

    // ---------- 属性 ----------
    private string _projectPath = string.Empty;
    public string ProjectPath
    {
        get => _projectPath;
        set
        {
            if (SetProperty(ref _projectPath, value ?? string.Empty))
            {
                // 路径变化后不自动检查，用户点“重新检查”
            }
        }
    }

    private string _commitMessage = string.Empty;
    public string CommitMessage
    {
        get => _commitMessage;
        set
        {
            // 提交说明变化实时影响 CanCommit/CanPush（0 变更时空说明也禁用）
            if (SetProperty(ref _commitMessage, value ?? string.Empty))
            {
                RecomputeReport();
            }
        }
    }

    private string _selectedCommitPrefix = string.Empty;
    public string SelectedCommitPrefix
    {
        get => _selectedCommitPrefix;
        set
        {
            if (SetProperty(ref _selectedCommitPrefix, value ?? string.Empty) && value != null)
            {
                ApplyPrefix(value);
            }
        }
    }

    private string? _selectedRecentProject;
    /// <summary>最近项目下拉选择。选择后自动检查。</summary>
    public string? SelectedRecentProject
    {
        get => _selectedRecentProject;
        set
        {
            SetProperty(ref _selectedRecentProject, value);
            if (value != null && !IsBusy && !string.Equals(value, ProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = SelectAndCheckAsync(value);
            }
            else if (IsBusy)
            {
                // 忙碌中不允许切换：回退显示当前
                OnPropertyChanged(nameof(SelectedRecentProject));
            }
        }
    }

    private string _statusBarText = string.Empty;
    public string StatusBarText
    {
        get => _statusBarText;
        private set => SetProperty(ref _statusBarText, value);
    }

    public int ChecksCount => Checks.Count;

    /// <summary>应用版本（来自程序集元数据，如 v1.0.0）。</summary>
    public string AppVersionText => AppVersionService.DisplayVersion;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanOperate));
                RecomputeReport();
            }
        }
    }

    private string _busyText = string.Empty;
    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    /// <summary>忙碌/检查中时禁止发布操作。</summary>
    public bool CanOperate => !IsBusy;

    private string _gitVersionText = "Git: 检测中…";
    public string GitVersionText
    {
        get => _gitVersionText;
        private set => SetProperty(ref _gitVersionText, value);
    }

    private string _dotNetVersionText = ".NET: 检测中…";
    public string DotNetVersionText
    {
        get => _dotNetVersionText;
        private set => SetProperty(ref _dotNetVersionText, value);
    }

    private bool _canCommit;
    public bool CanCommit
    {
        get => _canCommit;
        private set => SetProperty(ref _canCommit, value);
    }

    private bool _canPush;
    public bool CanPush
    {
        get => _canPush;
        private set => SetProperty(ref _canPush, value);
    }

    // ---------- 提交按钮禁用原因（Tooltip） ----------
    private string _commitTooltip = string.Empty;
    public string CommitTooltip
    {
        get => _commitTooltip;
        private set => SetProperty(ref _commitTooltip, value);
    }

    private string _pushTooltip = string.Empty;
    public string PushTooltip
    {
        get => _pushTooltip;
        private set => SetProperty(ref _pushTooltip, value);
    }

    /// <summary>真正可提交的变更数（排除冲突条目）。</summary>
    public int CommittableChangeCount => Changes.Count(c => !c.IsConflict);

    /// <summary>是否存在可提交的变更（Zero Change Gate 核心）。</summary>
    public bool HasCommittableChanges => CommittableChangeCount > 0;

    // ---------- 发布状态 Banner ----------
    private bool _publishBannerVisible;
    public bool PublishBannerVisible
    {
        get => _publishBannerVisible;
        private set => SetProperty(ref _publishBannerVisible, value);
    }

    private string _publishBannerTitle = string.Empty;
    public string PublishBannerTitle
    {
        get => _publishBannerTitle;
        private set => SetProperty(ref _publishBannerTitle, value);
    }

    private string _publishBannerDetail = string.Empty;
    public string PublishBannerDetail
    {
        get => _publishBannerDetail;
        private set => SetProperty(ref _publishBannerDetail, value);
    }

    private Brush _publishBannerBrush = UiPalette.Info;
    public Brush PublishBannerBrush
    {
        get => _publishBannerBrush;
        private set => SetProperty(ref _publishBannerBrush, value);
    }

    private string _publishBannerGlyph = "\uE946";
    public string PublishBannerGlyph
    {
        get => _publishBannerGlyph;
        private set => SetProperty(ref _publishBannerGlyph, value);
    }

    // ---------- 仓库摘要 chips ----------
    private string _repoNameText = "-";
    public string RepoNameText
    {
        get => _repoNameText;
        private set => SetProperty(ref _repoNameText, value);
    }

    private string _branchText = "-";
    public string BranchText
    {
        get => _branchText;
        private set => SetProperty(ref _branchText, value);
    }

    private string _remoteSummaryText = "未配置";
    public string RemoteSummaryText
    {
        get => _remoteSummaryText;
        private set => SetProperty(ref _remoteSummaryText, value);
    }

    private string _identitySummaryText = "-";
    public string IdentitySummaryText
    {
        get => _identitySummaryText;
        private set => SetProperty(ref _identitySummaryText, value);
    }

    private string _worktreeSummaryText = "0 个变更";
    public string WorktreeSummaryText
    {
        get => _worktreeSummaryText;
        private set => SetProperty(ref _worktreeSummaryText, value);
    }

    private bool _isGitRepo;
    public bool IsGitRepo
    {
        get => _isGitRepo;
        private set => SetProperty(ref _isGitRepo, value);
    }

    private bool _imageConfirmationRequired;
    public bool ImageConfirmationRequired
    {
        get => _imageConfirmationRequired;
        private set => SetProperty(ref _imageConfirmationRequired, value);
    }

    private bool _imageConfirmed;
    public bool ImageConfirmed
    {
        get => _imageConfirmed;
        set
        {
            if (SetProperty(ref _imageConfirmed, value))
            {
                RecomputeReport();
            }
        }
    }

    /// <summary>最近一次预检上下文（供确认页/报告使用）。</summary>
    public PreflightContext? LastContext { get; private set; }

    // ---------- 初始化 ----------
    private async Task LoadEnvironmentAsync()
    {
        try
        {
            var gitVersion = await _git.GetVersionAsync();
            GitVersionText = string.IsNullOrEmpty(gitVersion) ? "Git: 未找到" : $"Git: {gitVersion}";
        }
        catch
        {
            GitVersionText = "Git: 未找到";
        }

        try
        {
            var dotnet = await _runner.RunAsync(new Services.ProcessRequest
            {
                FileName = "dotnet",
                Arguments = new[] { "--version" },
                Timeout = TimeSpan.FromSeconds(15)
            }, CancellationToken.None);
            DotNetVersionText = dotnet.Success && dotnet.Stdout.Count > 0
                ? $".NET: {dotnet.Stdout[0].Trim()}"
                : ".NET: 未找到";
        }
        catch
        {
            DotNetVersionText = ".NET: 未找到";
        }
    }

    // ---------- 日志 ----------
    private void Log(LogLevel level, string message)
    {
        var app = Application.Current;
        if (app == null) return;

        void Append()
        {
            Logs.Add(new LogEntry(level, message));
            while (Logs.Count > 500) Logs.RemoveAt(0);
        }

        if (app.Dispatcher.CheckAccess()) Append();
        else app.Dispatcher.Invoke(Append);
    }

    private void HandleError(Exception ex)
    {
        Log(LogLevel.Error, $"发生错误：{ex.Message}");
        ShowMessageRequested?.Invoke($"操作失败：{ex.Message}", true);
    }

    /// <summary>清空日志区。</summary>
    public void ClearLogs() => Logs.Clear();

    /// <summary>将当前日志拼为纯文本（用于复制；不含任何 Secret 原文）。</summary>
    public string BuildLogText() =>
        string.Join(Environment.NewLine, Logs.Select(l => $"{l.DisplayTime}  {l.LevelShort}  {l.Message}"));

    // ---------- 项目选择与检查 ----------
    private async Task BrowseProjectAsync()
    {
        if (BrowseFolderRequested == null) return;
        var path = await BrowseFolderRequested();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await SelectAndCheckAsync(path.Trim());
        }
    }

    public async Task SelectAndCheckAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Log(LogLevel.Error, $"目录不存在：{path}");
            ShowMessageRequested?.Invoke($"目录不存在：\n{path}", true);
            return;
        }

        ProjectPath = path;
        _settings.AddRecentProject(path);
        _settingsService.Save(_settings);

        if (!RecentProjects.Contains(path))
        {
            RecentProjects.Insert(0, path);
            while (RecentProjects.Count > 10) RecentProjects.RemoveAt(RecentProjects.Count - 1);
        }

        await RunChecksAsync();
    }

    /// <summary>执行完整发布前检查。</summary>
    public async Task RunChecksAsync()
    {
        if (IsBusy) return;

        var path = ProjectPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Log(LogLevel.Error, "请先选择有效的项目目录。");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsBusy = true;
        BusyText = "正在检查…";
        Log(LogLevel.Info, $"开始检查：{path}");

        try
        {
            var ctx = await _preflight.RunAsync(path, _settings, Log, _imageConfirmed, ct);
            LastContext = ctx;
            ApplyContextToUi(ctx);
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Info, "检查已取消。");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"检查失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void ApplyContextToUi(PreflightContext ctx)
    {
        Checks.Clear();
        foreach (var c in ctx.Report.Checks) Checks.Add(c);
        OnPropertyChanged(nameof(ChecksCount));

        Changes.Clear();
        foreach (var c in ctx.Changes) Changes.Add(c);
        OnPropertyChanged(nameof(CommittableChangeCount));
        OnPropertyChanged(nameof(HasCommittableChanges));

        var remote = ctx.Remote;
        var remoteText = remote?.HasRemote == true
            ? (remote!.IsMalformed ? $"{remote.Name}（地址异常）" : $"{remote.Name} → {remote.DisplayName}")
            : "未配置";

        var repoName = string.IsNullOrEmpty(ctx.RepositoryRoot)
            ? "-"
            : new DirectoryInfo(ctx.RepositoryRoot).Name;
        RepoNameText = repoName;
        BranchText = string.IsNullOrEmpty(ctx.Branch) ? "-" : ctx.Branch;
        RemoteSummaryText = remoteText;
        IdentitySummaryText = ctx.Identity == null ? "-" : (ctx.Identity.NameDisplay ?? "-");
        WorktreeSummaryText = $"{ctx.Changes.Count(c => !c.IsConflict)} 个变更";
        IsGitRepo = ctx.RepositoryRoot != null;

        StatusBarText = $"Git: {ctx.GitVersion}    .NET: 10.x    Repository: {(string.IsNullOrEmpty(ctx.RepositoryRoot) ? "未初始化" : "Ready")}    Branch: {(string.IsNullOrEmpty(ctx.Branch) ? "-" : ctx.Branch)}    Remote: {(remote?.HasRemote == true ? remote!.Name : "未配置")}";

        ImageConfirmationRequired = ctx.NewImages.Count > 0;
        RecomputeReport();
    }

    /// <summary>根据当前 Checks / 变更 / 提交说明 / 忙碌状态重算 CanCommit/CanPush 与 Banner（不重新扫描）。</summary>
    private void RecomputeReport()
    {
        var report = LastContext?.Report;
        var newImages = LastContext?.NewImages.Count ?? 0;

        // 图片确认状态动态更新（写回 report 供确认页/报告展示）
        if (report != null && newImages > 0)
        {
            var imgCheck = report.Checks.FirstOrDefault(c => c.Id == "image_privacy");
            if (imgCheck != null)
            {
                var confirmedOk = !_settings.RequireImagePrivacyConfirmation || _imageConfirmed;
                imgCheck.Status = CheckStatus.Warning;
                imgCheck.Summary = $"{newImages} 张新增/修改图片{(_imageConfirmed ? "（已确认脱敏）" : "（待确认脱敏）")}";
                imgCheck.BlocksPush = !confirmedOk;
            }
        }

        var gate = PublishGateEvaluator.Evaluate(
            report,
            CommittableChangeCount,
            CommitMessage,
            IsBusy,
            newImages,
            _imageConfirmed,
            _settings.RequireImagePrivacyConfirmation);

        CanCommit = gate.CanCommit;
        CanPush = gate.CanPush;
        CommitTooltip = gate.CommitReason;
        PushTooltip = gate.PushReason;

        UpdatePublishBanner(report);

        // 命令级 CanExecute 同步（防快捷键 / 直接调用）
        CommitOnlyCommand.NotifyCanExecuteChanged();
        SafeCommitAndPushCommand.NotifyCanExecuteChanged();
    }

    private void UpdatePublishBanner(PreflightReport? report)
    {
        if (report == null)
        {
            PublishBannerVisible = false;
            return;
        }

        PublishBannerVisible = true;
        switch (PublishBannerEvaluator.Evaluate(report, CommittableChangeCount))
        {
            case PublishBannerKind.Blocked:
                PublishBannerTitle = "PUBLISH BLOCKED";
                // SGP-UI-002：Detail 语义与安全语义分离——真 Blocked 才显示"N 项阻断问题"；
                // Warning+BlocksPush（如未配置 origin）显示"N 项需处理问题，当前无法发布"，绝不显示"0 项阻断"。
                PublishBannerDetail = PublishBannerEvaluator.BlockedDetail(report);
                PublishBannerBrush = UiPalette.Blocked;
                PublishBannerGlyph = "\uEA39";
                break;
            case PublishBannerKind.ReviewRequired:
                PublishBannerTitle = "REVIEW REQUIRED";
                PublishBannerDetail = PublishBannerEvaluator.ReviewRequiredDetail(report);
                PublishBannerBrush = UiPalette.Warning;
                PublishBannerGlyph = "\uE7BA";
                break;
            case PublishBannerKind.Ready:
                PublishBannerTitle = "READY TO PUBLISH";
                PublishBannerDetail = "可以安全提交";
                PublishBannerBrush = UiPalette.Pass;
                PublishBannerGlyph = "\uE73E";
                break;
            case PublishBannerKind.UpToDate:
            default:
                PublishBannerTitle = "UP TO DATE";
                PublishBannerDetail = "当前没有可提交的变更";
                PublishBannerBrush = UiPalette.Info;
                PublishBannerGlyph = "\uE73E";
                break;
        }
    }

    // ---------- 修复操作 ----------
    private async Task HandleFixAsync(string? checkId)
    {
        switch (checkId)
        {
            case "repo_detected":
                await InitRepositoryAsync();
                break;
            case "gitignore":
                await GenerateGitignoreAsync();
                break;
            case "git_identity":
                await FixIdentityAsync();
                break;
            case "remote":
                await SetOriginAsync();
                break;
        }
    }

    /// <summary>初始化 Git 仓库（git init -b main，不做 add/commit）。</summary>
    public async Task InitRepositoryAsync()
    {
        var path = ProjectPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        IsBusy = true;
        BusyText = "初始化 Git 仓库…";
        Log(LogLevel.Info, "将执行：git init -b main");
        try
        {
            var result = await _git.InitAsync(path, _cts?.Token ?? CancellationToken.None);
            if (result.Success)
            {
                Log(LogLevel.Pass, "Git 仓库初始化完成。");
                _settings.AddRecentProject(path);
                _settingsService.Save(_settings);
            }
            else
            {
                Log(LogLevel.Error, $"git init 失败：{result.StdErrText}");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"初始化失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            await RunChecksAsync();
        }
    }

    /// <summary>生成/补充推荐 .gitignore（先预览，只追加缺失规则）。</summary>
    public async Task GenerateGitignoreAsync()
    {
        var root = LastContext?.RepositoryRoot;
        if (root == null)
        {
            Log(LogLevel.Error, "当前不是 Git 仓库，无法生成 .gitignore。");
            return;
        }

        var existing = File.Exists(Path.Combine(root, ".gitignore"))
            ? await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"))
            : string.Empty;

        var missing = GitIgnoreService.ComputeMissingRules(existing, GitIgnoreService.RequiredRules);
        if (missing.Count == 0)
        {
            Log(LogLevel.Pass, ".gitignore 已覆盖全部推荐规则。");
            return;
        }

        var content = GitIgnoreService.BuildMergedContent(existing, missing);

        if (GitignorePreviewRequested == null)
        {
            Log(LogLevel.Info, "预览对话框不可用，直接应用。");
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), content);
            Log(LogLevel.Pass, $".gitignore 已补充 {missing.Count} 条推荐规则。");
        }
        else
        {
            var data = new GitignorePreviewData { RepoRoot = root, NewContent = content };
            var ok = await GitignorePreviewRequested(data);
            if (ok)
            {
                await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), content);
                Log(LogLevel.Pass, $".gitignore 已补充 {missing.Count} 条推荐规则。");
            }
            else
            {
                Log(LogLevel.Info, "已取消生成 .gitignore。");
            }
        }

        await RunChecksAsync();
    }

    /// <summary>将推荐身份写入 repository local config（不碰 global）。</summary>
    public async Task FixIdentityAsync()
    {
        var root = LastContext?.RepositoryRoot;
        if (root == null) return;

        IsBusy = true;
        BusyText = "修正 Git 身份…";
        Log(LogLevel.Info, "将执行（仅 repository local config）：");
        Log(LogLevel.Info, $"git config --local user.name \"{_settings.RecommendedGitName}\"");
        Log(LogLevel.Info, $"git config --local user.email \"{_settings.RecommendedGitEmail}\"");
        try
        {
            var (ok, error) = await new GitIdentityService(_git)
                .ApplyRecommendedAsync(root, _settings.RecommendedGitName, _settings.RecommendedGitEmail, CancellationToken.None);
            Log(ok ? LogLevel.Pass : LogLevel.Error, ok ? "身份已修正。" : error);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"修正身份失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            await RunChecksAsync();
        }
    }

    /// <summary>设置/修正 origin remote（弹窗输入 URL，改已有 origin 需确认）。</summary>
    public async Task SetOriginAsync()
    {
        var root = LastContext?.RepositoryRoot;
        if (root == null) return;
        if (SetOriginRequested == null) return;

        var current = LastContext?.Remote;
        var suggested = GitRemoteService.BuildOriginUrl(_settings.RecommendedGitName, GuessRepoName(root));
        var data = new SetOriginData
        {
            RemoteName = current?.Name ?? "origin",
            CurrentUrl = current?.FetchUrl,
            SuggestedUrl = suggested
        };

        var result = await SetOriginRequested(data);
        if (result?.ResultUrl == null) return;

        IsBusy = true;
        BusyText = "设置 origin…";
        try
        {
            var url = result.ResultUrl.Trim();
            if (current?.HasRemote == true)
            {
                Log(LogLevel.Info, $"将执行：git remote set-url {result.RemoteName} {url}");
                var r = await _git.RemoteSetUrlAsync(root, result.RemoteName, url, CancellationToken.None);
                Log(r.Success ? LogLevel.Pass : LogLevel.Error,
                    r.Success ? $"origin 已更新为：{url}" : $"更新 origin 失败：{r.StdErrText}");
            }
            else
            {
                Log(LogLevel.Info, $"将执行：git remote add {result.RemoteName} {url}");
                var r = await _git.RemoteAddAsync(root, result.RemoteName, url, CancellationToken.None);
                Log(r.Success ? LogLevel.Pass : LogLevel.Error,
                    r.Success ? $"origin 已添加：{url}" : $"添加 origin 失败：{r.StdErrText}");
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"设置 origin 失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            await RunChecksAsync();
        }
    }

    private static string GuessRepoName(string root)
    {
        var name = new DirectoryInfo(root).Name;
        return name;
    }

    // ---------- 发布流程 ----------
    private async Task PublishAsync(bool commitOnly)
    {
        if (IsBusy) return;

        var msg = CommitMessage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(msg))
        {
            Log(LogLevel.Error, "Commit Message 不能为空。");
            ShowMessageRequested?.Invoke("请填写 Commit Message。", true);
            return;
        }

        // 首次发布：非仓库 → 向导
        if (LastContext == null || LastContext.RepositoryRoot == null)
        {
            await RunFirstPublishWizardAsync(msg, commitOnly);
            return;
        }

        var root = LastContext.RepositoryRoot;

        // 复查是否仍是仓库（防止检查后发生改变）
        var topLevel = await _git.GetTopLevelAsync(ProjectPath);
        if (topLevel == null || !string.Equals(topLevel.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            Log(LogLevel.Blocked, "仓库状态已变化，请重新检查。");
            return;
        }

        if (commitOnly && !CanCommit)
        {
            Log(LogLevel.Blocked, "存在阻断项，禁止提交。");
            return;
        }

        if (!commitOnly && !CanPush)
        {
            Log(LogLevel.Blocked, "存在阻断项（或图片未确认脱敏），禁止安全提交并上传。");
            return;
        }

        // ---------- Zero Change 第二层防护：进入确认页前重新读取真实 git status ----------
        // 即使检查后工作区被外部撤销为 0 变更，也必须在此停止，绝不打开最终确认页。
        var liveStatus = await _git.StatusPorcelainAsync(root, _cts?.Token ?? CancellationToken.None);
        if (liveStatus.Canceled)
        {
            Log(LogLevel.Info, "发布已取消。");
            return;
        }

        var liveChanges = GitRepositoryInspector.ParseStatusPorcelain(liveStatus.Stdout);
        var liveCommittable = liveChanges.Where(c => !c.IsConflict).ToList();
        if (liveCommittable.Count == 0)
        {
            Log(LogLevel.Info, "当前工作区没有可提交的变更。");
            ShowMessageRequested?.Invoke("当前没有需要提交的文件。", false);
            return;
        }

        // 工作区与最近一次检查不一致 → 要求重新检查，保持 UI 状态与最新 Report 一致
        var lastPaths = LastContext!.Changes
            .Where(c => !c.IsConflict)
            .Select(c => c.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var livePaths = liveCommittable.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!lastPaths.SetEquals(livePaths))
        {
            Log(LogLevel.Info, "工作区状态已变化，请重新检查后再发布。");
            ShowMessageRequested?.Invoke("工作区状态已变化，请点击“重新检查”后再发布。", true);
            return;
        }

        var confirmData = BuildConfirmData(commitOnly, msg);
        if (ConfirmPublishRequested == null || !await ConfirmPublishRequested(confirmData))
        {
            Log(LogLevel.Info, "用户取消发布。");
            return;
        }

        IsBusy = true;
        BusyText = commitOnly ? "正在提交…" : "正在安全提交并上传…";
        try
        {
            var result = await _publish.ExecuteAsync(new PublishWorkflowService.PublishRequest
            {
                RepositoryRoot = root,
                CommitMessage = msg,
                Mode = commitOnly
                    ? PublishWorkflowService.PublishMode.CommitOnly
                    : PublishWorkflowService.PublishMode.CommitAndPush,
                ImageConfirmed = _imageConfirmed,
                RequireImageConfirmation = _settings.RequireImagePrivacyConfirmation
            }, Log, _cts?.Token ?? CancellationToken.None);

            if (result.Informational)
            {
                // 非异常提示（如 0 变更）：INFO 日志 + 轻提示，不显示 ERROR 红叉
                Log(LogLevel.Info, result.Error ?? "当前工作区没有可提交的变更。");
                ShowMessageRequested?.Invoke(result.Error ?? "当前没有需要提交的文件。", false);
                return;
            }

            if (result.Committed)
            {
                Log(LogLevel.Pass, $"提交成功：{result.CommitShortHash}");
            }
            if (result.Pushed)
            {
                Log(LogLevel.Pass, "推送成功。");
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                Log(LogLevel.Error, result.Error);
            }
            if (result.UnstagedAfterBlocked)
            {
                Log(LogLevel.Blocked, "已暂存内容被安全闸门拦截，已执行 git reset 取消暂存。");
            }

            ShowMessageRequested?.Invoke(
                result.Pushed ? "发布完成：已提交并推送。" :
                result.Committed && string.IsNullOrEmpty(result.Error) ? "提交完成（未推送）。" :
                result.Error ?? "发布失败。",
                !(result.Pushed || (result.Committed && string.IsNullOrEmpty(result.Error))));
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Info, "发布已取消。");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"发布失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            await RunChecksAsync();
        }
    }

    private ConfirmPublishData BuildConfirmData(bool commitOnly, string msg)
    {
        var ctx = LastContext!;
        var report = ctx.Report;
        var remote = ctx.Remote;
        var identity = ctx.Identity;
        var build = ctx.Build;

        string buildDisplay;
        if (build == null) buildDisplay = "-";
        else if (!build.BuildRun) buildDisplay = build.SkipReason;
        else if (build.Succeeded && build.WarningCount == 0) buildDisplay = $"PASS（{build.Duration.TotalSeconds:F1}s）";
        else if (build.Succeeded) buildDisplay = $"PASS（{build.WarningCount} warnings）";
        else buildDisplay = "FAIL";

        return new ConfirmPublishData
        {
            RepositoryRoot = ctx.RepositoryRoot ?? string.Empty,
            ProjectPath = ctx.ProjectPath,
            RepoDisplay = remote?.DisplayName ?? "-",
            Branch = string.IsNullOrEmpty(ctx.Branch) ? "-" : ctx.Branch,
            RemoteDisplay = remote?.HasRemote == true ? (remote.Name + (remote.IsMalformed ? "（地址异常）" : string.Empty)) : "（未配置）",
            AuthorDisplay = identity == null ? "-" : $"{identity.NameDisplay} <{identity.EmailDisplay}>",
            CommitMessage = msg,
            ChangeCount = ctx.Changes.Count,
            PassCount = report.PassCount,
            WarningCount = report.WarningCount,
            BlockedCount = report.BlockedCount,
            ImageConfirmed = !ImageConfirmationRequired || _imageConfirmed,
            HasNewImages = ctx.NewImages.Count > 0,
            BuildDisplay = buildDisplay,
            WillSetUpstream = !ctx.HasUpstream && !commitOnly,
            CommitOnly = commitOnly
        };
    }

    // ---------- 首次发布向导 ----------
    private async Task RunFirstPublishWizardAsync(string msg, bool commitOnly)
    {
        if (WizardRequested == null) return;

        var data = new WizardData
        {
            ProjectPath = ProjectPath,
            CommitMessage = msg,
            OriginUrl = GitRemoteService.BuildOriginUrl(_settings.RecommendedGitName, GuessRepoName(ProjectPath))
        };

        var result = await WizardRequested(data);
        if (result == null || !result.Confirmed)
        {
            Log(LogLevel.Info, "首次发布向导已取消。");
            return;
        }

        IsBusy = true;
        BusyText = "执行首次发布向导…";
        try
        {
            // 步骤 1：初始化
            if (result.InitGit)
            {
                Log(LogLevel.Info, "将执行：git init -b main");
                var r = await _git.InitAsync(ProjectPath, _cts?.Token ?? CancellationToken.None);
                if (!r.Success)
                {
                    Log(LogLevel.Error, $"git init 失败：{r.StdErrText}");
                    return;
                }
                Log(LogLevel.Pass, "Git 仓库初始化完成。");
            }

            // 步骤 2：.gitignore
            if (result.GenerateGitignore)
            {
                var gitignorePath = Path.Combine(ProjectPath, ".gitignore");
                var existing = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath) : string.Empty;
                var missing = GitIgnoreService.ComputeMissingRules(existing, GitIgnoreService.RequiredRules);
                if (missing.Count > 0)
                {
                    Log(LogLevel.Info, $"将写入 .gitignore（追加 {missing.Count} 条推荐规则）");
                    await File.WriteAllTextAsync(Path.Combine(ProjectPath, ".gitignore"),
                        GitIgnoreService.BuildMergedContent(existing, missing));
                    Log(LogLevel.Pass, ".gitignore 已生成/补充。");
                }
                else
                {
                    Log(LogLevel.Pass, ".gitignore 已存在且规则完整。");
                }
            }

            // 步骤 3：身份
            if (result.SetIdentity)
            {
                var (ok, err) = await new GitIdentityService(_git)
                    .ApplyRecommendedAsync(ProjectPath, _settings.RecommendedGitName, _settings.RecommendedGitEmail, _cts?.Token ?? CancellationToken.None);
                Log(ok ? LogLevel.Pass : LogLevel.Error, ok ? "Local Git 身份已设置。" : err);
            }

            // 步骤 4/5/6/7/8：进入标准流程（检查 → 确认 → 提交 → push）
            IsBusy = false;
            BusyText = string.Empty;

            await RunChecksAsync();

            if (!string.IsNullOrWhiteSpace(result.OriginUrl))
            {
                var remoteInfo = GitRepositoryInspector.ParseRemoteV((await _git.RemoteVAsync(ProjectPath)).Stdout);
                if (!remoteInfo.HasRemote)
                {
                    Log(LogLevel.Info, $"将执行：git remote add origin {result.OriginUrl}");
                    var r = await _git.RemoteAddAsync(ProjectPath, "origin", result.OriginUrl);
                    Log(r.Success ? LogLevel.Pass : LogLevel.Error,
                        r.Success ? "origin 已配置。" : $"配置 origin 失败：{r.StdErrText}");
                }
            }

            if (!string.IsNullOrWhiteSpace(result.OriginUrl) && commitOnly == false)
            {
                await RunChecksAsync();
            }

            CommitMessage = result.CommitMessage;
            await PublishAsync(commitOnly);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"向导执行失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ---------- 其它命令 ----------
    private void CancelCurrentOperation()
    {
        _cts?.Cancel();
        Log(LogLevel.Info, "正在取消当前操作…");
    }

    /// <summary>git pull --ff-only，失败不自动 merge。</summary>
    private async Task SyncRemoteAsync()
    {
        var root = LastContext?.RepositoryRoot;
        if (root == null)
        {
            Log(LogLevel.Error, "当前不是 Git 仓库。");
            return;
        }

        IsBusy = true;
        BusyText = "同步远端…";
        Log(LogLevel.Info, "将执行：git pull --ff-only");
        try
        {
            var result = await _git.PullFfOnlyAsync(root, _cts?.Token ?? CancellationToken.None);
            if (result.Success)
            {
                Log(LogLevel.Pass, "同步完成（Fast-forward）。");
            }
            else
            {
                var err = (result.StdErrText + "\n" + result.StdOutText).Trim();
                Log(LogLevel.Error, "远端与本地历史无法 Fast Forward，需要人工处理（本工具不会自动 merge/rebase/reset）。");
                if (!string.IsNullOrWhiteSpace(err)) Log(LogLevel.Info, err);
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"同步失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ---------- 设置 ----------
    private async Task ShowSettingsAsync()
    {
        if (SettingsRequested == null) return;
        var data = new SettingsData { Settings = _settings.Clone(), SettingsPath = _settingsService.SettingsPath };
        var saved = await SettingsRequested(data);
        if (saved)
        {
            _settings = data.Settings;
            _settingsService.Save(_settings);
            Log(LogLevel.Pass, "设置已保存。");
            _settings.AddRecentProject(ProjectPath);
            _settingsService.Save(_settings);
        }
    }

    // ---------- 前缀 ----------
    private void ApplyPrefix(string prefix)
    {
        var current = CommitMessage ?? string.Empty;
        var trimmed = current.TrimStart();
        // 替换旧前缀
        var prefixes = CommitPrefixes.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        foreach (var old in prefixes)
        {
            if (trimmed.StartsWith(old, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[old.Length..];
                break;
            }
        }
        CommitMessage = prefix + trimmed;
    }
}