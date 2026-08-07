using System.IO;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.E2E;

/// <summary>
/// Zero Change Gate 端到端场景（真实 git + %TEMP% 临时仓库）。
/// ZERO-01/02/03：VM 层（UI 状态与按钮使能）；
/// ZERO-04：workflow 层二次检查拦截（检查通过 → 外部还原为 0 变更 → 不得 add/commit/push）；
/// ZERO-05：确认页按钮禁用（见单测 DialogSmokeTests.ConfirmDialog_ZeroChanges_ButtonDisabled）；
/// ZERO-06：对照用例，1 变更正常提交不受影响；
/// ZERO-07：0 变更 + 必败 csproj → Build Gate 必须整体跳过（Not Required，不得 Blocked）；
/// ZERO-08：成功 commit+push 后刷新 → 0 变更 → UP TO DATE（self-host 缺陷核心场景）。
/// </summary>
public static class ZeroChangeScenarios
{
    private static readonly ProcessRunner Runner = new();
    private static readonly GitService Git = new(Runner);

    private static async Task<string> NewRepo(string name)
    {
        var dir = Path.Combine(Program.RootDir, name);
        Directory.CreateDirectory(dir);
        Program.Track(dir);
        var init = await Git.InitAsync(dir);
        E2EAssert.True(init.Success, $"git init 失败：{init.StdErrText}");
        var (ok, err) = await new GitIdentityService(Git).ApplyRecommendedAsync(dir, "VkDream", "312913839+VkDream@users.noreply.github.com");
        E2EAssert.True(ok, $"设置本地身份失败：{err}");
        await GitIgnoreService.ApplyAsync(dir);
        return dir;
    }

    private static void Write(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    /// <summary>创建本地 bare 仓库并配置为 origin（模拟 GitHub 远端）。</summary>
    private static async Task<string> NewBareOrigin(string name)
    {
        var dir = Path.Combine(Program.RootDir, name);
        Directory.CreateDirectory(dir);
        Program.Track(dir);
        var init = await Git.InitAsync(dir);
        E2EAssert.True(init.Success, $"bare init 失败：{init.StdErrText}");
        return dir;
    }

    private static async Task AddRemote(string repo, string url)
    {
        var r = await Git.RemoteAddAsync(repo, "origin", url);
        E2EAssert.True(r.Success, $"remote add 失败：{r.StdErrText}");
    }

    private static async Task<string> InitialCommit(string repo)
    {
        var add = await Git.AddAllAsync(repo);
        E2EAssert.True(add.Success, $"git add 失败：{add.StdErrText}");
        var commit = await Git.CommitAsync(repo, "chore: init");
        E2EAssert.True(commit.Success, $"git commit 失败：{commit.StdErrText}");
        return await Git.HeadShortAsync(repo) ?? string.Empty;
    }

    private static PreflightService NewPreflight() => new(
        Git,
        new SensitiveFileScanner(Git),
        new SecretScanner(),
        new LargeFileScanner(),
        new DotNetBuildService(Runner));

    private static PublishWorkflowService NewPublish() => new(
        Git,
        new SensitiveFileScanner(Git),
        new SecretScanner(),
        new LargeFileScanner());

    // ---------- ZERO-01：VM 层，0 变更时任意提交说明均不得解锁 ----------
    [E2ETest]
    public static async Task Z01_VM_ZeroChanges_MessageCannotEnable()
    {
        var repo = await NewRepo("z01_vm_zero");
        await InitialCommit(repo);
        var bare = await NewBareOrigin("z01_origin");
        await AddRemote(repo, bare);

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();

        E2EAssert.True(!vm.CanCommit, "0 变更 → CanCommit 必须 false");
        E2EAssert.True(!vm.CanPush, "0 变更 → CanPush 必须 false");
        E2EAssert.Equal("UP TO DATE", vm.PublishBannerTitle, "0 变更 Banner 应为 UP TO DATE");
        E2EAssert.True(vm.CommitTooltip.Contains("没有可提交的变更"), $"Tooltip 应提示无变更，实际：{vm.CommitTooltip}");

        // 人工验收回归核心：0 变更 + "test:" 也不得解锁
        vm.CommitMessage = "test: 1";
        E2EAssert.True(!vm.CanCommit, "0 变更 + 提交说明 → CanCommit 仍必须 false");
        E2EAssert.True(!vm.CanPush, "0 变更 + 提交说明 → CanPush 仍必须 false");
        E2EAssert.True(!vm.CommitOnlyCommand.CanExecute(null), "命令级 CanExecute 必须 false（防快捷键）");
        E2EAssert.True(!vm.SafeCommitAndPushCommand.CanExecute(null), "SafeCommitAndPush 命令级 CanExecute 必须 false");

        vm.CommitMessage = string.Empty;
        E2EAssert.True(!vm.CanCommit, "清空说明后仍必须 false");
    }

    // ---------- ZERO-02：VM 层，1 变更 + 说明解锁提交（仅提交） ----------
    [E2ETest]
    public static async Task Z02_VM_OneChange_EnablesCommitOnly()
    {
        var repo = await NewRepo("z02_vm_one");
        await InitialCommit(repo);
        var bare = await NewBareOrigin("z02_origin");
        await AddRemote(repo, bare);
        Write(repo, "a.txt", "v1");

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();

        E2EAssert.Equal(1, vm.CommittableChangeCount, "应有 1 个可提交变更");
        E2EAssert.True(!vm.CanCommit, "空说明时仍应禁用");
        E2EAssert.True(vm.CommitTooltip.Contains("提交说明"), $"Tooltip 应提示填写说明，实际：{vm.CommitTooltip}");

        vm.CommitMessage = "test: 1";
        E2EAssert.True(vm.CanCommit, "1 变更 + 说明 → CanCommit 必须 true");
        E2EAssert.True(vm.CanPush, "已配置 Remote + 无图片 → CanPush 必须 true");
        E2EAssert.True(vm.CommitOnlyCommand.CanExecute(null), "命令级 CanExecute 应随门控解锁");
        E2EAssert.Equal("READY TO PUBLISH", vm.PublishBannerTitle, "1 变更可提交时应为 READY TO PUBLISH");
    }

    // ---------- ZERO-03：VM 层，图片确认开关实时刷新 Push 门控 ----------
    [E2ETest]
    public static async Task Z03_VM_ImageConfirm_RefreshesPushGate()
    {
        var repo = await NewRepo("z03_img");
        await InitialCommit(repo);
        var bare = await NewBareOrigin("z03_origin");
        await AddRemote(repo, bare);
        Write(repo, "img/a.png", "x");

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();

        E2EAssert.True(vm.ImageConfirmationRequired, "新增图片应要求脱敏确认");
        vm.CommitMessage = "feat: 图片";
        E2EAssert.True(!vm.CanPush, "图片未确认 → CanPush 必须 false");
        E2EAssert.True(vm.PushTooltip.Contains("图片"), $"Tooltip 应提示图片确认，实际：{vm.PushTooltip}");

        vm.ImageConfirmed = true;
        E2EAssert.True(vm.CanPush, "确认后应实时解锁 CanPush");
        vm.ImageConfirmed = false;
        E2EAssert.True(!vm.CanPush, "取消确认应实时锁定 CanPush");
    }

    // ---------- ZERO-04：workflow 层二次检查拦截 0 变更 ----------
    [E2ETest]
    public static async Task Z04_Workflow_ZeroChanges_Intercepted()
    {
        var repo = await NewRepo("z04_wf");
        await InitialCommit(repo);
        Write(repo, "b.txt", "v1");

        var ctx = await NewPreflight().RunAsync(repo, new AppSettings(), log: null, imageConfirmed: true);
        E2EAssert.True(ctx.Report.CanCommit, "检查阶段应有可提交内容");

        // 检查后被外部还原为 0 变更（模拟用户在检查后撤销全部变更）
        File.Delete(Path.Combine(repo, "b.txt"));
        var before = await Git.HeadShortAsync(repo);

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "test: 1",
            Mode = PublishWorkflowService.PublishMode.CommitOnly
        });

        E2EAssert.True(result.Informational, "0 变更应返回 Informational（非异常）提示");
        E2EAssert.True(result.Error != null && result.Error.Contains("没有可提交的变更"),
            $"错误文案应为无变更提示，实际：{result.Error}");
        E2EAssert.True(!result.Committed, "不得产生提交");

        var after = await Git.HeadShortAsync(repo);
        E2EAssert.Equal(before, after, "HEAD 不得变化（未执行 add/commit）");
    }

    // ---------- ZERO-06：对照用例，1 变更正常提交 ----------
    [E2ETest]
    public static async Task Z06_Workflow_OneChange_ProceedsNormally()
    {
        var repo = await NewRepo("z06_wf");
        await InitialCommit(repo);
        Write(repo, "c.txt", "v1");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: c",
            Mode = PublishWorkflowService.PublishMode.CommitOnly
        });

        E2EAssert.True(result.Committed, "1 变更应正常提交");
        E2EAssert.True(!result.Informational, "正常流程不应标记 Informational");
    }

    // ---------- ZERO-07（self-host 缺陷回归核心）：0 变更 + .NET 仓库 → Build Gate 必须整体跳过 ----------
    // 故意放置一个必然构建失败的 csproj：如果 0 变更时仍执行 dotnet build，
    // 该仓库必然触发 Build Blocked → 本测试即失败，从而证明"跳过"真实生效。
    [E2ETest]
    public static async Task Z07_ZeroChanges_DotNetRepo_BuildGateSkipped()
    {
        var repo = await NewRepo("z07_buildskip");
        await InitialCommit(repo);
        var bare = await NewBareOrigin("z07_origin");
        await AddRemote(repo, bare);

        // 必败 csproj（引用不存在的包）：只要 build 被执行必然 ExitCode != 0
        Write(repo, "BadApp/BadApp.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
            "  <ItemGroup><PackageReference Include=\"NoSuch.Package.999\" Version=\"9.9.9\" /></ItemGroup>\n" +
            "</Project>\n");
        var add = await Git.AddAllAsync(repo);
        E2EAssert.True(add.Success, $"git add 失败：{add.StdErrText}");
        var commit = await Git.CommitAsync(repo, "chore: add bad csproj");
        E2EAssert.True(commit.Success, $"git commit 失败：{commit.StdErrText}");

        var ctx = await NewPreflight().RunAsync(repo, new AppSettings(), log: null, imageConfirmed: true);

        E2EAssert.True(ctx.Build != null, "ctx.Build 应存在");
        E2EAssert.True(ctx.Build!.BuildRun == false, "0 变更时 Build 不得执行");
        E2EAssert.True(ctx.Build.SkipReason.Contains("无可提交变更"),
            $"SkipReason 应说明无变更，实际：{ctx.Build.SkipReason}");
        var buildCheck = ctx.Report.Checks.FirstOrDefault(c => c.Id == "build");
        E2EAssert.True(buildCheck != null, "build 检查项应存在");
        if (buildCheck != null)
        {
            E2EAssert.True(buildCheck.Status != CheckStatus.Blocked,
                $"0 变更时 build 检查不得为 Blocked，实际：{buildCheck.Status}");
        }
        E2EAssert.True(!ctx.Report.HasCommitBlock && !ctx.Report.HasPushBlock,
            "0 变更 + 必败 csproj 也不得产生任何阻断");
        // 0 变更的 CanCommit=false 是 PublishGateEvaluator 门控合同（非检查项阻断）
        var gate = PublishGateEvaluator.Evaluate(ctx.Report, 0, "test: 1", busy: false, newImageCount: 0, imageConfirmed: true, requireImageConfirmation: false);
        E2EAssert.True(!gate.CanCommit && !gate.CanPush,
            "0 变更 → 门控不允许提交/推送（原因是无变更，非阻断）");
    }

    // ---------- ZERO-08（self-host 缺陷核心场景）：成功提交+推送后刷新 → 0 变更 → UP TO DATE ----------
    // 复现真实缺陷路径：发布（commit+push）成功 → 工作区归零 → 刷新预检。
    // 修复前：post-publish 全量预检真实执行 dotnet build 可能失败 → 误显示 PUBLISH BLOCKED。
    // 修复后：0 变更 → Build 跳过 + Banner=UP TO DATE。
    [E2ETest]
    public static async Task Z08_PostPublish_Refresh_ZeroChanges_UpToDate()
    {
        var repo = await NewRepo("z08_postpub");
        await InitialCommit(repo);
        var bare = await NewBareOrigin("z08_origin.git");
        await AddRemote(repo, bare);
        Write(repo, "feature.txt", "v1");

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();
        E2EAssert.Equal(1, vm.CommittableChangeCount, "发布前应有 1 个可提交变更");
        vm.CommitMessage = "feat: post-publish refresh";
        E2EAssert.True(vm.CanCommit, "发布前应可提交");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: post-publish refresh",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Committed && result.Pushed, $"提交+推送应成功：{result.Error}");

        // 发布后刷新（PublishAsync finally 中 RunChecksAsync 的等价路径）
        await vm.RunChecksAsync();

        E2EAssert.Equal(0, vm.CommittableChangeCount, "发布后工作区应归零");
        E2EAssert.True(!vm.CanCommit, "发布后 CanCommit=false（原因是无变更，非阻断）");
        E2EAssert.True(!vm.CanPush, "发布后 CanPush=false（原因是无变更，非阻断）");
        E2EAssert.Equal("UP TO DATE", vm.PublishBannerTitle,
            "发布后 0 变更必须显示 UP TO DATE，不得出现假 PUBLISH BLOCKED");

        var build = vm.LastContext!.Build;
        E2EAssert.True(build != null && build.BuildRun == false, "发布后刷新时 Build 不得执行");
        E2EAssert.True(build!.SkipReason.Contains("无可提交变更"),
            $"发布后刷新 SkipReason 应为无变更，实际：{build.SkipReason}");
    }
}
