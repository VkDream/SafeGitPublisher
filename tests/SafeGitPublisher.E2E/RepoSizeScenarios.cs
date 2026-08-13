using System.IO;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.E2E;

/// <summary>
/// 仓库总体积门禁 E2E（真实 git.exe + 临时仓库）。
/// 通过注入小阈值（MB 级）验证完整链路，避免真实创建 GB 级文件。
/// 预检第 13 项 repo_size 与最终发布门禁（QuickSafetyCheck）必须同合同。
/// </summary>
public static class RepoSizeScenarios
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

    private static PreflightService NewPreflight() => new(
        Git,
        new SensitiveFileScanner(Git),
        new SecretScanner(),
        new LargeFileScanner(),
        new DotNetBuildService(Runner));

    /// <summary>快速创建指定大小的文件（SetLength，不写实际内容）。</summary>
    private static void CreateSizedFile(string repo, string rel, double mb)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
        fs.SetLength((long)(mb * 1024 * 1024));
    }

    private static AppSettings SizeSettings(double warnMB, double blockMB) => new()
    {
        RepoSizeWarningMB = warnMB,
        RepoSizeBlockingMB = blockMB
    };

    // ---------- RS01：总量超阻断阈值 → 预检阻断 + 最终门禁拦截 ----------

    [E2ETest]
    public static async Task RS01_TotalSizeBlocked_PreflightAndFinalGate()
    {
        var repo = await NewRepo("rs01_blocked");
        // 单文件 2.5MB：单文件检查 Normal（<10MB），但总量 2.5MB > 阻断阈值 2MB
        CreateSizedFile(repo, "data/output.bin", 2.5);
        var settings = SizeSettings(warnMB: 1, blockMB: 2);

        var ctx = await NewPreflight().RunAsync(repo, settings);
        var sizeCheck = ctx.Report.Checks.First(c => c.Id == "repo_size");
        E2EAssert.Equal(CheckStatus.Blocked, sizeCheck.Status, "总量 2.5MB > 2MB 应阻断");
        E2EAssert.True(sizeCheck.BlocksCommit && sizeCheck.BlocksPush, "repo_size 应同时阻断提交与推送");
        E2EAssert.True(sizeCheck.Details.Contains(".bin", StringComparison.OrdinalIgnoreCase), "详情应含扩展名 Top 汇总");
        E2EAssert.True(!ctx.Report.CanCommit, "总量阻断应禁止提交");

        // 最终门禁（QuickSafetyCheck）必须同合同拦截
        var result = await new PublishWorkflowService(Git, new SensitiveFileScanner(Git), new SecretScanner(), new LargeFileScanner())
            .ExecuteAsync(new PublishWorkflowService.PublishRequest
            {
                RepositoryRoot = repo,
                CommitMessage = "feat: big total",
                Mode = PublishWorkflowService.PublishMode.CommitOnly,
                RepoSizeBlockingMB = 2
            });
        E2EAssert.True(!result.Committed, "最终门禁应拦截总量超限的提交");
        E2EAssert.True(result.Error != null && result.Error.Contains("总体积", StringComparison.Ordinal), "错误信息应指向总体积门禁");
    }

    // ---------- RS02：总量超警告阈值但不超阻断阈值 → 警告不阻断 ----------

    [E2ETest]
    public static async Task RS02_TotalSizeWarning_NotBlocking()
    {
        var repo = await NewRepo("rs02_warning");
        // 总量 1.5MB：> 警告阈值 1MB 但 < 阻断阈值 2MB
        CreateSizedFile(repo, "images/photo.bmp", 1.5);
        var settings = SizeSettings(warnMB: 1, blockMB: 2);

        var ctx = await NewPreflight().RunAsync(repo, settings, imageConfirmed: true);
        var sizeCheck = ctx.Report.Checks.First(c => c.Id == "repo_size");
        E2EAssert.Equal(CheckStatus.Warning, sizeCheck.Status, "总量 1.5MB > 1MB 应警告");
        E2EAssert.True(!sizeCheck.BlocksCommit && !sizeCheck.BlocksPush, "警告级不应阻断");
        E2EAssert.True(ctx.Report.CanCommit, "总量警告不应禁止提交");
    }

    // ---------- RS03：小体积 → Pass；检查项含 repo_size（13 项合同） ----------

    [E2ETest]
    public static async Task RS03_TotalSizePass_CheckListContainsRepoSize()
    {
        var repo = await NewRepo("rs03_pass");
        File.WriteAllText(Path.Combine(repo, "hello.txt"), "small", Encoding.UTF8);

        var ctx = await NewPreflight().RunAsync(repo, new AppSettings());
        var sizeCheck = ctx.Report.Checks.FirstOrDefault(c => c.Id == "repo_size");
        E2EAssert.True(sizeCheck != null, "检查列表必须包含 repo_size 项");
        E2EAssert.Equal(CheckStatus.Pass, sizeCheck!.Status, "小体积应 Pass");
        E2EAssert.True(ctx.Report.Checks.Count >= 13, "检查项应不少于 13 项（新增 repo_size 后）");
    }
}
