using System.IO;
using System.Reflection;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.E2E;

/// <summary>
/// 端到端验证（真实 git.exe + 临时仓库）。
/// 只操作 %TEMP% 下的临时目录；不使用任何用户真实仓库；不修改全局 git 配置。
/// </summary>
public static class Program
{
    private static string _root = string.Empty;
    private static readonly List<string> _created = new();

    public static string RootDir => _root;

    public static void Track(string path) => _created.Add(path);

    public static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        _root = Path.Combine(Path.GetTempPath(), "SafeGitPublisherE2E", $"run_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        Console.WriteLine($"E2E 临时根目录: {_root}");

        var methods = typeof(Program).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<E2ETestAttribute>() != null)
            .OrderBy(m => m.Name)
            .ToList();

        var pass = 0;
        var fail = 0;
        var failures = new List<string>();

        foreach (var m in methods)
        {
            try
            {
                var result = m.Invoke(null, null);
                if (result is Task t) await t;
                pass++;
                Console.WriteLine($"[PASS] {m.Name}");
            }
            catch (TargetInvocationException tie)
            {
                fail++;
                var ex = tie.InnerException ?? tie;
                Console.WriteLine($"[FAIL] {m.Name} :: {ex.Message}");
                failures.Add(m.Name);
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"[FAIL] {m.Name} :: {ex.Message}");
                failures.Add(m.Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"E2E 结果: {pass} 通过, {fail} 失败, 共 {pass + fail} 项");
        foreach (var f in failures) Console.WriteLine("  失败项: " + f);

        Cleanup();
        return fail == 0 ? 0 : 1;
    }

    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            Console.WriteLine("  提示：E2E 临时目录清理失败，可手动删除 " + _root);
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class E2ETestAttribute : Attribute
{
}

internal static class E2EAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new Exception("断言失败：" + message);
    }

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!Equals(expected, actual)) throw new Exception($"断言失败：{message}（期望 <{expected}>，实际 <{actual}>）");
    }
}

/// <summary>
/// E2E 用例：真实 git 操作 + PreflightService / PublishWorkflowService 全链路。
/// </summary>
public static class Scenarios
{
    private static readonly ProcessRunner Runner = new();
    private static readonly GitService Git = new(Runner);

    /// <summary>初始化一个临时 git 仓库并应用推荐身份。</summary>
    private static async Task<string> NewRepo(string name, bool applyIdentity = true, bool generateGitignore = true)
    {
        var dir = Path.Combine(Program.RootDir, name);
        Directory.CreateDirectory(dir);
        Program.Track(dir);
        var init = await Git.InitAsync(dir);
        E2EAssert.True(init.Success, $"git init 失败：{init.StdErrText}");
        if (applyIdentity)
        {
            var (ok, err) = await new GitIdentityService(Git).ApplyRecommendedAsync(dir, "VkDream", "312913839+VkDream@users.noreply.github.com");
            E2EAssert.True(ok, $"设置本地身份失败：{err}");
        }
        if (generateGitignore)
        {
            await GitIgnoreService.ApplyAsync(dir);
        }
        return dir;
    }

    /// <summary>创建本地 bare 仓库作为 origin（模拟 GitHub 远端）。</summary>
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

    private static void Write(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
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

    private static Task<PreflightContext> RunChecks(string repo, bool imageConfirmed = false)
    {
        return NewPreflight().RunAsync(repo, new AppSettings(), log: null, imageConfirmed: imageConfirmed);
    }

    // ---------- 1) git init 基本初始化（-b main） ----------
    [E2ETest]
    public static async Task T01_Init_BranchMain()
    {
        var repo = await NewRepo("t01_init");
        var branch = await Git.CurrentBranchAsync(repo);
        E2EAssert.Equal("main", branch, "git init 后默认分支应为 main");
    }

    // ---------- 2) 仅本地提交（不推送） ----------
    [E2ETest]
    public static async Task T02_CommitOnly()
    {
        var repo = await NewRepo("t02_commit");
        Write(repo, "a.txt", "hello");
        var ctx = await RunChecks(repo);
        E2EAssert.True(ctx.Report.CanCommit, "基础仓库应允许提交");
        E2EAssert.True(!ctx.Report.CanPush, "未配置 origin 时不允许 Push");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 初始化",
            Mode = PublishWorkflowService.PublishMode.CommitOnly
        });
        E2EAssert.True(result.Committed && !result.Pushed, "仅提交模式应提交成功且不推送");
        E2EAssert.True(!string.IsNullOrEmpty(result.CommitShortHash), "应返回提交短哈希");
    }

    // ---------- 3) Push 到本地 bare origin（模拟 GitHub） ----------
    [E2ETest]
    public static async Task T03_PushToOrigin()
    {
        var bare = await NewBareOrigin("t03_origin.git");
        var repo = await NewRepo("t03_repo");
        await AddRemote(repo, bare);
        Write(repo, "app.cs", "class App { }");
        var ctx = await RunChecks(repo);
        E2EAssert.True(ctx.Report.CanPush, "配置 origin 后应允许 Push");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 首次推送",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Pushed, $"应推送成功：{result.Error}");
        E2EAssert.True(result.UsedSetUpstream, "首次推送应使用 push -u");

        var bareLog = await Git.RunGitAsync(bare, new[] { "log", "--oneline", "-1" });
        E2EAssert.True(bareLog.Success && bareLog.Stdout.Count > 0, "bare 仓库应包含推送的提交");
    }

    // ---------- 4) 中文 + 空格路径仓库 ----------
    [E2ETest]
    public static async Task T04_ChineseSpacePaths()
    {
        var bare = await NewBareOrigin("t04_origin.git");
        var repo = await NewRepo("t04_中文 发布 仓库");
        await AddRemote(repo, bare);
        Write(repo, "新文件 1.txt", "内容一");
        Write(repo, "子目录/中文 文档.md", "# 文档");

        var status = await Git.StatusPorcelainAsync(repo);
        var lines = string.Join("\n", status.Stdout);
        E2EAssert.True(lines.Contains("新文件 1.txt", StringComparison.Ordinal), "中文路径应原样出现在 status 输出");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 中文路径",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Pushed, $"中文路径应可推送：{result.Error}");

        var ls = await Git.LsFilesAsync(repo);
        var lsText = string.Join("\n", ls.Stdout);
        E2EAssert.True(lsText.Contains("新文件 1.txt", StringComparison.Ordinal), "已跟踪列表应含中文文件名");
        E2EAssert.True(lsText.Contains("子目录/中文 文档.md", StringComparison.Ordinal), "已跟踪列表应含子目录中文文件");
    }

    // ---------- 5) .db 未忽略 → 阻断 ----------
    [E2ETest]
    public static async Task T05_DbUnignored_Blocked()
    {
        var repo = await NewRepo("t05_db_block", generateGitignore: false);
        Write(repo, "data.db", "\u0000\u0001");
        var ctx = await RunChecks(repo);
        var sensitive = ctx.Report.Checks.FirstOrDefault(c => c.Id == "sensitive_files");
        E2EAssert.True(sensitive != null && sensitive.Status == CheckStatus.Blocked, "未忽略 .db 应阻断");
        E2EAssert.True(!ctx.Report.CanCommit, ".db 阻断提交");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "bad",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Error != null && result.Error.Contains("敏感", StringComparison.Ordinal), "发布应被安全门拦截");
        E2EAssert.True(!result.Committed, "阻断时不应产生提交");
    }

    // ---------- 6) .db 已被 .gitignore 排除 → 不阻断 ----------
    [E2ETest]
    public static async Task T06_DbIgnored_Allowed()
    {
        var repo = await NewRepo("t06_db_ignored");
        Write(repo, "data.db", "\u0000\u0001");
        var ctx = await RunChecks(repo);
        E2EAssert.True(ctx.Report.CanCommit, ".db 被忽略后应放行");
        E2EAssert.True(ctx.IgnoredSafePaths.Contains("data.db"), "应记录已安全忽略的路径");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 正常提交",
            Mode = PublishWorkflowService.PublishMode.CommitOnly
        });
        E2EAssert.True(result.Committed, "被忽略 .db 不应阻断提交");
    }

    // ---------- 7) 已跟踪 .db → 仍阻断 ----------
    [E2ETest]
    public static async Task T07_TrackedDb_Blocked()
    {
        // 先生成无 .gitignore 的仓库，把 .db 真正提交入库，再检查“已跟踪敏感文件”仍阻断
        var repo = await NewRepo("t07_tracked_db", generateGitignore: false);
        Write(repo, "legacy.db", "\u0000\u0001");
        var add = await Git.AddAllAsync(repo);
        E2EAssert.True(add.Success, "git add 失败");
        var commit = await Git.CommitAsync(repo, "chore: 历史入库");
        E2EAssert.True(commit.Success, "git commit 失败");
        var ls = await Git.LsFilesAsync(repo);
        E2EAssert.True(string.Join("\n", ls.Stdout).Contains("legacy.db", StringComparison.Ordinal), "legacy.db 应已入库");

        var ctx = await RunChecks(repo);
        E2EAssert.True(!ctx.Report.CanCommit, "已跟踪 .db 应阻断发布");
    }

    // ---------- 8) 图片未确认 → 禁 Push；确认后放行 ----------
    [E2ETest]
    public static async Task T08_ImageGate()
    {
        var bare = await NewBareOrigin("t08_origin.git");
        var repo = await NewRepo("t08_image");
        await AddRemote(repo, bare);
        File.WriteAllBytes(Path.Combine(repo, "shot.png"), new byte[] { 1, 2, 3, 4 });

        var ctxNo = await RunChecks(repo, imageConfirmed: false);
        E2EAssert.True(ctxNo.Report.CanCommit, "图片未确认不应阻断提交");
        E2EAssert.True(!ctxNo.Report.CanPush, "图片未确认应阻断 Push");
        var imgCheck = ctxNo.Report.Checks.First(c => c.Id == "image_privacy");
        E2EAssert.True(imgCheck.RequiresConfirmation, "图片检查应要求人工确认");

        var pushNo = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 截图",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush,
            ImageConfirmed = false
        });
        E2EAssert.True(!pushNo.Pushed, "未确认图片不应推送");
        E2EAssert.True(pushNo.Error != null && pushNo.Error.Contains("图片", StringComparison.Ordinal), "应提示图片未确认");

        var pushYes = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "feat: 截图",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush,
            ImageConfirmed = true
        });
        E2EAssert.True(pushYes.Pushed, $"确认脱敏后应可推送：{pushYes.Error}");
    }

    // ---------- 9) Secret 内容 → 阻断提交 ----------
    [E2ETest]
    public static async Task T09_SecretBlocked()
    {
        var repo = await NewRepo("t09_secret");
        // 使用会被提交的普通 JSON 文件（.env 已被推荐 gitignore 排除，不构成风险）
        Write(repo, "appsettings.json", "{\n  \"Token\": \"" + "ghp_" + "abcdef1234567890XYZ99\"\n}");
        var ctx = await RunChecks(repo);
        var secretCheck = ctx.Report.Checks.First(c => c.Id == "secret_scan");
        E2EAssert.True(secretCheck.Status == CheckStatus.Blocked, "Token 应阻断");
        E2EAssert.True(!ctx.Report.CanCommit, "Token 阻断提交");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "bad",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Error != null && result.Error.Contains("Secret", StringComparison.Ordinal), "发布应被 Secret 门拦截");
        E2EAssert.True(!result.Committed, "阻断时不应产生提交");

        Write(repo, ".env", "API_KEY " + "= abcdef");
        var checkIgnore = await Git.GetIgnoredPathsAsync(repo, new[] { ".env" });
        E2EAssert.True(checkIgnore.Contains(".env"), "推荐 .gitignore 应已排除 .env");
    }

    // ---------- 10) 超大文件（>100MB）→ 阻断 ----------
    [E2ETest]
    public static async Task T10_LargeFileBlocked()
    {
        var repo = await NewRepo("t10_large");
        var big = Path.Combine(repo, "big.bin");
        using (var fs = new FileStream(big, FileMode.CreateNew, FileAccess.Write))
        {
            fs.SetLength(101L * 1024 * 1024);
        }
        var ctx = await RunChecks(repo);
        var large = ctx.Report.Checks.First(c => c.Id == "large_files");
        E2EAssert.True(large.Status == CheckStatus.Blocked, ">100MB 应阻断");
        E2EAssert.True(!ctx.Report.CanCommit, "超大文件阻断提交");
        File.Delete(big);
    }

    // ---------- 11) Build 失败 → 阻断发布 ----------
    [E2ETest]
    public static async Task T11_BuildFail_Blocked()
    {
        var repo = await NewRepo("t11_build");
        Write(repo, "Broken.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "Broken.cs", "class Broken { void M( { }");
        var ctx = await RunChecks(repo);
        E2EAssert.True(ctx.DotNetProject, "应识别为 .NET 项目");
        var build = ctx.Report.Checks.First(c => c.Id == "build");
        E2EAssert.True(build.Status == CheckStatus.Blocked, "build 失败应阻断发布");
    }

    // ---------- 12) 作者身份不匹配 → 警告；修复后通过 ----------
    [E2ETest]
    public static async Task T12_IdentityWarningThenFix()
    {
        var repo = await NewRepo("t12_identity", applyIdentity: false, generateGitignore: false);
        var badName = await Git.ConfigSetLocalAsync(repo, "user.name", "someone");
        var badEmail = await Git.ConfigSetLocalAsync(repo, "user.email", "x@localhost");
        E2EAssert.True(badName.Success && badEmail.Success, "写入错误身份失败");

        var ctx1 = await RunChecks(repo);
        var identity = ctx1.Report.Checks.First(c => c.Id == "git_identity");
        E2EAssert.True(identity.Status == CheckStatus.Warning, "身份不匹配应产生警告");

        var (ok, err) = await new GitIdentityService(Git).ApplyRecommendedAsync(repo, "VkDream", "312913839+VkDream@users.noreply.github.com");
        E2EAssert.True(ok, $"修复身份失败：{err}");

        var ctx2 = await RunChecks(repo);
        var identity2 = ctx2.Report.Checks.First(c => c.Id == "git_identity");
        E2EAssert.True(identity2.Status == CheckStatus.Pass, "修复后身份检查应通过");
    }

    // ---------- 13) 合并冲突 → 阻断 ----------
    [E2ETest]
    public static async Task T13_MergeConflict_Blocked()
    {
        var repo = await NewRepo("t13_conflict");
        Write(repo, "b.txt", "base\n");
        var add1 = await Git.AddAllAsync(repo);
        E2EAssert.True(add1.Success, "git add(base) 失败");
        var c1 = await Git.CommitAsync(repo, "feat: base");
        E2EAssert.True(c1.Success, "初始提交失败");

        var ckoutB = await Git.RunGitAsync(repo, new[] { "checkout", "-b", "feature" });
        E2EAssert.True(ckoutB.Success, "checkout -b 失败");
        Write(repo, "b.txt", "feature\n");
        var add2 = await Git.AddAllAsync(repo);
        E2EAssert.True(add2.Success, "git add(feature) 失败");
        var c2 = await Git.CommitAsync(repo, "feat: feature");
        E2EAssert.True(c2.Success, "feature 提交失败");

        var ckoutMain = await Git.RunGitAsync(repo, new[] { "checkout", "main" });
        E2EAssert.True(ckoutMain.Success, "checkout main 失败");
        Write(repo, "b.txt", "main\n");
        var add3 = await Git.AddAllAsync(repo);
        E2EAssert.True(add3.Success, "git add(main) 失败");
        var c3 = await Git.CommitAsync(repo, "feat: main");
        E2EAssert.True(c3.Success, "main 提交失败");

        var merge = await Git.RunGitAsync(repo, new[] { "merge", "feature" });
        E2EAssert.True(!merge.Success, "应产生合并冲突");

        var status = await Git.StatusPorcelainAsync(repo);
        E2EAssert.True(string.Join("\n", status.Stdout).Contains("UU b.txt", StringComparison.Ordinal), "应检测到 UU 冲突状态");

        var ctx = await RunChecks(repo);
        var statusCheck = ctx.Report.Checks.First(c => c.Id == "status");
        E2EAssert.True(statusCheck.Status == CheckStatus.Blocked, "冲突应阻断");
        E2EAssert.True(!ctx.Report.CanCommit, "冲突阻断提交");

        var result = await NewPublish().ExecuteAsync(new PublishWorkflowService.PublishRequest
        {
            RepositoryRoot = repo,
            CommitMessage = "bad",
            Mode = PublishWorkflowService.PublishMode.CommitAndPush
        });
        E2EAssert.True(result.Error != null && result.Error.Contains("冲突", StringComparison.Ordinal), "发布应被冲突拦截");
    }

    // ---------- 15) 自发布真实结构：根目录 .slnx + src/tests 子目录 csproj ----------
    [E2ETest]
    public static async Task T15_SelfPublishStructure_Slnx_BuildPass()
    {
        var repo = await NewRepo("t15_selfpublish");
        Write(repo, "MyApp.slnx",
            "<Solution>\n  <Project Path=\"src/MyApp/MyApp.csproj\" />\n  <Project Path=\"tests/MyApp.Tests/MyApp.Tests.csproj\" />\n</Solution>\n");
        Write(repo, "src/MyApp/MyApp.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "src/MyApp/Program.cs", "System.Console.WriteLine(\"hi\");");
        Write(repo, "tests/MyApp.Tests/MyApp.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var target = DotNetBuildService.FindBuildTarget(repo);
        E2EAssert.Equal(BuildTargetKind.Solution, target.Kind, "BUILD-ROOT-01：唯一 .slnx 应选中");
        E2EAssert.Equal("MyApp.slnx", target.FileName, "应选中 .slnx 而非子目录 csproj");

        var ctx = await RunChecks(repo);
        E2EAssert.True(ctx.DotNetProject, "应识别为 .NET 项目");
        var build = ctx.Report.Checks.First(c => c.Id == "build");
        E2EAssert.True(build.Status == CheckStatus.Pass, $"真实 .slnx 结构构建应通过（MSB1009 修复验证）：{build.Summary} {build.Details}");
        E2EAssert.Equal("MyApp.slnx", ctx.Build!.TargetDisplay, "Build Target 应展示为 .slnx 文件名");
        E2EAssert.True(ctx.Build!.CommandSummary.Contains("dotnet build MyApp.slnx", StringComparison.Ordinal), "命令摘要应含完整 build 目标");
        E2EAssert.True(ctx.Report.CanCommit, "构建通过且无敏感时允许提交");
    }

    // ---------- 16) 中文 + 空格路径 solution 构建 ----------
    [E2ETest]
    public static async Task T16_ChineseSpaceSlnx_BuildPass()
    {
        var repo = await NewRepo("t16_中文 构建 测试");
        Write(repo, "我的 应用.slnx",
            "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");
        Write(repo, "src/App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "src/App/Program.cs", "System.Console.WriteLine(\"ok\");");

        var target = DotNetBuildService.FindBuildTarget(repo);
        E2EAssert.Equal(BuildTargetKind.Solution, target.Kind, "BUILD-ROOT-06：中文+空格路径应解析");
        E2EAssert.True(File.Exists(target.Path!), "解析出的目标必须真实存在");

        var ctx = await RunChecks(repo);
        var build = ctx.Report.Checks.First(c => c.Id == "build");
        E2EAssert.True(build.Status == CheckStatus.Pass, $"中文+空格路径构建应通过：{build.Summary} {build.Details}");
    }

    // ---------- 17) 无 .NET 项目 → 跳过构建（BUILD-ROOT-05） ----------
    [E2ETest]
    public static async Task T17_NoDotNetProject_SkipBuild()
    {
        var repo = await NewRepo("t17_nodotnet");
        Write(repo, "README.md", "# 文档仓库");
        var ctx = await RunChecks(repo);
        E2EAssert.True(!ctx.DotNetProject, "无 .NET 项目应判定为非 .NET");
        var build = ctx.Report.Checks.FirstOrDefault(c => c.Id == "build");
        E2EAssert.True(build != null && build.Status == CheckStatus.Info, "非 .NET 项目应显示 Info（跳过构建），不得 Blocked/MSB1009");
        E2EAssert.True(ctx.Report.CanCommit, "非 .NET 项目允许提交");
    }

    // ---------- 18) 多 csproj 无主应用 → 需人工选择，不硬猜、不阻断提交 ----------
    [E2ETest]
    public static async Task T18_MultipleCsproj_Ambiguous_RequiresSelection()
    {
        var repo = await NewRepo("t18_multi");
        Write(repo, "src/A/A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "src/A/Program.cs", "System.Console.WriteLine(\"A\");");
        Write(repo, "src/B/B.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "src/B/Program.cs", "System.Console.WriteLine(\"B\");");

        var target = DotNetBuildService.FindBuildTarget(repo);
        E2EAssert.Equal(BuildTargetKind.Ambiguous, target.Kind, "BUILD-ROOT-04：多 csproj 且无主应用应歧义，不得硬猜");

        var ctx = await RunChecks(repo);
        var build = ctx.Report.Checks.First(c => c.Id == "build");
        E2EAssert.True(build.Status == CheckStatus.Warning, "歧义应为 Warning/需人工选择，不得 Build Failed 阻断");
        E2EAssert.True(build.Details.Contains("人工", StringComparison.Ordinal), "应提示需要人工确认构建目标");
        E2EAssert.True(ctx.Report.CanCommit, "歧义不阻断提交");
    }
}