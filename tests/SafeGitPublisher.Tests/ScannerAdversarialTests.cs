using System.IO;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>Secret、大文件和构建目标扫描边界的对抗性纯单元测试。</summary>
public static class ScannerAdversarialTests
{
    /// <summary>
    /// 运行时生成符合 GitHub classic token 规则的测试值；源码中不保留完整凭据形态，
    /// 避免安全扫描器对项目自身的对抗测试样本产生发布阻断。
    /// </summary>
    private static readonly string GitHubSample = string.Concat("gh", "p_", new string('A', 20));

    [Test]
    public static void Secret_ExtensionlessDockerfile_IsScanned()
    {
        var dir = TempDir.Create("secret_dockerfile");
        try
        {
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "value=" + GitHubSample, new UTF8Encoding(false));

            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "Dockerfile" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.ScannedCount, "无扩展名 Dockerfile 必须实际扫描");
            Assert.Equal(0, result.ErrorCount);
            Assert.True(result.Findings.Any(x => x.RuleId == "ghp" && x.Severity == ScanSeverity.Blocked), "Dockerfile 中的 Token 必须按 GitHub Token 规则阻断");
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_Utf16Bom_IsScanned()
    {
        var dir = TempDir.Create("secret_utf16");
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.txt"), "密码说明\r\nvalue=" + GitHubSample, Encoding.Unicode);

            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "config.txt" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.ScannedCount, "UTF-16 BOM 文本不得因 NUL 字节被当成二进制");
            Assert.Equal(0, result.SkippedCount);
            Assert.True(result.Findings.Any(x => x.RuleId == "ghp" && x.Severity == ScanSeverity.Blocked && x.Line == 2));
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_Gb18030_IsScanned()
    {
        var dir = TempDir.Create("secret_gb18030");
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var gb18030 = Encoding.GetEncoding("GB18030");
            File.WriteAllBytes(Path.Combine(dir, "配置.txt"), gb18030.GetBytes("中文配置\r\nvalue=" + GitHubSample));

            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "配置.txt" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.ScannedCount, "GB18030 文本必须回退解码并完成扫描");
            Assert.Equal(0, result.ErrorCount);
            Assert.True(result.Findings.Any(x => x.RuleId == "ghp" && x.Severity == ScanSeverity.Blocked));
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_OverTwoMiB_TokenNearEnd_IsScanned()
    {
        var dir = TempDir.Create("secret_large_text");
        try
        {
            var path = Path.Combine(dir, "large.txt");
            using (var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)))
            {
                var line = new string('a', 1023);
                for (var i = 0; i < 2300; i++) writer.WriteLine(line);
                writer.WriteLine("value=" + GitHubSample);
            }
            Assert.True(new FileInfo(path).Length > 2L * 1024 * 1024, "测试前提：文本必须超过旧 2 MiB 上限");

            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "large.txt" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.ScannedCount, "超过 2 MiB 的文本不能静默跳过");
            Assert.Equal(0, result.ErrorCount);
            Assert.True(result.Findings.Any(x => x.RuleId == "ghp" && x.Severity == ScanSeverity.Blocked), "文件尾部 Token 必须按 GitHub Token 规则命中");
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_MissingTarget_IsRecordedAndBlocked()
    {
        var dir = TempDir.Create("secret_missing");
        try
        {
            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "vanished.txt" }).GetAwaiter().GetResult();

            Assert.Equal(0, result.ScannedCount);
            Assert.Equal(1, result.ErrorCount, "读取失败/竞态消失必须进入错误统计");
            Assert.True(!result.IsComplete);
            Assert.True(result.Findings.Any(x => x.RuleId == "secret-scan-incomplete" && x.Severity == ScanSeverity.Blocked));
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_BinaryContent_IsRecordedAsSafeSkip()
    {
        var dir = TempDir.Create("secret_binary_content");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "unknown.dat"), new byte[] { 1, 2, 0, 4, 5 });

            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "unknown.dat" }).GetAwaiter().GetResult();

            Assert.Equal(0, result.ScannedCount);
            Assert.Equal(1, result.SkippedCount, "内容确认为二进制时允许安全跳过，但必须有记录");
            Assert.Equal(0, result.ErrorCount);
            Assert.True(result.IsComplete);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void Secret_PathEscape_IsRecordedAndBlocked()
    {
        var dir = TempDir.Create("secret_escape");
        try
        {
            var result = new SecretScanner().ScanFilesAsync(dir, new[] { "../outside.txt" }).GetAwaiter().GetResult();

            Assert.Equal(1, result.ErrorCount);
            Assert.True(result.HasBlocked, "仓库外路径不得被当作正常扫描目标");
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void LargeFile_ConfigCannotRaiseGitHubHardLimit()
    {
        var scanner = new LargeFileScanner(warningMB: 200, highWarningMB: 300, blockingMB: 500);

        var result = scanner.Classify(101L * 1024 * 1024);

        Assert.Equal(RiskLevel.Blocked, result.Risk, "配置阈值高于 100 MiB 时仍必须执行 GitHub 硬门");
        Assert.Contains(result.Description, "100 MB");
    }

    [Test]
    public static void LargeFile_MissingTarget_IsBlocked()
    {
        var dir = TempDir.Create("large_missing");
        try
        {
            var change = NewChange("vanished.bin");

            var findings = new LargeFileScanner().Scan(dir, new[] { change });

            Assert.True(findings.Any(x => x.RuleId == "large-file-scan-incomplete" && x.Severity == ScanSeverity.Blocked));
            Assert.Equal(RiskLevel.Blocked, change.Risk);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void LargeFile_PathEscape_IsBlocked()
    {
        var dir = TempDir.Create("large_escape");
        try
        {
            var change = NewChange("../outside.bin");

            var findings = new LargeFileScanner().Scan(dir, new[] { change });

            Assert.True(findings.Any(x => x.Severity == ScanSeverity.Blocked));
            Assert.Equal(RiskLevel.Blocked, change.Risk);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void DeletedLike_RecognizesBothPorcelainColumns_ButNotConflicts()
    {
        Assert.True(NewChange("gone-a.txt", "D ").IsDeletedLike(), "暂存区删除 D空格 必须识别");
        Assert.True(NewChange("gone-b.txt", " D").IsDeletedLike(), "工作区删除 空格D 必须识别");
        Assert.True(NewChange("gone-c.txt", "D").IsDeletedLike(), "diff --cached 单字符 D 必须识别");
        Assert.True(!NewChange("conflict-dd.txt", "DD").IsDeletedLike(), "DD 冲突不得当普通删除跳过");
        Assert.True(!NewChange("conflict-du.txt", "DU").IsDeletedLike(), "DU 冲突不得当普通删除跳过");
        Assert.True(!NewChange("conflict-ud.txt", "UD").IsDeletedLike(), "UD 冲突不得当普通删除跳过");
        Assert.True(!NewChange("modified.txt", " M").IsDeletedLike(), "普通修改不得误判删除");
    }

    [Test]
    public static void LargeFile_WorktreeDeletion_DoesNotBlockForMissingFile()
    {
        var dir = TempDir.Create("large_deleted");
        try
        {
            var change = NewChange("removed.txt", " D");

            var findings = new LargeFileScanner().Scan(dir, new[] { change });

            Assert.Equal(0, findings.Count, "正常工作树删除不应因磁盘文件已不存在而触发扫描不完整");
            Assert.Equal(RiskLevel.Normal, change.Risk);
        }
        finally { TempDir.Delete(dir); }
    }

    [Test]
    public static void BuildTarget_SerenaProjectsAreExcluded()
    {
        var dir = TempDir.Create("build_serena");
        try
        {
            WriteFile(dir, "src/App/App.csproj");
            WriteFile(dir, ".serena/cache/Generated.csproj");

            var result = BuildTargetResolver.Resolve(dir);

            Assert.Equal(BuildTargetKind.Project, result.Kind, ".serena 内的工具缓存不得制造构建目标歧义");
            Assert.Equal("App.csproj", result.FileName);
        }
        finally { TempDir.Delete(dir); }
    }

    private static void WriteFile(string root, string relativePath)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "<Project />", new UTF8Encoding(false));
    }

    private static GitFileChange NewChange(string path, string statusCode = "??") => new()
    {
        StatusCode = statusCode,
        StatusLabel = "未跟踪",
        Path = path
    };
}
