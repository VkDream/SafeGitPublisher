using System.IO;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.E2E;

/// <summary>
/// Self-Build 隔离输出与 .serena 元数据门禁端到端场景（真实 dotnet CLI + %TEMP% 临时仓库）。
/// SELFBUILD-01：运行/锁定自身输出 EXE 时，隔离构建必须 PASS；
/// SELFBUILD-05/06：源码树零污染 + 临时目录清理（并入 S01）；
/// SELFBUILD-02/03/04 由 T11/T15/T16 增强版覆盖（隔离模式下真实执行）。
/// SERENA-03：.serena/ 被忽略后不进入变更、不 Sensitive 阻断、不 Secret 扫描。
/// </summary>
public static class SelfBuildScenarios
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

    private static PreflightService NewPreflight() => new(
        Git,
        new SensitiveFileScanner(Git),
        new SecretScanner(),
        new LargeFileScanner(),
        new DotNetBuildService(Runner));

    private static DotNetBuildService NewBuildService() => new(Runner);

    private static async Task<string> RunDotNetAsync(string workDir, params string[] args)
    {
        var output = new List<string>();
        var r = await Runner.RunAsync(new ProcessRequest
        {
            FileName = "dotnet",
            Arguments = args.ToList(),
            WorkingDirectory = workDir,
            Timeout = TimeSpan.FromMinutes(10),
            Utf8Output = true,
            OnStdoutLine = line => { lock (output) output.Add(line); },
            OnStderrLine = line => { lock (output) output.Add(line); }
        }, CancellationToken.None);
        return string.Join("\n", output);
    }

    private static List<string> SnapshotTree(string repoRoot)
    {
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            if (f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(Path.GetRelativePath(repoRoot, f).Replace('\\', '/'));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    // ---------- SELFBUILD-01（+05 无污染 / +06 清理）：锁定自身 EXE 时隔离构建必须 PASS ----------
    // 自动化复现现场：传统 build 输出 EXE 被锁定（等价 SafeGitPublisher.exe 运行中）→ MSB3027 失败；
    // 相同锁存在时，DotNetBuildService 隔离构建（--artifacts-path 到 %TEMP%）→ PASS，且原 bin/obj 零变化。
    [E2ETest]
    public static async Task S01_SelfBuild_RunningOutputLocked_IsolatedPass()
    {
        var repo = await NewRepo("s01_selfbuild");
        Write(repo, "App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(repo, "App/Program.cs", "System.Console.WriteLine(\"self\");");

        // 1) 传统构建一次，建立正式 bin/obj 输出
        var csproj = Path.Combine(repo, "App", "App.csproj");
        var first = await RunDotNetAsync(repo, "build", csproj, "--nologo", "-v:m");
        E2EAssert.True(first.Contains("0 个错误") || first.Contains("Build succeeded") || first.Contains("已成功生成"),
            $"传统首次构建应成功：{first}");

        var exe = Path.Combine(repo, "App", "bin", "Debug", "net10.0", "App.exe");
        E2EAssert.True(File.Exists(exe), "传统构建应产出 App.exe");

        // 2) 放置哨兵（隔离构建不得触碰）
        var sentinel = Path.Combine(repo, "App", "bin", "Debug", "net10.0", "sentinel.txt");
        File.WriteAllText(sentinel, "keep-me");

        // 3) 锁定 App.exe（等价：SafeGitPublisher.exe 正在运行）
        var lockStream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.None);

        // 4) 自动化复现现场：锁存在时传统 build（强制重建）→ 必须失败且出现 MSB3027/MSB3021
        var retry = await RunDotNetAsync(repo, "build", csproj, "--no-incremental", "--nologo", "-v:m");
        E2EAssert.True(retry.Contains("MSB3027") || retry.Contains("MSB3021"),
            $"传统 build 在输出锁定时应产生 MSB3027/MSB3021（现场复现证据）：\n{retry}");

        // 5) 基线快照（传统失败构建可能残留的 obj 缓存也计入基线；隔离构建不得再新增任何文件）
        var beforeTree = SnapshotTree(repo);

        // 6) 锁存在时执行隔离构建（本轮核心）：必须 PASS
        var ctx = await NewBuildService().BuildRepositoryAsync(repo, skipBuild: false);
        E2EAssert.True(ctx.BuildRun, "隔离构建必须执行（BuildRun=true）");
        E2EAssert.True(ctx.Succeeded, $"隔离构建必须 PASS：ExitCode={ctx.ExitCode}\n{string.Join("\n", ctx.ErrorLines)}");
        E2EAssert.Equal(0, ctx.ExitCode, "ExitCode 必须为 0");
        E2EAssert.Equal("Isolated Temporary Output", ctx.BuildMode, "Build Mode 必须为 Isolated Temporary Output");
        E2EAssert.True(!string.IsNullOrEmpty(ctx.IsolationRoot), "IsolationRoot 必须记录");
        E2EAssert.True(!ctx.CleanupFailed, "临时目录清理不应失败");
        E2EAssert.True(ctx.CommandSummary.Contains("隔离输出"), $"CommandSummary 应体现隔离输出：{ctx.CommandSummary}");

        // 7) 隔离目录生命周期：build 完成后应被清理（SELFBUILD-06）
        E2EAssert.True(!Directory.Exists(ctx.IsolationRoot!), $"隔离构建临时目录应已清理：{ctx.IsolationRoot}");

        // 8) 源码树零污染（SELFBUILD-05）：bin/obj 快照与哨兵不变
        var afterTree = SnapshotTree(repo);
        E2EAssert.True(beforeTree.All(afterTree.Contains), "隔离构建不得向源码树新增任何文件（bin/obj 零变化）");
        E2EAssert.True(File.Exists(sentinel), "原输出哨兵文件必须保留");
        E2EAssert.Equal("keep-me", File.ReadAllText(sentinel), "原输出哨兵内容不得被覆盖");

        // 9) 锁定中的原 EXE 未被替换
        E2EAssert.True(File.Exists(exe), "原锁定 EXE 必须原样存在");

        lockStream.Dispose();
    }

    // ---------- SERENA-03：.serena/ 被忽略 → 不进入变更 / 不 Sensitive 阻断 / 不 Secret 扫描 ----------
    [E2ETest]
    public static async Task S02_SerenaIgnored_NotInChanges_NotBlocked()
    {
        var repo = await NewRepo("s02_serena");
        // 初始提交（把 .gitignore 等基线入库，保证后续只出现被测变更）
        var add = await Git.AddAllAsync(repo);
        E2EAssert.True(add.Success, $"git add 失败：{add.StdErrText}");
        var commit = await Git.CommitAsync(repo, "chore: init");
        E2EAssert.True(commit.Success, $"git commit 失败：{commit.StdErrText}");

        Write(repo, ".serena/project.yml", "project: demo\n");
        Write(repo, ".serena/.gitignore", "node_modules/\n");
        // 种子运行时拼接：文件文本虽含完整模式，但被 ignore 后不进入扫描候选
        Write(repo, ".serena/secrets.md", "token = " + "ghp_" + "AbC1234567890XYZ9876\n");
        Write(repo, "readme.md", "ok\n");

        // .gitignore 推荐规则含 .serena/（NewRepo 已应用）
        var ctx = await NewPreflight().RunAsync(repo, new AppSettings(), log: null, imageConfirmed: true);

        var serenaInChanges = ctx.Changes.Any(c =>
            c.Path.StartsWith(".serena/", StringComparison.OrdinalIgnoreCase));
        E2EAssert.True(!serenaInChanges, ".serena/ 文件不得进入本次变更");

        var buildCheck = ctx.Report.Checks.FirstOrDefault(c => c.Id == "build");
        E2EAssert.True(buildCheck == null || buildCheck.Status != CheckStatus.Blocked,
            "非 .NET 项目不应有 build 阻断");

        var sensitiveFindings = ctx.Report.Checks.FirstOrDefault(c => c.Id == "sensitive_file");
        E2EAssert.True(sensitiveFindings == null || sensitiveFindings.Status != CheckStatus.Blocked,
            "被忽略的 .serena/ 不得产生 Sensitive 阻断");

        var secretFindings = ctx.Report.Checks.FirstOrDefault(c => c.Id == "secret_scan");
        E2EAssert.True(secretFindings == null || secretFindings.Status != CheckStatus.Blocked,
            "被忽略的 .serena/ 不得进入 Secret 扫描阻断");

        // 提交门控：readme.md 是唯一可提交变更，应可提交
        E2EAssert.Equal(1, ctx.Changes.Count(c => !c.IsConflict), "仅 readme.md 应作为可提交变更");
    }
}
