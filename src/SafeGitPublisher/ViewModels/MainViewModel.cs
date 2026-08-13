using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
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
    private LargeFileScanner _largeScanner = null!;
    private readonly DotNetBuildService _buildService;
    private PreflightService _preflight = null!;
    private PublishWorkflowService _publish = null!;
    private readonly SettingsService _settingsService;

    private AppSettings _settings;
    private CancellationTokenSource? _cts;
    private long _preflightGeneration;
    private bool _operationLeaseActive;
    private string? _lastPreflightChangeFingerprint;
    private string? _currentImageFingerprint;
    private string? _confirmedImageFingerprint;
    private const string DefaultExistingCommitRecoverySummary =
        "如果提交已经创建但上传失败，可先安全检查已有提交，再单独上传；不会重复创建提交。";

    public MainViewModel()
    {
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();
        _git = new GitService(_runner);
        _sensitiveScanner = new SensitiveFileScanner(_git);
        _buildService = new DotNetBuildService(_runner);
        RebuildPolicyServices();

        foreach (var r in _settings.RecentProjects) RecentProjects.Add(r);

        CommitPrefixes.Add(string.Empty);
        CommitPrefixes.Add("feat: ");
        CommitPrefixes.Add("fix: ");
        CommitPrefixes.Add("docs: ");
        CommitPrefixes.Add("refactor: ");
        CommitPrefixes.Add("chore: ");
        CommitPrefixes.Add("test: ");
        SelectedCommitPrefix = string.Empty;

        BrowseProjectCommand = new AsyncRelayCommand(_ => BrowseProjectAsync(), _ => CanOperate, onException: ex => HandleError(ex));
        RunChecksCommand = new AsyncRelayCommand(_ => RunChecksAsync(), _ => CanOperate, onException: ex => HandleError(ex));
        CancelCommand = new RelayCommand(_ => CancelCurrentOperation(), _ => IsBusy);
        FixCheckCommand = new AsyncRelayCommand(p => HandleFixAsync(p as string), _ => CanOperate, onException: ex => HandleError(ex));
        // 命令级 Zero Change 防护：CanExecute 直接读取门控状态，
        // 即使按钮被绕过（快捷键 / 直接调用 Execute）也无法越权执行。
        CommitOnlyCommand = new AsyncRelayCommand(_ => PublishAsync(commitOnly: true), _ => CanOperate && CanCommit, onException: ex => HandleError(ex));
        SafeCommitAndPushCommand = new AsyncRelayCommand(_ => PublishAsync(commitOnly: false), _ => CanOperate && CanPush, onException: ex => HandleError(ex));
        PushExistingCommitCommand = new AsyncRelayCommand(_ => PushExistingCommitAsync(), _ => CanPushExistingCommit, onException: ex => HandleError(ex));
        FirstPublishCommand = new AsyncRelayCommand(_ => RunFirstPublishWizardAsync(CommitMessage.Trim(), commitOnly: false), _ => CanStartFirstPublish, onException: ex => HandleError(ex));
        SyncRemoteCommand = new AsyncRelayCommand(_ => SyncRemoteAsync(), _ => CanOperate && LastContext?.RepositoryRoot != null, onException: ex => HandleError(ex));
        ShowReportCommand = new RelayCommand(_ => ShowReportRequested?.Invoke(new ReportData { Context = LastContext ?? new PreflightContext { ProjectPath = string.Empty, Settings = _settings } }));
        ShowSettingsCommand = new AsyncRelayCommand(_ => ShowSettingsAsync(), _ => CanOperate, onException: ex => HandleError(ex));

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
    public AsyncRelayCommand PushExistingCommitCommand { get; }
    public AsyncRelayCommand FirstPublishCommand { get; }
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
                // 路径输入一旦变化，旧仓库报告和图片确认都不再属于当前目标。
                ExistingCommitRecoverySummary = DefaultExistingCommitRecoverySummary;
                InvalidatePreflight("项目目录已变化，请重新检查。", resetImageConfirmation: true);
                OnPropertyChanged(nameof(CanStartFirstPublish));
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
            if (IsBusy)
            {
                // 忙碌中拒绝切换，保留 backing field 并让控件恢复当前值。
                OnPropertyChanged(nameof(SelectedRecentProject));
                return;
            }

            if (SetProperty(ref _selectedRecentProject, value)
                && value != null
                && !string.Equals(value, ProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                _ = SelectAndCheckAsync(value);
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
                OnPropertyChanged(nameof(CanStartFirstPublish));
                OnPropertyChanged(nameof(CanPushExistingCommit));
                OnPropertyChanged(nameof(ExistingCommitRecoveryTooltip));
                // RelayCommand/AsyncRelayCommand 均监听 WPF 全局 RequerySuggested。
                CommandManager.InvalidateRequerySuggested();
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
    public bool CanOperate => !IsBusy && !_operationLeaseActive;

    /// <summary>
    /// 已完成检查的 Git 仓库始终提供“检查已有提交”入口，兼容应用升级前已经留在本地的提交。
    /// 显示入口不代表允许 Push；真正门禁由每次点击后生成的一次性安全计划决定。
    /// </summary>
    public bool HasExistingCommitRecovery => IsGitRepo;

    /// <summary>已有提交恢复命令只依赖仓库身份与全局操作租约，不复用普通 CanPush/工作区 Gate。</summary>
    public bool CanPushExistingCommit => CanOperate && HasExistingCommitRecovery;

    private string _existingCommitRecoverySummary = DefaultExistingCommitRecoverySummary;
    public string ExistingCommitRecoverySummary
    {
        get => _existingCommitRecoverySummary;
        private set => SetProperty(ref _existingCommitRecoverySummary, value);
    }

    public string ExistingCommitRecoveryTooltip => !HasExistingCommitRecovery
        ? "请先选择并检查 Git 仓库"
        : !CanOperate
            ? "请等待当前操作完成"
            : "重新检查 HEAD、分支、远端和待推送历史；只上传已有提交，不会再次 add 或 commit";

    /// <summary>非仓库目录可从主界面直接进入首次发布向导。</summary>
    public bool CanStartFirstPublish => CanOperate
        && !string.IsNullOrWhiteSpace(ProjectPath)
        && Directory.Exists(ProjectPath)
        && LastContext != null
        && LastContext.RepositoryRoot == null;

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
        private set
        {
            if (SetProperty(ref _isGitRepo, value))
            {
                OnPropertyChanged(nameof(HasExistingCommitRecovery));
                OnPropertyChanged(nameof(CanPushExistingCommit));
                OnPropertyChanged(nameof(ExistingCommitRecoveryTooltip));
                PushExistingCommitCommand.NotifyCanExecuteChanged();
            }
        }
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
            // 确认只能绑定到本次检查得到的“仓库 + 图片路径 + 图片内容”指纹。
            // 无有效指纹时拒绝把全局 bool 置真，避免跨项目复用确认。
            var accepted = value && !string.IsNullOrEmpty(_currentImageFingerprint);
            if (SetProperty(ref _imageConfirmed, accepted))
            {
                _confirmedImageFingerprint = accepted ? _currentImageFingerprint : null;
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
        // 异步命令兜底异常也必须撤销旧发布 Gate；不能只记日志后继续沿用旧报告。
        if (ex is not OperationCanceledException)
        {
            InvalidatePreflight("操作异常，旧检查结果已失效，请重新检查。", resetImageConfirmation: false);
        }
        Log(LogLevel.Error, $"发生错误：{ex.Message}");
        ShowMessageRequested?.Invoke($"操作失败：{ex.Message}", true);
    }

    /// <summary>清空日志区。</summary>
    public void ClearLogs() => Logs.Clear();

    /// <summary>将当前日志拼为纯文本（用于复制；不含任何 Secret 原文）。</summary>
    public string BuildLogText() =>
        string.Join(Environment.NewLine, Logs.Select(l => $"{l.DisplayTime}  {l.LevelShort}  {l.Message}"));

    /// <summary>按当前设置重建所有持有策略快照的服务，使新阈值立即生效。</summary>
    private void RebuildPolicyServices()
    {
        _largeScanner = new LargeFileScanner(
            _settings.LargeFileWarningMB,
            _settings.LargeFileHighWarningMB,
            _settings.LargeFileBlockingMB);
        _preflight = new PreflightService(_git, _sensitiveScanner, _secretScanner, _largeScanner, _buildService);
        _publish = new PublishWorkflowService(_git, _sensitiveScanner, _secretScanner, _largeScanner);
    }

    /// <summary>
    /// 使旧预检快照失效。失效后报告、变更及发布门禁立即清空，必须完成新检查才能发布。
    /// </summary>
    private void InvalidatePreflight(string reason, bool resetImageConfirmation)
    {
        var confirmedImageFingerprint = resetImageConfirmation ? null : _confirmedImageFingerprint;
        _preflightGeneration++;
        LastContext = null;
        _lastPreflightChangeFingerprint = null;
        _currentImageFingerprint = null;
        _confirmedImageFingerprint = confirmedImageFingerprint;
        if (_imageConfirmed)
        {
            _imageConfirmed = false;
            OnPropertyChanged(nameof(ImageConfirmed));
        }

        Checks.Clear();
        Changes.Clear();
        OnPropertyChanged(nameof(ChecksCount));
        OnPropertyChanged(nameof(CommittableChangeCount));
        OnPropertyChanged(nameof(HasCommittableChanges));

        RepoNameText = "-";
        BranchText = "-";
        RemoteSummaryText = "未配置";
        IdentitySummaryText = "-";
        WorktreeSummaryText = "0 个变更";
        IsGitRepo = false;
        ImageConfirmationRequired = false;
        StatusBarText = reason;
        OnPropertyChanged(nameof(CanStartFirstPublish));
        RecomputeReport();
    }

    /// <summary>启动独占的状态变更操作，并为“取消”按钮创建专用令牌。</summary>
    private CancellationToken StartOperation(string busyText)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        BusyText = busyText;
        return _cts.Token;
    }

    /// <summary>
    /// 跨多个 await 保持全局操作租约。内部预检可以短暂切换 IsBusy，
    /// 但其它状态变更命令在整个发布复核/确认期间始终不可重入。
    /// </summary>
    private void SetOperationLease(bool active)
    {
        _operationLeaseActive = active;
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanStartFirstPublish));
        OnPropertyChanged(nameof(CanPushExistingCommit));
        OnPropertyChanged(nameof(ExistingCommitRecoveryTooltip));
        PushExistingCommitCommand.NotifyCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------- 项目选择与检查 ----------
    private async Task BrowseProjectAsync()
    {
        if (IsBusy) return;
        if (BrowseFolderRequested == null) return;
        var path = await BrowseFolderRequested();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await SelectAndCheckAsync(path.Trim());
        }
    }

    public async Task SelectAndCheckAsync(string path)
    {
        if (IsBusy) return;
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
    public async Task<bool> RunChecksAsync()
        => await RunChecksAsync(allowOperationLease: false);

    /// <summary>内部发布复核可在持有全局操作租约时执行；普通命令不可。</summary>
    private async Task<bool> RunChecksAsync(bool allowOperationLease)
    {
        if (IsBusy || (_operationLeaseActive && !allowOperationLease)) return false;

        if (!allowOperationLease)
        {
            // 普通重查、设置保存、Remote 修改或同步后不沿用上一轮恢复提示。
            // 可执行性始终由下一次 PrepareExistingPushAsync 的新计划决定。
            ExistingCommitRecoverySummary = DefaultExistingCommitRecoverySummary;
        }

        var path = ProjectPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            InvalidatePreflight("请选择有效的项目目录后重新检查。", resetImageConfirmation: true);
            Log(LogLevel.Error, "请先选择有效的项目目录。");
            return false;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 检查启动即撤销旧 Gate；取消、异常或命令失败都不会重新开放旧报告。
        InvalidatePreflight("正在重新检查，旧检查结果已失效。", resetImageConfirmation: false);
        var generation = _preflightGeneration;

        IsBusy = true;
        BusyText = "正在检查…";
        Log(LogLevel.Info, $"开始检查：{path}");

        try
        {
            // 图片确认由本 ViewModel 的内容指纹恢复，预检不得直接复用旧全局 bool。
            var ctx = await _preflight.RunAsync(path, _settings, Log, imageConfirmed: false, ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _preflightGeneration
                || !string.Equals(path, (ProjectPath ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Log(LogLevel.Info, "检查期间项目目录已变化，本次结果已丢弃。");
                return false;
            }

            string? changeFingerprint = null;
            string? imageFingerprint = null;
            if (ctx.RepositoryRoot != null)
            {
                changeFingerprint = await ComputeChangeFingerprintAsync(ctx.RepositoryRoot, ctx.Changes, ct);
                if (ctx.NewImages.Count > 0)
                {
                    imageFingerprint = await ComputeChangeFingerprintAsync(ctx.RepositoryRoot, ctx.NewImages, ct);
                }
            }

            ct.ThrowIfCancellationRequested();
            LastContext = ctx;
            _lastPreflightChangeFingerprint = changeFingerprint;
            ApplyContextToUi(ctx, imageFingerprint);
            return true;
        }
        catch (OperationCanceledException)
        {
            InvalidatePreflight("检查已取消，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Info, "检查已取消。");
            return false;
        }
        catch (Exception ex)
        {
            InvalidatePreflight("检查失败，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Error, $"检查失败：{ex.Message}");
            return false;
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private void ApplyContextToUi(PreflightContext ctx, string? imageFingerprint)
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
        _currentImageFingerprint = imageFingerprint;
        var confirmationStillMatches = imageFingerprint != null
            && string.Equals(imageFingerprint, _confirmedImageFingerprint, StringComparison.Ordinal);
        if (_imageConfirmed != confirmationStillMatches)
        {
            _imageConfirmed = confirmationStillMatches;
            OnPropertyChanged(nameof(ImageConfirmed));
        }
        if (imageFingerprint == null) _confirmedImageFingerprint = null;

        OnPropertyChanged(nameof(CanStartFirstPublish));
        OnPropertyChanged(nameof(ImageConfirmationRequired));
        RecomputeReport();
    }

    /// <summary>
    /// 生成“仓库 + 状态码 + 路径 + 文件内容”的 SHA-256 指纹。
    /// 检查前后若文件在读取中变化则失败关闭，避免同路径内容变化复用旧 Build/扫描结论。
    /// </summary>
    private static async Task<string> ComputeChangeFingerprintAsync(
        string repositoryRoot,
        IEnumerable<GitFileChange> changes,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintText(hash, Path.GetFullPath(repositoryRoot).TrimEnd('\\', '/'));

        foreach (var change in changes
                     .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.StatusCode, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            AppendFingerprintText(hash, change.StatusCode);
            AppendFingerprintText(hash, change.Path);
            AppendFingerprintText(hash, change.OldPath ?? string.Empty);

            if (change.IsDeletedLike())
            {
                AppendFingerprintText(hash, "<deleted>");
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot,
                change.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(fullPath))
            {
                AppendFingerprintText(hash, "<missing>");
                continue;
            }

            var before = new FileInfo(fullPath);
            var beforeLength = before.Length;
            var beforeWrite = before.LastWriteTimeUtc;
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            var after = new FileInfo(fullPath);
            if (!after.Exists || after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite)
            {
                throw new IOException($"计算文件指纹时内容发生变化：{change.Path}");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendFingerprintText(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { 0 });
    }

    /// <summary>根据当前 Checks / 变更 / 提交说明 / 忙碌状态重算 CanCommit/CanPush 与 Banner（不重新扫描）。</summary>
    private void RecomputeReport()
    {
        var report = LastContext?.Report;
        var newImages = LastContext?.NewImages.Count ?? 0;
        var imageConfirmationCurrent = _imageConfirmed
            && _currentImageFingerprint != null
            && string.Equals(_currentImageFingerprint, _confirmedImageFingerprint, StringComparison.Ordinal);

        // 图片确认状态动态更新（写回 report 供确认页/报告展示）
        if (report != null && newImages > 0)
        {
            var imgCheck = report.Checks.FirstOrDefault(c => c.Id == "image_privacy");
            if (imgCheck != null)
            {
                var confirmedOk = !_settings.RequireImagePrivacyConfirmation || imageConfirmationCurrent;
                imgCheck.Status = CheckStatus.Warning;
                imgCheck.Summary = $"{newImages} 张新增/修改图片{(imageConfirmationCurrent ? "（已确认脱敏）" : "（待确认脱敏）")}";
                imgCheck.BlocksPush = !confirmedOk;
            }
        }

        var gate = PublishGateEvaluator.Evaluate(
            report,
            CommittableChangeCount,
            CommitMessage,
            IsBusy,
            newImages,
            imageConfirmationCurrent,
            _settings.RequireImagePrivacyConfirmation);

        CanCommit = gate.CanCommit;
        CanPush = gate.CanPush;
        CommitTooltip = gate.CommitReason;
        PushTooltip = gate.PushReason;

        UpdatePublishBanner(report);

        // 命令级 CanExecute 同步（防快捷键 / 直接调用）
        CommitOnlyCommand.NotifyCanExecuteChanged();
        SafeCommitAndPushCommand.NotifyCanExecuteChanged();
        PushExistingCommitCommand.NotifyCanExecuteChanged();
        FirstPublishCommand.NotifyCanExecuteChanged();
        SyncRemoteCommand.NotifyCanExecuteChanged();
        CommandManager.InvalidateRequerySuggested();
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
        if (IsBusy) return;
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
        if (IsBusy) return;
        var path = ProjectPath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        var ct = StartOperation("初始化 Git 仓库…");
        InvalidatePreflight("仓库正在初始化，完成后将重新检查。", resetImageConfirmation: true);
        Log(LogLevel.Info, "将执行：git init -b main");
        try
        {
            var result = await _git.InitAsync(path, ct);
            if (result.Success)
            {
                Log(LogLevel.Pass, "Git 仓库初始化完成。");
                _settings.AddRecentProject(path);
                _settingsService.Save(_settings);
            }
            else
            {
                Log(LogLevel.Error, $"git init 失败：{GitRemoteService.RedactOutput(result.StdErrText)}");
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
        if (IsBusy) return;
        var root = LastContext?.RepositoryRoot;
        if (root == null)
        {
            Log(LogLevel.Error, "当前不是 Git 仓库，无法生成 .gitignore。");
            return;
        }

        var ct = StartOperation("正在更新 .gitignore…");
        try
        {
            var existing = File.Exists(Path.Combine(root, ".gitignore"))
                ? await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"), ct)
                : string.Empty;

            var missing = GitIgnoreService.ComputeMissingRules(existing, GitIgnoreService.RequiredRules);
            if (missing.Count == 0)
            {
                Log(LogLevel.Pass, ".gitignore 已覆盖全部推荐规则。");
                return;
            }

            var content = GitIgnoreService.BuildMergedContent(existing, missing);
            var shouldWrite = true;
            if (GitignorePreviewRequested != null)
            {
                var data = new GitignorePreviewData { RepoRoot = root, NewContent = content };
                shouldWrite = await GitignorePreviewRequested(data);
            }

            if (shouldWrite)
            {
                await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), content, ct);
                InvalidatePreflight(".gitignore 已变化，请重新检查。", resetImageConfirmation: false);
                Log(LogLevel.Pass, $".gitignore 已补充 {missing.Count} 条推荐规则。");
            }
            else
            {
                Log(LogLevel.Info, "已取消生成 .gitignore。");
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }

        await RunChecksAsync();
    }

    /// <summary>将推荐身份写入 repository local config（不碰 global）。</summary>
    public async Task FixIdentityAsync()
    {
        if (IsBusy) return;
        var root = LastContext?.RepositoryRoot;
        if (root == null) return;

        var ct = StartOperation("修正 Git 身份…");
        InvalidatePreflight("Git 身份正在修改，完成后将重新检查。", resetImageConfirmation: false);
        Log(LogLevel.Info, "将执行（仅 repository local config）：");
        Log(LogLevel.Info, $"git config --local user.name \"{_settings.RecommendedGitName}\"");
        Log(LogLevel.Info, $"git config --local user.email \"{_settings.RecommendedGitEmail}\"");
        try
        {
            var (ok, error) = await new GitIdentityService(_git)
                .ApplyRecommendedAsync(root, _settings.RecommendedGitName, _settings.RecommendedGitEmail, ct);
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
        if (IsBusy) return;
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

        var ct = StartOperation("设置 origin…");
        InvalidatePreflight("Remote 正在修改，完成后将重新检查。", resetImageConfirmation: false);
        try
        {
            var url = result.ResultUrl.Trim();
            var (_, _, malformed, reason, _) = GitRemoteService.ParseUrl(url);
            if (malformed)
            {
                Log(LogLevel.Blocked, $"Remote URL 未通过安全校验：{reason}");
                ShowMessageRequested?.Invoke($"Remote URL 未通过安全校验：{reason}", true);
                return;
            }
            var displayUrl = GitRemoteService.RedactForDisplay(url);
            if (current?.HasRemote == true)
            {
                Log(LogLevel.Info, $"将执行：git remote set-url {result.RemoteName} {displayUrl}");
                var r = await _git.RemoteSetUrlAsync(root, result.RemoteName, url, ct);
                Log(r.Success ? LogLevel.Pass : LogLevel.Error,
                    r.Success ? $"origin 已更新为：{displayUrl}" : $"更新 origin 失败：{GitRemoteService.RedactOutput(r.StdErrText)}");
            }
            else
            {
                Log(LogLevel.Info, $"将执行：git remote add {result.RemoteName} {displayUrl}");
                var r = await _git.RemoteAddAsync(root, result.RemoteName, url, ct);
                Log(r.Success ? LogLevel.Pass : LogLevel.Error,
                    r.Success ? $"origin 已添加：{displayUrl}" : $"添加 origin 失败：{GitRemoteService.RedactOutput(r.StdErrText)}");
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

        // 每次进入最终确认前都重跑完整预检（包括按设置要求的 Build）。
        // 旧报告即使路径/文件名相同也不能复用；图片确认只会在内容指纹一致时自动恢复。
        SetOperationLease(true);
        try
        {
            await PublishCheckedAsync(commitOnly, msg);
        }
        finally
        {
            SetOperationLease(false);
        }
    }

    /// <summary>发布命令持有全局租约后的完整复核、确认与执行流程。</summary>
    private async Task PublishCheckedAsync(bool commitOnly, string msg, bool refreshPreflight = true)
    {
        if (refreshPreflight
            && (!await RunChecksAsync(allowOperationLease: true) || LastContext?.RepositoryRoot == null))
        {
            ShowMessageRequested?.Invoke("发布前重新检查未完成，已保持发布禁用。", true);
            return;
        }
        if (LastContext?.RepositoryRoot == null)
        {
            ShowMessageRequested?.Invoke("尚未完成可发布的仓库检查。", true);
            return;
        }

        var root = LastContext.RepositoryRoot;

        // 复查是否仍是仓库（防止检查后发生改变）
        var topLevel = await _git.GetTopLevelAsync(ProjectPath);
        if (topLevel == null || !string.Equals(topLevel.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            InvalidatePreflight("仓库状态已变化，请重新检查。", resetImageConfirmation: true);
            Log(LogLevel.Blocked, "仓库状态已变化，请重新检查。");
            return;
        }

        if (commitOnly && !CanCommit)
        {
            Log(LogLevel.Blocked, "存在阻断项，禁止提交。");
            return;
        }

        var imageCanBeConfirmedInDialog = CanResolveImageGateInConfirm(commitOnly);
        if (!commitOnly && !CanPush && !imageCanBeConfirmedInDialog)
        {
            Log(LogLevel.Blocked, "存在阻断项（或图片未确认脱敏），禁止安全提交并上传。");
            return;
        }

        // ---------- Zero Change 第二层防护：进入确认页前重新读取真实 git status ----------
        // 即使检查后工作区被外部撤销为 0 变更，也必须在此停止，绝不打开最终确认页。
        var liveStatus = await _git.StatusPorcelainAsync(root, _cts?.Token ?? CancellationToken.None);
        if (liveStatus.Canceled)
        {
            InvalidatePreflight("发布复核已取消，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Info, "发布已取消。");
            return;
        }
        if (!liveStatus.Success)
        {
            InvalidatePreflight("读取最新工作区失败，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Blocked, $"无法读取最新工作区状态：{GitRemoteService.RedactOutput(liveStatus.StdErrText)}");
            return;
        }

        var liveChanges = GitRepositoryInspector.ParseStatusPorcelain(liveStatus.Stdout);
        var liveCommittable = liveChanges.Where(c => !c.IsConflict).ToList();
        if (liveCommittable.Count == 0)
        {
            InvalidatePreflight("当前已无可提交变更，请重新检查。", resetImageConfirmation: true);
            Log(LogLevel.Info, "当前工作区没有可提交的变更。");
            ShowMessageRequested?.Invoke("当前没有需要提交的文件。", false);
            return;
        }

        // 状态码/路径/重命名来源及当前文件内容均必须与刚完成的预检一致。
        var liveFingerprint = await ComputeChangeFingerprintAsync(root, liveChanges, CancellationToken.None);
        if (!string.Equals(_lastPreflightChangeFingerprint, liveFingerprint, StringComparison.Ordinal))
        {
            InvalidatePreflight("工作区内容已变化，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Info, "工作区状态已变化，请重新检查后再发布。");
            ShowMessageRequested?.Invoke("工作区状态已变化，请点击“重新检查”后再发布。", true);
            return;
        }

        var confirmData = BuildConfirmData(commitOnly, msg);
        if (ConfirmPublishRequested == null || !await ConfirmPublishRequested(confirmData))
        {
            InvalidatePreflight("发布已取消，请重新检查后再发布。", resetImageConfirmation: false);
            Log(LogLevel.Info, "用户取消发布。");
            return;
        }

        if (confirmData.RequiresImageConfirmation)
        {
            // Setter 会把确认严格绑定到当前“仓库 + 图片路径 + 内容”指纹。
            ImageConfirmed = confirmData.ImageConfirmed;
            if (!CanPush)
            {
                InvalidatePreflight("图片脱敏确认未能绑定当前内容，请重新检查。", resetImageConfirmation: true);
                ShowMessageRequested?.Invoke("图片状态已变化，本次发布已取消。", true);
                return;
            }
        }

        // 确认页打开期间外部编辑也会使确认失效；不自动循环弹窗。
        var afterConfirmStatus = await _git.StatusPorcelainAsync(root, CancellationToken.None);
        if (!afterConfirmStatus.Success)
        {
            InvalidatePreflight("最终确认后无法复核工作区，请重新检查。", resetImageConfirmation: false);
            Log(LogLevel.Blocked, "最终确认后无法复核工作区状态，已取消发布。");
            return;
        }

        var afterConfirmChanges = GitRepositoryInspector.ParseStatusPorcelain(afterConfirmStatus.Stdout);
        var afterConfirmFingerprint = await ComputeChangeFingerprintAsync(root, afterConfirmChanges, CancellationToken.None);
        if (!string.Equals(liveFingerprint, afterConfirmFingerprint, StringComparison.Ordinal))
        {
            InvalidatePreflight("确认期间工作区内容已变化，请重新检查。", resetImageConfirmation: false);
            ShowMessageRequested?.Invoke("确认期间文件发生变化，本次发布已取消，请重新检查。", true);
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
                ImageConfirmed = _imageConfirmed
                    && string.Equals(_currentImageFingerprint, _confirmedImageFingerprint, StringComparison.Ordinal),
                RequireImageConfirmation = _settings.RequireImagePrivacyConfirmation,
                RepoSizeBlockingMB = _settings.RepoSizeBlockingMB
            }, Log, _cts?.Token ?? CancellationToken.None);

            if (result.Informational)
            {
                // 非异常提示（如 0 变更）：INFO 日志 + 轻提示，不显示 ERROR 红叉
                Log(LogLevel.Info, result.Error ?? "当前工作区没有可提交的变更。");
                ShowMessageRequested?.Invoke(result.Error ?? "当前没有需要提交的文件。", false);
                return;
            }

            if (result.CommitCreatedButUnverified)
            {
                Log(LogLevel.Blocked, $"已生成但未通过安全校验的本地提交：{result.CommitShortHash ?? "未知"}");
            }
            else if (result.Committed)
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
                Log(result.IndexRestoredAfterAbort ? LogLevel.Info : LogLevel.Blocked,
                    result.IndexRestoredAfterAbort
                        ? "发布已中止，已恢复操作前的暂存状态。"
                        : "发布已中止，但暂存区恢复结果未确认，请人工检查。");
            }

            if (result.PushState == PushDeliveryState.Unknown)
            {
                ExistingCommitRecoverySummary = "本地提交已保留，远端接收结果尚未确认。请检查并上传已有提交。";
                ShowMessageRequested?.Invoke(NetworkRecoveryMessage(), true);
            }
            else if (result.PushState == PushDeliveryState.Pending)
            {
                ExistingCommitRecoverySummary = "本地提交已保留，尚未开始上传。可检查并上传已有提交。";
                ShowMessageRequested?.Invoke("本地提交已经创建并保留，但尚未开始上传。请点击“检查并上传已有提交”重新完成安全复检；不会重复创建提交。", true);
            }
            else
            {
                ShowMessageRequested?.Invoke(
                    result.Pushed ? "发布完成：已提交并推送。" :
                    result.Committed && !result.CommitCreatedButUnverified && string.IsNullOrEmpty(result.Error) ? "提交完成（未推送）。" :
                    result.Error ?? "发布失败。",
                    !(result.Pushed || (result.Committed && !result.CommitCreatedButUnverified && string.IsNullOrEmpty(result.Error))));
            }
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
            await RunChecksAsync(allowOperationLease: true);
        }
    }

    /// <summary>
    /// 检查并上传当前分支已有的本地提交。该流程不复用普通工作区 Gate，
    /// 也不调用 add/commit；准备与执行安全判断全部由 PublishWorkflowService 负责。
    /// </summary>
    private async Task PushExistingCommitAsync()
    {
        if (!CanPushExistingCommit) return;
        var root = LastContext?.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(root)) return;
        var projectPathAtStart = ProjectPath?.Trim() ?? string.Empty;

        SetOperationLease(true);
        string? finalSummary = null;
        try
        {
            var prepareToken = StartOperation("正在检查已有提交…");
            ExistingPushPlan? plan = null;
            var buildTarget = _settings.BuildBeforeCommit ? BuildTargetResolver.Resolve(root) : null;
            var requireBuildVerification = _settings.BuildBeforeCommit && buildTarget?.Kind != BuildTargetKind.None;
            var buildDisplay = !_settings.BuildBeforeCommit
                ? "根据设置跳过构建（仅上传已有提交）"
                : buildTarget?.Kind == BuildTargetKind.None
                    ? "未发现 .NET 构建目标，按项目类型跳过构建"
                    : "尚未完成当前 HEAD 构建验证";
            try
            {
                string? buildVerifiedCommitOid = null;
                if (requireBuildVerification)
                {
                    // 仅当工作区完全干净时，隔离构建的输入才能证明对应当前 HEAD。
                    // 不复用普通预检的旧 Build，也不把含未跟踪文件的工作区构建冒充提交证明。
                    var statusBefore = await _git.StatusPorcelainAsync(root, prepareToken);
                    if (!statusBefore.Success)
                    {
                        finalSummary = "构建前无法确认工作区状态，已禁止上传已有提交。";
                        Log(LogLevel.Blocked, $"读取构建前工作区状态失败：{GitRemoteService.RedactOutput(statusBefore.StdErrText)}");
                        ShowMessageRequested?.Invoke(finalSummary, true);
                        return;
                    }
                    if (GitRepositoryInspector.ParseStatusPorcelain(statusBefore.Stdout).Count > 0)
                    {
                        finalSummary = "当前还有未提交变更，无法证明构建结果属于已有提交。";
                        Log(LogLevel.Blocked, finalSummary);
                        ShowMessageRequested?.Invoke("当前工作区包含已修改、已暂存或未跟踪的文件。请先处理这些变更，再检查并上传已有提交；本工具不会把工作区构建结果冒充当前 HEAD 的构建证明。", true);
                        return;
                    }

                    var headBeforeResult = await _git.HeadOidResultAsync(root, prepareToken);
                    var headBefore = headBeforeResult.Success ? headBeforeResult.Stdout.FirstOrDefault()?.Trim() : null;
                    if (!IsFullObjectId(headBefore))
                    {
                        finalSummary = "无法锁定当前完整提交 OID，已禁止上传已有提交。";
                        Log(LogLevel.Blocked, finalSummary);
                        ShowMessageRequested?.Invoke(finalSummary, true);
                        return;
                    }

                    BusyText = "正在构建当前 HEAD…";
                    Log(LogLevel.Info, $"当前设置要求构建验证，正在对已有提交 {headBefore![..8]} 执行隔离构建…");
                    var build = await _buildService.BuildRepositoryAsync(root, false, prepareToken);
                    if (!build.BuildRun || !build.Succeeded)
                    {
                        buildDisplay = build.BuildRun ? "FAIL" : build.SkipReason;
                        finalSummary = "当前 HEAD 未通过构建验证，已禁止上传已有提交。";
                        Log(LogLevel.Blocked, $"已有提交构建验证未通过：{buildDisplay}");
                        ShowMessageRequested?.Invoke("当前设置要求提交前构建，但已有提交没有获得有效的构建通过证明，因此没有上传。请先修复构建问题或明确调整设置后重新检查。", true);
                        return;
                    }

                    var headAfterResult = await _git.HeadOidResultAsync(root, prepareToken);
                    var headAfter = headAfterResult.Success ? headAfterResult.Stdout.FirstOrDefault()?.Trim() : null;
                    var statusAfter = await _git.StatusPorcelainAsync(root, prepareToken);
                    if (!IsFullObjectId(headAfter)
                        || !string.Equals(headBefore, headAfter, StringComparison.Ordinal)
                        || !statusAfter.Success
                        || GitRepositoryInspector.ParseStatusPorcelain(statusAfter.Stdout).Count > 0)
                    {
                        finalSummary = "构建期间 HEAD 或工作区发生变化，构建证明已失效。";
                        Log(LogLevel.Blocked, finalSummary);
                        ShowMessageRequested?.Invoke("构建期间提交或工作区发生了变化。为避免上传未经验证的内容，本次已停止；请重新检查已有提交。", true);
                        return;
                    }

                    buildVerifiedCommitOid = headAfter;
                    buildDisplay = build.WarningCount == 0
                        ? $"PASS（{build.Duration.TotalSeconds:F1}s，绑定 {headAfter![..8]}）"
                        : $"PASS（{build.WarningCount} warnings，绑定 {headAfter![..8]}）";
                    Log(build.WarningCount == 0 ? LogLevel.Pass : LogLevel.Warn, $"已有提交隔离构建通过：{buildDisplay}");
                }

                Log(LogLevel.Info, "开始检查已有提交、分支、远端目标和待推送历史…");
                plan = await _publish.PrepareExistingPushAsync(new ExistingPushPrepareRequest
                {
                    RepositoryRoot = root,
                    RequireImageConfirmation = _settings.RequireImagePrivacyConfirmation,
                    RequireBuildVerification = requireBuildVerification,
                    BuildVerifiedCommitOid = buildVerifiedCommitOid,
                    RepoSizeBlockingMB = _settings.RepoSizeBlockingMB
                }, Log, prepareToken);
            }
            finally
            {
                IsBusy = false;
                BusyText = string.Empty;
            }

            if (plan == null) return;
            if (!plan.CanExecute
                || string.IsNullOrWhiteSpace(plan.CommitOid)
                || string.IsNullOrWhiteSpace(plan.RemoteTargetFingerprint))
            {
                var (summary, userMessage, isError) = DescribeExistingPushPlan(plan);
                finalSummary = summary;
                Log(isError ? LogLevel.Blocked : LogLevel.Info, plan.Message);
                ShowMessageRequested?.Invoke(userMessage, isError);
                return;
            }

            finalSummary = $"已找到 {plan.OutgoingCommitCount} 个待推送提交：{plan.Branch} · {plan.CommitShortHash} → {plan.RemoteDisplay}";
            ExistingCommitRecoverySummary = finalSummary;
            var confirmData = BuildExistingPushConfirmData(plan, buildDisplay);
            if (ConfirmPublishRequested == null || !await ConfirmPublishRequested(confirmData))
            {
                finalSummary = "已取消上传；本地提交未改变。需要时可重新检查并上传。";
                Log(LogLevel.Info, "用户取消上传已有提交；未执行 Push。");
                return;
            }

            // 全局租约会阻止正常 UI 改变项目；仍检查一次 VM 目标，防程序化路径切换绕过界面门禁。
            if (!string.Equals(LastContext?.RepositoryRoot, plan.RepositoryRoot, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ProjectPath?.Trim(), projectPathAtStart, StringComparison.OrdinalIgnoreCase))
            {
                finalSummary = "项目或仓库状态已变化，请重新检查已有提交。";
                Log(LogLevel.Blocked, finalSummary);
                ShowMessageRequested?.Invoke(finalSummary, true);
                return;
            }

            var executeToken = StartOperation("正在上传已有提交…");
            PublishResult result;
            try
            {
                result = await _publish.ExecuteExistingPushAsync(new ExistingPushExecuteRequest
                {
                    PlanId = plan.PlanId!,
                    CommitOid = plan.CommitOid!,
                    RemoteTargetFingerprint = plan.RemoteTargetFingerprint,
                    RequireImageConfirmation = _settings.RequireImagePrivacyConfirmation,
                    RequireBuildVerification = requireBuildVerification,
                    // 本次对话框的新确认严格绑定一次性计划；不读取主界面的旧图片确认。
                    ImageConfirmed = confirmData.ImageConfirmed
                }, Log, executeToken);
            }
            finally
            {
                IsBusy = false;
                BusyText = string.Empty;
            }

            var (resultSummary, resultMessage, resultError) = DescribeExistingPushResult(result);
            finalSummary = resultSummary;
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                // 服务返回的详情已脱敏；保留在日志供诊断，弹窗只显示自然语言操作指引。
                Log(resultError ? LogLevel.Error : LogLevel.Info, result.Error);
            }
            else
            {
                Log(resultError ? LogLevel.Error : LogLevel.Pass, resultMessage);
            }
            ShowMessageRequested?.Invoke(resultMessage, resultError);
        }
        catch (OperationCanceledException)
        {
            finalSummary = "检查或上传已取消；本地提交未改变。再次上传前请重新检查。";
            Log(LogLevel.Info, finalSummary);
        }
        catch (Exception ex)
        {
            finalSummary = "检查已有提交时发生异常，未执行盲目上传。请稍后重新检查。";
            Log(LogLevel.Error, $"检查并上传已有提交失败：{ex.Message}");
            ShowMessageRequested?.Invoke(finalSummary, true);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            try
            {
                await RunChecksAsync(allowOperationLease: true);
                if (!string.IsNullOrWhiteSpace(finalSummary)) ExistingCommitRecoverySummary = finalSummary;
            }
            finally
            {
                // 即使收尾预检自身异常，也不能把全局租约永久留在活动状态。
                SetOperationLease(false);
            }
        }
    }

    /// <summary>把一次性已有提交计划映射为只读确认页；安全判断仍由服务所有。</summary>
    private ConfirmPublishData BuildExistingPushConfirmData(ExistingPushPlan plan, string buildDisplay) => new()
    {
        RepositoryRoot = plan.RepositoryRoot,
        ProjectPath = ProjectPath,
        RepoDisplay = new DirectoryInfo(plan.RepositoryRoot).Name,
        Branch = plan.Branch,
        RemoteDisplay = "origin",
        PushUrlDisplay = plan.RemoteDisplay,
        AuthorDisplay = "-",
        CommitMessage = "只上传已有提交，不会再次 add 或 commit",
        CommitOidDisplay = plan.CommitOid ?? "-",
        ChangeCount = 0,
        OutgoingCommitCount = plan.OutgoingCommitCount,
        PassCount = 0,
        WarningCount = 0,
        BlockedCount = 0,
        ImageConfirmed = false,
        HasNewImages = plan.HasOutgoingImages,
        RequiresImageConfirmation = plan.RequiresImageConfirmation,
        BuildDisplay = buildDisplay,
        WillSetUpstream = false,
        CommitOnly = false,
        PushExistingOnly = true
    };

    private static (string Summary, string UserMessage, bool IsError) DescribeExistingPushPlan(ExistingPushPlan plan)
    {
        return plan.Disposition switch
        {
            ExistingPushDisposition.AlreadyUploaded =>
                ("远端已经包含当前提交，无需重复上传。", "远端已经包含当前提交，无需重复上传。", false),
            ExistingPushDisposition.NoLocalCommit =>
                ("当前仓库还没有可上传的本地提交。", "当前仓库还没有可上传的本地提交。", false),
            ExistingPushDisposition.Unknown =>
                ("暂时无法确认远端状态，未执行上传。网络恢复后可再次检查。", NetworkRecoveryMessage(), true),
            ExistingPushDisposition.DetachedHead =>
                ("当前不在明确分支上，已禁止上传已有提交。", "当前处于 detached HEAD，无法安全确定目标分支，因此没有上传。", true),
            ExistingPushDisposition.RemoteDrift =>
                ("本地与远端历史不一致，需要人工处理。", "本地与远端历史已经分叉或发生变化，本工具不会自动合并、重置或强制上传。", true),
            ExistingPushDisposition.Blocked =>
                ("已有提交未通过安全复检，已禁止上传。", "待推送历史未通过安全复检，已禁止上传。请查看日志中的脱敏详情。", true),
            _ => (plan.Message, plan.Message, true)
        };
    }

    private static (string Summary, string UserMessage, bool IsError) DescribeExistingPushResult(PublishResult result)
    {
        return result.PushState switch
        {
            PushDeliveryState.Pushed =>
                ("已有提交已安全上传。", "上传完成：已有提交已推送到远端，没有创建新提交。", false),
            PushDeliveryState.AlreadyUploaded =>
                ("远端已包含该提交，无需重复上传。", "远端已包含该提交，本次没有重复 Push，也没有创建新提交。", false),
            PushDeliveryState.Unknown =>
                ("远端接收结果暂时无法确认；请重新检查，勿直接重复上传。", NetworkRecoveryMessage(), true),
            PushDeliveryState.Blocked =>
                ("仓库或远端状态发生变化，已安全阻止上传。", "确认后仓库、分支、远端目标、远端状态或安全策略发生了变化，因此没有上传。请重新点击“检查并上传已有提交”。", true),
            PushDeliveryState.Pending =>
                ("已有提交仍保留在本地，尚未上传成功。", "上传没有完成。本地提交仍然保留，且没有创建重复提交。请查看日志中的脱敏详情，再点击“检查并上传已有提交”重新复检。", true),
            _ =>
                ("已有提交没有上传；请查看日志后重新检查。", "已有提交没有上传，且本地提交未被重复创建。请查看日志中的脱敏详情后重新检查。", true)
        };
    }

    private static string NetworkRecoveryMessage() =>
        "暂时无法确认远端是否已收到提交，因此没有盲目重试。\n\n" +
        "浏览器能打开 GitHub，不代表 git.exe 使用相同的网络路径；本地提交不会重复创建。" +
        "网络恢复后，请点击“检查并上传已有提交”。";

    /// <summary>仅接受 Git 完整 SHA-1/SHA-256 对象 ID，短哈希不能作为构建或上传绑定依据。</summary>
    private static bool IsFullObjectId(string? oid) =>
        oid is { Length: 40 or 64 } && oid.All(Uri.IsHexDigit);

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
            ImageConfirmed = !ImageConfirmationRequired
                || (_imageConfirmed && string.Equals(_currentImageFingerprint, _confirmedImageFingerprint, StringComparison.Ordinal)),
            HasNewImages = ctx.NewImages.Count > 0,
            RequiresImageConfirmation = !commitOnly
                && _settings.RequireImagePrivacyConfirmation
                && ctx.NewImages.Count > 0,
            BuildDisplay = buildDisplay,
            WillSetUpstream = !ctx.HasUpstream && !commitOnly,
            PushUrlDisplay = remote?.EffectivePushDisplay ?? "（未配置）",
            CommitOnly = commitOnly
        };
    }

    /// <summary>
    /// 判断当前 Push 唯一未完成的 Gate 是“本次图片脱敏确认”。
    /// 该入口主要供首次发布向导使用，避免持有全局租约时无法操作主界面勾选框。
    /// </summary>
    private bool CanResolveImageGateInConfirm(bool commitOnly)
    {
        if (commitOnly || !_settings.RequireImagePrivacyConfirmation || !ImageConfirmationRequired
            || _imageConfirmed || string.IsNullOrEmpty(_currentImageFingerprint)
            || LastContext?.Report == null || CommittableChangeCount <= 0
            || string.IsNullOrWhiteSpace(CommitMessage) || IsBusy)
        {
            return false;
        }

        var report = LastContext.Report;
        if (report.HasCommitBlock) return false;
        return !report.Checks.Any(check => check.Id != "image_privacy"
            && check.BlocksPush
            && check.Status is not CheckStatus.Pass and not CheckStatus.Info);
    }

    // ---------- 首次发布向导 ----------
    private async Task RunFirstPublishWizardAsync(string msg, bool commitOnly)
    {
        if (IsBusy || _operationLeaseActive) return;
        if (WizardRequested == null) return;
        if (string.IsNullOrWhiteSpace(ProjectPath) || !Directory.Exists(ProjectPath))
        {
            ShowMessageRequested?.Invoke("请先选择有效的项目目录。", true);
            return;
        }

        // 从向导弹窗打开到最终预检/确认/发布结束始终持有同一租约，
        // 中间即使 IsBusy 短暂为 false，路径、设置及其它状态命令也不可介入。
        SetOperationLease(true);
        try
        {
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

            var ct = StartOperation("执行首次发布向导…");
            InvalidatePreflight("首次发布向导正在修改仓库，完成后将重新检查。", resetImageConfirmation: true);

            // 步骤 1：初始化
            if (result.InitGit)
            {
                Log(LogLevel.Info, "将执行：git init -b main");
                var r = await _git.InitAsync(ProjectPath, ct);
                if (!r.Success)
                {
                    Log(LogLevel.Error, $"git init 失败：{GitRemoteService.RedactOutput(r.StdErrText)}");
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
                    .ApplyRecommendedAsync(ProjectPath, _settings.RecommendedGitName, _settings.RecommendedGitEmail, ct);
                Log(ok ? LogLevel.Pass : LogLevel.Error, ok ? "Local Git 身份已设置。" : err);
                if (!ok) return;
            }

            // 步骤 4：按用户勾选配置 origin。必须先于完整预检，避免预检因缺 origin
            // 阻断后又继续走陈旧上下文；未勾选时绝不修改 Remote。
            if (result.SetOrigin)
            {
                var originUrl = result.OriginUrl.Trim();
                if (string.IsNullOrWhiteSpace(originUrl))
                {
                    Log(LogLevel.Error, "已勾选配置 origin，但 URL 为空。");
                    ShowMessageRequested?.Invoke("已勾选配置 origin，请填写 Remote URL。", true);
                    return;
                }

                var remoteResult = await _git.RemoteVAsync(ProjectPath, ct);
                if (!remoteResult.Success)
                {
                    Log(LogLevel.Error, $"读取 origin 失败：{GitRemoteService.RedactOutput(remoteResult.StdErrText)}");
                    return;
                }

                var remoteInfo = GitRepositoryInspector.ParseRemoteV(remoteResult.Stdout);
                Log(LogLevel.Info, remoteInfo.HasRemote
                    ? $"将执行：git remote set-url origin {GitRemoteService.RedactForDisplay(originUrl)}"
                    : $"将执行：git remote add origin {GitRemoteService.RedactForDisplay(originUrl)}");
                var remoteWrite = remoteInfo.HasRemote
                    ? await _git.RemoteSetUrlAsync(ProjectPath, "origin", originUrl, ct)
                    : await _git.RemoteAddAsync(ProjectPath, "origin", originUrl, ct);
                if (!remoteWrite.Success)
                {
                    Log(LogLevel.Error, $"配置 origin 失败：{GitRemoteService.RedactOutput(remoteWrite.StdErrText)}");
                    return;
                }
                Log(LogLevel.Pass, "origin 已配置。");
            }

            // 步骤 5：所有仓库修改完成后只跑一次完整预检。
            IsBusy = false;
            BusyText = string.Empty;
            if (!await RunChecksAsync(allowOperationLease: true) || LastContext?.RepositoryRoot == null)
            {
                ShowMessageRequested?.Invoke("首次发布检查未通过，请处理检查项后重试。", true);
                return;
            }

            // 步骤 6：直接进入已持有租约的确认/发布核心，禁止递归调用 PublishAsync。
            CommitMessage = result.CommitMessage;
            var finalMessage = result.CommitMessage.Trim();
            if (string.IsNullOrWhiteSpace(finalMessage))
            {
                ShowMessageRequested?.Invoke("请填写 Commit Message。", true);
                return;
            }
            await PublishCheckedAsync(commitOnly, finalMessage, refreshPreflight: false);
        }
        catch (OperationCanceledException)
        {
            InvalidatePreflight("首次发布向导已取消，请重新检查。", resetImageConfirmation: true);
            Log(LogLevel.Info, "首次发布向导已取消。");
        }
        catch (Exception ex)
        {
            InvalidatePreflight("首次发布向导异常中止，请重新检查。", resetImageConfirmation: true);
            Log(LogLevel.Error, $"向导执行失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            SetOperationLease(false);
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
        if (IsBusy) return;
        var root = LastContext?.RepositoryRoot;
        if (root == null)
        {
            Log(LogLevel.Error, "当前不是 Git 仓库。");
            return;
        }

        var ct = StartOperation("同步远端…");
        Log(LogLevel.Info, "将执行：git pull --ff-only");
        var synchronized = false;
        try
        {
            var result = await _git.PullFfOnlyAsync(root, ct);
            if (result.Success)
            {
                synchronized = true;
                InvalidatePreflight("同步远端已改变仓库状态，正在重新检查。", resetImageConfirmation: true);
                Log(LogLevel.Pass, "同步完成（Fast-forward）。");
            }
            else
            {
                var err = GitRemoteService.RedactOutput((result.StdErrText + "\n" + result.StdOutText).Trim());
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

        if (synchronized) await RunChecksAsync();
    }

    // ---------- 设置 ----------
    private async Task ShowSettingsAsync()
    {
        if (IsBusy) return;
        if (SettingsRequested == null) return;
        var data = new SettingsData { Settings = _settings.Clone(), SettingsPath = _settingsService.SettingsPath };
        var saved = await SettingsRequested(data);
        if (saved)
        {
            _settings = data.Settings;
            _settingsService.Save(_settings);
            RebuildPolicyServices();
            InvalidatePreflight("设置已变化，旧预检与构建结论已失效。", resetImageConfirmation: true);
            Log(LogLevel.Pass, "设置已保存。");
            _settings.AddRecentProject(ProjectPath);
            _settingsService.Save(_settings);
            if (!string.IsNullOrWhiteSpace(ProjectPath) && Directory.Exists(ProjectPath))
            {
                await RunChecksAsync();
            }
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
