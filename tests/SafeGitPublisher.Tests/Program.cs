using System.IO;
using System.Reflection;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// 零依赖控制台单测（无外部测试框架）。
/// 以 [Test] 标记的 public static void 方法作为一个测试用例。
/// </summary>
public static class Program
{
    public static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        var methods = typeof(Program).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
            .OrderBy(m => m.Name)
            .ToList();

        var pass = 0;
        var fail = 0;
        var failures = new List<string>();

        foreach (var m in methods)
        {
            try
            {
                m.Invoke(null, null);
                pass++;
                Console.WriteLine($"[PASS] {m.Name}");
            }
            catch (TargetInvocationException tie)
            {
                fail++;
                Console.WriteLine($"[FAIL] {m.Name} :: {Describe(tie.InnerException)}");
                failures.Add(m.Name);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"单测结果: {pass} 通过, {fail} 失败, 共 {pass + fail} 项, 断言 {Assert.Total} 次");
        foreach (var f in failures) Console.WriteLine("  失败项: " + f);

        return fail == 0 ? 0 : 1;
    }

    private static string Describe(Exception? ex)
    {
        if (ex == null) return "未知异常";
        var trace = ex.StackTrace;
        var line = trace?.Split('\n').FirstOrDefault(l => l.Contains("Program", StringComparison.OrdinalIgnoreCase))?.Trim() ?? string.Empty;
        return $"{ex.Message}  {line}";
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
}

internal static class TempDir
{
    public static string Create(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "SafePubUnitTests", $"{name}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }
}

/// <summary>最小断言库。</summary>
public static class Assert
{
    [ThreadStatic] private static int _count;
    public static int Total => _count;

    private static void Bump() => _count++;

    public static void True(bool condition, string? message = null)
    {
        _count++;
        if (!condition) throw new Exception("Assert.True 失败" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        _count++;
        if (!Equals(expected, actual))
        {
            throw new Exception($"Assert.Equal 失败：期望 <{expected}>，实际 <{actual}>" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
        }
    }

    public static void NotNull(object? value, string? message = null)
    {
        _count++;
        if (value == null) throw new Exception("Assert.NotNull 失败" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
    }

    public static void Null(object? value, string? message = null)
    {
        _count++;
        if (value != null) throw new Exception("Assert.Null 失败：期望为 null，实际 <" + value + ">" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        _count++;
        if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new Exception($"Assert.Contains 失败：未找到 <{needle}> 于 <{haystack}>" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
        }
    }

    public static void NotContains(string haystack, string needle, string? message = null)
    {
        _count++;
        if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            throw new Exception($"Assert.NotContains 失败：不应包含 <{needle}> 于 <{haystack}>" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
        }
    }

    public static void Empty(IEnumerable<object?> items, string? message = null)
    {
        _count++;
        if (items.Any()) throw new Exception("Assert.Empty 失败：集合应为空" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
    }

    public static void Count(int expected, IEnumerable<object?> items, string? message = null)
    {
        _count++;
        var n = items.Count();
        if (n != expected) throw new Exception($"Assert.Count 失败：期望 {expected}，实际 {n}" + (string.IsNullOrEmpty(message) ? string.Empty : "：" + message));
    }
}

/// <summary>SecretScanner 相关测试。</summary>
public static class SecretScannerTests
{
    private static readonly SecretScanner Scanner = new();

    [Test]
    public static void Token_GitHubClassic_Blocked()
    {
        var findings = Scanner.ScanContent("a.txt", "const token " + "= \"" + "ghp_" + "AbC1234567890XYZ9876" + "\";");
        var blocked = findings.First(f => f.Severity == ScanSeverity.Blocked);
        Assert.Count(1, findings.Where(f => f.Severity == ScanSeverity.Blocked));
        Assert.True(blocked.Message.Contains("ghp", StringComparison.OrdinalIgnoreCase), "应提到 token 类型");
        Assert.NotContains(blocked.Message, "ghp_" + "AbC1234567890", "消息不得泄露完整 Token");
        Assert.Equal("ghp_****9876", blocked.Preview ?? string.Empty);
    }

    [Test]
    public static void GitHubFineGrained_Blocked()
    {
        var findings = Scanner.ScanContent("cfg.json", "PAT: " + "github_pat_" + "11AAABCZxDyU1aBCefgh2_xxxxx");
        var blocked = findings.First(f => f.Severity == ScanSeverity.Blocked);
        Assert.Count(1, findings.Where(f => f.Severity == ScanSeverity.Blocked));
        Assert.Contains(blocked.Preview ?? string.Empty, "github_pat_", "应保留公开前缀");
        Assert.NotContains(blocked.Preview ?? string.Empty, "11AAABCZxDy", "主体必须脱敏");
    }

    [Test]
    public static void OpenAiKey_Blocked()
    {
        var findings = Scanner.ScanContent("app.py", "client = OpenAI(api_key" + "=\"" + "sk-" + "proj-1234abcdEFGH\")");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Blocked), "sk- 应阻断");
    }

    [Test]
    public static void AwsAccessKey_Blocked()
    {
        var findings = Scanner.ScanContent(".env", "AWS_ACCESS_KEY_ID=" + "AKIA" + "1234567890ABCDEF");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Blocked), "AKIA 应阻断");
    }

    [Test]
    public static void BearerToken_Blocked()
    {
        var findings = Scanner.ScanContent(".env", "Authorization: " + "Bearer " + "abcdefgh.0123456789.ABCDEF==");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Blocked), "Bearer 应阻断");
    }

    [Test]
    public static void PasswordAssignment_High()
    {
        var findings = Scanner.ScanContent("config.txt", "password " + "= s3cr3tPass1! ");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.High), "字面量密码应 High");
    }

    [Test]
    public static void ConnectionStringShortPassword_High()
    {
        var findings = Scanner.ScanContent("app.config", "Data Source=db;Password " + "=123;");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.High), "连接串短密码 Password 加 123 应 High");
        Assert.True(findings.Where(f => f.Preview != null).All(f => !f.Preview!.Contains("123;")), "Preview 不得包含完整值 123");
    }

    [Test]
    public static void FormIndexAccess_NotSecret()
    {
        var findings = Scanner.ScanContent("login.cs", "var password = form[\"password\"];");
        Assert.True(findings.All(f => f.Severity != ScanSeverity.High), "form[] 索引访问不应判为字面量凭据");
    }

    [Test]
    public static void MethodCallHash_NotSecret()
    {
        var findings = Scanner.ScanContent("util.cs", "var hash = HashPassword(password);");
        Assert.True(findings.All(f => f.Severity != ScanSeverity.High), "方法调用不应判为字面量凭据");
    }

    [Test]
    public static void PlaceholderPassword_NotHigh()
    {
        var findings = Scanner.ScanContent("x.txt", "password " + "= \"your password here\"");
        Assert.True(findings.All(f => f.Severity != ScanSeverity.High), "占位符值不应判为 High");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Info), "应保留关键字 Info 提示");
    }

    [Test]
    public static void KnownVarReference_NotHigh()
    {
        var findings = Scanner.ScanContent("x.txt", "Password = password;");
        Assert.True(findings.All(f => f.Severity != ScanSeverity.High), "裸变量引用不应判为字面量");
    }

    [Test]
    public static void PrivateIp_Warning()
    {
        var findings = Scanner.ScanContent("conf.txt", "Server" + "=" + "192.168." + "1.23;Database=app;");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Warning && f.RuleId == "private-ip"), "内网 IP 应 Warning");
        Assert.True(findings.Any(f => f.Severity == ScanSeverity.Warning && f.RuleId == "server-host"), "非本机 Server 应 Warning");
    }

    [Test]
    public static void LocalhostServer_NoWarning()
    {
        var findings = Scanner.ScanContent("conf.txt", "Server=localhost;Database=app;");
        Assert.True(findings.All(f => f.RuleId != "server-host"), "本机 Server 不应 Warning");
    }

    [Test]
    public static void Keyword_InfoOnly()
    {
        var findings = Scanner.ScanContent("notes.txt", "// TODO：清理 token 关键字用法");
        Assert.True(findings.All(f => f.Severity == ScanSeverity.Info), "仅关键字应为 Info");
    }

    [Test]
    public static void LineNumber_Reported()
    {
        var findings = Scanner.ScanContent("a.txt", "safe\nsafe\ntoken = abcdef1234");
        var high = findings.FirstOrDefault(f => f.Severity == ScanSeverity.High);
        Assert.NotNull(high, "应发现 High 级别凭据");
        Assert.Equal(3, high!.Line, "行号应为 3");
    }

    [Test]
    public static void RedactToken_Formats()
    {
        Assert.Equal("ghp_****9876", SecretScanner.RedactToken("ghp_" + "1234567890ABCD9876"));
        Assert.Equal("github_pat_****7890", SecretScanner.RedactToken("github_pat_" + "ABCDEFG1234567890"));
        Assert.Equal("sk-****abcd", SecretScanner.RedactToken("sk-" + "1234abcd"));
        Assert.Contains(SecretScanner.RedactToken("AKIA" + "1234567890ABCDEF"), "****");
    }

    [Test]
    public static void ScanFiles_CompleteRun()
    {
        var dir = TempDir.Create("scanfiles");
        try
        {
            File.WriteAllText(Path.Combine(dir, "ok.txt"), "just a normal file", Encoding.UTF8);
            File.WriteAllText(Path.Combine(dir, "bad.txt"), "key = " + "ghp_" + "1234567890ABCDEF999999");
            var result = Scanner.ScanFilesAsync(dir, new[] { "ok.txt", "bad.txt" }).GetAwaiter().GetResult();
            Assert.Count(1, result.Findings.Where(f => f.Severity == ScanSeverity.Blocked));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Test]
    public static void BinaryExtension_Skipped()
    {
        var dir = TempDir.Create("binary");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.png"), Encoding.UTF8.GetBytes("ghp_" + "1234567890ABCDEF99999"));
            var findings = Scanner.ScanFilesAsync(dir, new[] { "a.png" }).GetAwaiter().GetResult();
            Assert.Count(0, findings.Findings.Cast<object?>().ToList());
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}

/// <summary>敏感文件规则测试。</summary>
public static class SensitiveFileRuleTests
{
    [Test]
    public static void DirectoryNames_Blocked()
    {
        Assert.True(SensitiveFileRules.IsBlockedPath("bin/app.dll"));
        Assert.True(SensitiveFileRules.IsBlockedPath("src/obj/Debug/x"));
        Assert.True(SensitiveFileRules.IsBlockedPath("publish/output.exe"));
        Assert.True(SensitiveFileRules.IsBlockedPath("tmp/setup.tmp"));
        Assert.True(SensitiveFileRules.IsBlockedPath(".vs/settings"));
        Assert.True(SensitiveFileRules.IsBlockedPath(".claude/settings.json"));
    }

    [Test]
    public static void FileNames_Blocked()
    {
        Assert.True(SensitiveFileRules.IsBlockedPath("data.db"));
        Assert.True(SensitiveFileRules.IsBlockedPath("web/.env"));
        Assert.True(SensitiveFileRules.IsBlockedPath("secrets.json"));
        Assert.True(SensitiveFileRules.IsBlockedPath("appsettings.Local.json"));
        Assert.True(SensitiveFileRules.IsBlockedPath("cert.pfx"));
        Assert.True(SensitiveFileRules.IsBlockedPath("id_rsa.key"));
        Assert.True(SensitiveFileRules.IsBlockedPath("logs/app.log"));
    }

    [Test]
    public static void NormalFile_NotBlocked()
    {
        Assert.True(!SensitiveFileRules.IsBlockedPath("README.md"));
        Assert.True(!SensitiveFileRules.IsBlockedPath("src/Program.cs"));
        Assert.True(!SensitiveFileRules.IsBlockedPath("images/logo.png"));
    }

    [Test]
    public static void BlockReason_NonEmpty()
    {
        Assert.True(!string.IsNullOrEmpty(SensitiveFileRules.BlockReason("bin/x.dll")));
        Assert.True(!string.IsNullOrEmpty(SensitiveFileRules.BlockReason("a.db")));
    }
}

/// <summary>URL 解析测试。</summary>
public static class RemoteUrlTests
{
    [Test]
    public static void Https_Standard()
    {
        var (owner, repo, bad, _, _) = GitRemoteService.ParseUrl("https://github.com/MyOrg/repoName.git");
        Assert.Equal("MyOrg", owner);
        Assert.Equal("repoName", repo);
        Assert.True(!bad);
    }

    [Test]
    public static void Https_NoGitSuffix()
    {
        var (owner, repo, _, _, _) = GitRemoteService.ParseUrl("https://github.com/a/b");
        Assert.Equal("a", owner);
        Assert.Equal("b", repo);
    }

    [Test]
    public static void SshGit_At()
    {
        var (owner, repo, bad, _, _) = GitRemoteService.ParseUrl("git@github.com:user/repo.git");
        Assert.True(!bad);
        Assert.Equal("user", owner);
        Assert.Equal("repo", repo);
    }

    [Test]
    public static void SshSlashes()
    {
        var (owner, repo, _, _, _) = GitRemoteService.ParseUrl("ssh://git@github.com/u/r.git");
        Assert.Equal("u", owner);
        Assert.Equal("r", repo);
    }

    [Test]
    public static void Backslash_DetectedMalformed()
    {
        var (_, _, bad, reason, suggested) = GitRemoteService.ParseUrl("https\\://github.com/u/r.git");
        Assert.True(bad, "反斜杠 URL 应判为畸形");
        Assert.Contains(reason, "反斜杠");
        Assert.True(!string.IsNullOrEmpty(suggested), "应给出建议修复值");
        Assert.Equal("https://github.com/u/r.git", suggested);
    }

    [Test]
    public static void MissingDoubleSlash_Detected()
    {
        var (_, _, bad, _, _) = GitRemoteService.ParseUrl("https:github.com/u/r");
        Assert.True(bad, "缺少 // 应判为畸形");
    }

    [Test]
    public static void LocalPath_NotGithub()
    {
        var (owner, repo, bad, _, _) = GitRemoteService.ParseUrl(@"D:\myrepo");
        Assert.Null(owner);
        Assert.Null(repo);
        Assert.True(!bad);
    }

    [Test]
    public static void BuildOriginUrl_Probe()
    {
        Assert.Equal("https://github.com/o/r.git", GitRemoteService.BuildOriginUrl("o", "r"));
        Assert.True(GitRemoteService.IsGitHubUrl("https://github.com/o/r.git"));
        Assert.True(!GitRemoteService.IsGitHubUrl("D:\\myrepo"));
    }
}

/// <summary>porcelain 解析测试。</summary>
public static class PorcelainTests
{
    [Test]
    public static void Untracked()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "?? newfile.txt" });
        Assert.Count(1, l.Cast<object?>());
        Assert.Equal("??", l[0].StatusCode);
        Assert.Equal("未跟踪", l[0].StatusLabel);
        Assert.Equal("newfile.txt", l[0].Path);
        Assert.True(l[0].IsUntracked);
    }

    [Test]
    public static void WorktreeModified()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { " M src/file.cs" });
        Assert.Count(1, l.Cast<object?>());
        Assert.Equal("修改", l[0].StatusLabel);
        Assert.True(!l[0].IsStaged);
        Assert.Equal(RiskLevel.Normal, l[0].Risk);
    }

    [Test]
    public static void StagedAdd()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "A  added.cs" });
        Assert.Equal("新增", l[0].StatusLabel);
        Assert.True(l[0].IsStaged);
    }

    [Test]
    public static void Deleted()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "D  gone.cs" });
        Assert.Equal("删除", l[0].StatusLabel);
        Assert.True(l[0].IsDeleted);
    }

    [Test]
    public static void Renamed()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "R  old.txt -> new.txt" });
        Assert.Equal("重命名", l[0].StatusLabel);
        Assert.Equal("new.txt", l[0].Path);
        Assert.Equal("old.txt", l[0].OldPath);
    }

    [Test]
    public static void Conflict_Detected()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "UU conflict.txt" });
        Assert.True(l[0].IsConflict);
        Assert.Equal(RiskLevel.Blocked, l[0].Risk);
        Assert.Equal("冲突", l[0].StatusLabel);
    }

    [Test]
    public static void ConflictVaAndYou_Detected()
    {
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "AU a.txt", "DD b.txt", "UA c.txt", "AA d.txt" });
        Assert.True(l.All(c => c.IsConflict));
    }

    [Test]
    public static void Empty_Input()
    {
        Assert.Empty(GitRepositoryInspector.ParseStatusPorcelain(Array.Empty<string>()).Cast<object?>());
    }

    [Test]
    public static void QuotePath_Toggle_Off()
    {
        // 当 core.quotepath=false 时路径原样输出
        var l = GitRepositoryInspector.ParseStatusPorcelain(new[] { "?? 中文 路径.txt" });
        Assert.Equal("中文 路径.txt", l[0].Path);
    }
}

/// <summary>.gitignore 相关测试。</summary>
public static class GitIgnoreTests
{
    [Test]
    public static void Empty_AllMissing()
    {
        var missing = GitIgnoreService.ComputeMissingRules(string.Empty, GitIgnoreService.RequiredRules);
        Assert.Equal(GitIgnoreService.RequiredRules.Length, missing.Count);
    }

    [Test]
    public static void ExistingRule_NotDuplicated()
    {
        var existing = "[Bb]in/\n*.db\n";
        var missing = GitIgnoreService.ComputeMissingRules(existing, GitIgnoreService.RequiredRules);
        Assert.True(!missing.Contains("[Bb]in/"));
        Assert.True(!missing.Contains("*.db"));
        Assert.True(missing.Contains("[Oo]bj/"));
    }

    [Test]
    public static void CaseSensitive_Semantics()
    {
        // 精确匹配语义：已有 "bin/"（小写）不视为已存在 "[Bb]in/"（规则写法不同，视为缺失）
        var missing = GitIgnoreService.ComputeMissingRules("bin/\n", new[] { "[Bb]in/" });
        Assert.True(missing.Contains("[Bb]in/"));
    }

    [Test]
    public static void Merge_AppendOnly()
    {
        var existing = "# 我的规则\n*.tmp\n";
        var merged = GitIgnoreService.Merge(existing, new[] { "*.db", "[Bb]in/" });
        Assert.Contains(merged, "*.tmp", "原有内容必须保留");
        Assert.Contains(merged, "[Bb]in/", "缺失规则应追加");
        Assert.Contains(merged, "SafeGitPublisher");
    }

    [Test]
    public static void Merge_NoMissing_ReturnsSame()
    {
        var existing = "[Bb]in/\n[Oo]bj/\n*.db\n";
        var merged = GitIgnoreService.Merge(existing, new[] { "[Bb]in/", "[Oo]bj/" });
        Assert.Equal(existing, merged);
    }
}

/// <summary>大文件分级测试。</summary>
public static class LargeFileTests
{
    private static readonly LargeFileScanner Scanner = new(warningMB: 10, highWarningMB: 50, blockingMB: 100);

    [Test]
    public static void Small_Normal()
    {
        Assert.Equal(RiskLevel.Normal, Scanner.Classify(5 * 1024 * 1024).Risk);
    }

    [Test]
    public static void Medium_Warning()
    {
        Assert.Equal(RiskLevel.Warning, Scanner.Classify(15 * 1024 * 1024).Risk);
    }

    [Test]
    public static void High_Warning()
    {
        Assert.Equal(RiskLevel.Warning, Scanner.Classify(60 * 1024 * 1024).Risk);
    }

    [Test]
    public static void Huge_Blocked()
    {
        Assert.Equal(RiskLevel.Blocked, Scanner.Classify(200 * 1024 * 1024).Risk);
    }

    [Test]
    public static void Boundary_Blocking()
    {
        Assert.True(Scanner.Classify(101 * 1024 * 1024).Risk == RiskLevel.Blocked);
    }

    [Test]
    public static void SizeDisplay_Formats()
    {
        Assert.Equal("512 B", GitFileChange.FormatSize(512));
        Assert.Contains(GitFileChange.FormatSize(2048), "KB");
        Assert.Contains(GitFileChange.FormatSize(20 * 1024 * 1024), "MB");
    }
}

/// <summary>Git 身份信息测试。</summary>
public static class IdentityTests
{
    [Test]
    public static void Recommendation_Match()
    {
        var settings = new AppSettings();
        var info = new GitIdentityInfo
        {
            Name = "VkDream",
            Email = "312913839+VkDream@users.noreply.github.com",
            RecommendedName = settings.RecommendedGitName,
            RecommendedEmail = settings.RecommendedGitEmail
        };
        Assert.True(info.NameMatches);
        Assert.True(info.EmailMatches);
        Assert.True(!info.HasIssue);
    }

    [Test]
    public static void Mismatch_HasIssue()
    {
        var settings = new AppSettings();
        var info = new GitIdentityInfo
        {
            Name = "someone",
            Email = "x@localhost",
            RecommendedName = settings.RecommendedGitName,
            RecommendedEmail = settings.RecommendedGitEmail
        };
        Assert.True(!info.NameMatches);
        Assert.True(!info.EmailMatches);
        Assert.True(info.HasIssue);
    }
}

/// <summary>Build Target 解析器测试（BUILD-ROOT-01..06）。</summary>
public static class BuildTargetResolverTests
{
    private static string Write(string root, string relPath, string content = "x")
    {
        var full = Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Test]
    public static void BuildRoot01_UniqueSlnx_SelectsSlnx()
    {
        var dir = TempDir.Create("br01");
        try
        {
            Write(dir, "RepoX.slnx");
            Write(dir, "src/RepoX/RepoX.csproj");
            Write(dir, "tests/Tests/Tests.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Solution, t.Kind, "唯一 .slnx 应选择 solution");
            Assert.Equal("RepoX.slnx", t.FileName, "应选中 .slnx 而非 csproj");
            Assert.True(Path.GetFileName(t.Path!)!.Equals("RepoX.slnx", StringComparison.OrdinalIgnoreCase));
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot02_UniqueSln_SelectsSln()
    {
        var dir = TempDir.Create("br02");
        try
        {
            Write(dir, "Legacy.sln");
            Write(dir, "src/Legacy/Legacy.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Solution, t.Kind);
            Assert.Equal("Legacy.sln", t.FileName);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot03_NoSln_SingleCsprojInSubdir_SelectsProject()
    {
        var dir = TempDir.Create("br03");
        try
        {
            Write(dir, "src/App/App.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Project, t.Kind, "无 sln 且唯一 csproj 应选择项目");
            Assert.Equal("App.csproj", t.FileName);
            Assert.True(t.Path!.Contains("src" + Path.DirectorySeparatorChar + "App"), "应选中子目录 csproj，绝不假定根目录 RepoName.csproj");
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot04_MultipleCsproj_NoPrimary_Ambiguous()
    {
        var dir = TempDir.Create("br04");
        try
        {
            Write(dir, "src/A/A.csproj");
            Write(dir, "tests/B/B.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Ambiguous, t.Kind, "多 csproj 且无匹配主应用时应歧义，不得硬猜");
            Assert.NotNull(t.Reason);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot04b_MultipleCsproj_RepoNamePrimary_Selected()
    {
        var dir = TempDir.Create("br04b");
        try
        {
            var repoName = Path.GetFileName(dir);
            Write(dir, $"src/{repoName}/{repoName}.csproj");
            Write(dir, "tests/App.Tests/App.Tests.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Project, t.Kind, "与仓库名匹配的主应用应优先");
            Assert.Equal(repoName + ".csproj", t.FileName);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot04c_MultipleSln_RepoNameMatch_Selected()
    {
        var dir = TempDir.Create("br04c");
        try
        {
            var repoName = Path.GetFileName(dir);
            Write(dir, $"{repoName}.sln");
            Write(dir, "Other.slnx");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Solution, t.Kind, "多 solution 时名称与仓库名匹配者优先");
            Assert.Equal(repoName + ".sln", t.FileName);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot04d_MultipleSln_NoMatch_Ambiguous()
    {
        var dir = TempDir.Create("br04d");
        try
        {
            Write(dir, "One.sln");
            Write(dir, "Two.slnx");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Ambiguous, t.Kind);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot05_NoDotNetProject_None()
    {
        var dir = TempDir.Create("br05");
        try
        {
            Write(dir, "README.md");
            Write(dir, "docs/guide.txt");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.None, t.Kind, "无 .NET 项目应为 None（跳过构建，不报 MSB1009）");
            Assert.NotNull(t.Reason);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot05b_EmptyRepo_None()
    {
        var dir = TempDir.Create("br05b");
        try
        {
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.None, t.Kind);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildRoot06_ChineseSpacesPath_Resolves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "SafePub 中文 路径 测试");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        try
        {
            Write(dir, "我的 项目.slnx");
            Write(dir, "src/App/App.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Solution, t.Kind, "中文+空格路径应正常解析");
            Assert.True(File.Exists(t.Path!), "解析出的目标必须真实存在");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public static void IsDotNetProject_TrueForSlnAndProject_NoneForEmpty()
    {
        var dir = TempDir.Create("ispnet");
        try
        {
            Write(dir, "A.sln");
            Assert.True(DotNetBuildService.IsDotNetProject(dir), "有 sln 应视为 .NET 项目");
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Write(dir, "x/c.csproj");
            Assert.True(DotNetBuildService.IsDotNetProject(dir), "有 csproj 应视为 .NET 项目");
            Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Write(dir, "README.md");
            Assert.True(!DotNetBuildService.IsDotNetProject(dir), "无 .NET 项目不应视为 .NET 项目");
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Resolver_ExcludesBinObj_AndMaxDepth()
    {
        var dir = TempDir.Create("brx");
        try
        {
            Write(dir, "src/App/App.csproj");
            Write(dir, "src/App/bin/Debug/App.dll");
            Write(dir, "src/App/obj/Debug/App.csproj.GenerateMSBuildEditorConfigFile.EditorConfig.csproj");
            var t = BuildTargetResolver.Resolve(dir);
            Assert.Equal(BuildTargetKind.Project, t.Kind, "bin/obj 内容不应影响解析");
            Assert.Equal("App.csproj", t.FileName);
        }
        finally { TempDir.Delete(dir); }
    }
}

/// <summary>PreflightReport 决策逻辑测试。</summary>
public static class PreflightReportTests{
    private static PreflightCheck Make(string id, CheckStatus status, bool blocksCommit = false, bool blocksPush = false)
    {
        return new PreflightCheck { Id = id, Name = id, Status = status, BlocksCommit = blocksCommit, BlocksPush = blocksPush };
    }

    [Test]
    public static void AllPass_CanCommitAndPush()
    {
        var r = new PreflightReport(new[] { Make("a", CheckStatus.Pass, true, true), Make("b", CheckStatus.Pass) });
        Assert.True(r.CanCommit);
        Assert.True(r.CanPush);
    }

    [Test]
    public static void CommitBlock_BlocksBoth()
    {
        var r = new PreflightReport(new[] { Make("secrets", CheckStatus.Blocked, blocksCommit: true, blocksPush: true) });
        Assert.True(!r.CanCommit);
        Assert.True(!r.CanPush);
    }

    [Test]
    public static void PushOnlyBlock_StillCanCommit()
    {
        var r = new PreflightReport(new[] { Make("image", CheckStatus.Blocked, blocksCommit: false, blocksPush: true) });
        Assert.True(r.CanCommit, "仅阻断 Push 时仍可仅提交");
        Assert.True(!r.CanPush);
    }

    [Test]
    public static void Warning_AllowsCommit()
    {
        var r = new PreflightReport(new[] { Make("w", CheckStatus.Warning) });
        Assert.True(r.CanCommit);
        Assert.True(r.CanPush);
        Assert.True(r.HasWarning);
    }
}