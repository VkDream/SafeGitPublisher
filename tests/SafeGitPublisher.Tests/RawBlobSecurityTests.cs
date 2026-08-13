using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>原始 Git blob 解码、完整性与无扩展名最终门测试；不启动 Git。</summary>
public static class RawBlobSecurityTests
{
    /// <summary>
    /// 运行时生成符合 GitHub classic token 规则的测试值；源码中不保留完整凭据形态，
    /// 避免 SafeGitPublisher 对自身仓库执行预检时把测试种子当成真实 Secret。
    /// </summary>
    private static string CreateGitHubToken(char fill) => string.Concat("gh", "p_", new string(fill, 20));

    /// <summary>返回当前测试源码路径，用于验证安全夹具不会反向阻断项目自身预检。</summary>
    private static string CurrentSourcePath([CallerFilePath] string path = "") => path;

    [Test]
    public static void SecurityFixturesAndReleaseNotes_DoNotWarnRepositorySelfScan()
    {
        var testsDirectory = Path.GetDirectoryName(CurrentSourcePath())!;
        var repositoryRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));
        var paths = new[]
        {
            "tests/SafeGitPublisher.Tests/RawBlobSecurityTests.cs",
            "tests/SafeGitPublisher.Tests/ScannerAdversarialTests.cs",
            "CHANGELOG.md"
        };

        var result = new SecretScanner().ScanFilesAsync(repositoryRoot, paths).GetAwaiter().GetResult();
        var reviewFindings = result.Findings
            .Where(f => f.Severity is ScanSeverity.Warning or ScanSeverity.High or ScanSeverity.Blocked)
            .ToList();

        Assert.True(result.IsComplete, "安全测试源码与发布说明必须被完整扫描");
        Assert.Equal(0, reviewFindings.Count,
            "安全测试夹具与发布说明不得反向触发项目自身复核：" + string.Join("；", reviewFindings.Select(f => $"{f.File}:{f.Line} {f.RuleId}")));
    }

    [Test]
    public static void RawBlob_Utf8BomToken_IsBlockedAndComplete()
    {
        var directory = TempDir.Create("rawblob_utf8bom");
        try
        {
            var path = Path.Combine(directory, "blob.tmp");
            var payloadText = "value=" + CreateGitHubToken('B') + "\n";
            var payload = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(payloadText)).ToArray();
            File.WriteAllBytes(path, payload);
            var result = new SecretScanner().ScanRawBlobFileAsync(path, "Dockerfile").GetAwaiter().GetResult();
            Assert.True(result.IsComplete);
            Assert.True(result.Findings.Any(f => f.RuleId == "ghp" && f.Severity == ScanSeverity.Blocked));
            Assert.Equal(SecretScanner.ScanFileDisposition.Scanned, result.FileOutcomes.Single().Disposition);
        }
        finally { TempDir.Delete(directory); }
    }

    [Test]
    public static void RawBlob_Gb18030Token_IsBlockedAndComplete()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var directory = TempDir.Create("rawblob_gb18030");
        try
        {
            var path = Path.Combine(directory, "blob.tmp");
            var payloadText = "中文\nvalue=" + CreateGitHubToken('C') + "\n";
            File.WriteAllBytes(path, Encoding.GetEncoding("GB18030").GetBytes(payloadText));
            var result = new SecretScanner().ScanRawBlobFileAsync(path, "Makefile").GetAwaiter().GetResult();
            Assert.True(result.IsComplete);
            Assert.True(result.Findings.Any(f => f.RuleId == "ghp" && f.Severity == ScanSeverity.Blocked));
        }
        finally { TempDir.Delete(directory); }
    }

    [Test]
    public static void RawBlob_NulContent_IsSafelyClassifiedBinary()
    {
        var directory = TempDir.Create("rawblob_binary");
        try
        {
            var path = Path.Combine(directory, "blob.tmp");
            File.WriteAllBytes(path, new byte[] { 0x01, 0x00, 0x02, 0x03 });
            var result = new SecretScanner().ScanRawBlobFileAsync(path, "Jenkinsfile").GetAwaiter().GetResult();
            Assert.True(result.IsComplete);
            Assert.Equal(SecretScanner.ScanFileDisposition.SkippedBinary, result.FileOutcomes.Single().Disposition);
        }
        finally { TempDir.Delete(directory); }
    }

    [Test]
    public static void ExtensionlessPaths_AreNotPreSkipped()
    {
        Assert.True(!SecretScanner.IsKnownBinaryPath("Dockerfile"));
        Assert.True(!SecretScanner.IsKnownBinaryPath("Makefile"));
        Assert.True(!SecretScanner.IsKnownBinaryPath("Jenkinsfile"));
        Assert.True(SecretScanner.IsKnownBinaryPath("assets/logo.png"));
    }
}

/// <summary>SCP-like Remote 用户与显示脱敏测试。</summary>
public static class RemoteCredentialSafetyTests
{
    [Test]
    public static void ScpLike_OnlyGitUserAllowed()
    {
        var (_, _, gitBad, _, _) = GitRemoteService.ParseUrl("git@github.com:owner/repo.git");
        var (_, _, tokenBad, _, _) = GitRemoteService.ParseUrl("secret-token@github.com:owner/repo.git");
        Assert.True(!gitBad);
        Assert.True(tokenBad);
    }

    [Test]
    public static void ScpLike_Display_RedactsNonGitUser()
    {
        const string secretUser = "secret-token";
        var display = GitRemoteService.RedactForDisplay(secretUser + "@github.com:owner/repo.git");
        Assert.True(!display.Contains(secretUser, StringComparison.Ordinal));
        Assert.Contains(display, "@github.com:owner/repo.git");
        Assert.Equal("git@github.com:owner/repo.git", GitRemoteService.RedactForDisplay("git@github.com:owner/repo.git"));
    }

    [Test]
    public static void CommandOutput_RedactsUriAndScpCredentials()
    {
        const string credentialSample = "private-value";
        var uriOutput = GitRemoteService.RedactOutput("fatal: unable to access 'https://user:" + credentialSample + "@github.com/owner/repo.git'");
        var scpOutput = GitRemoteService.RedactOutput("fatal: " + credentialSample + "@github.com:owner/repo.git denied");

        Assert.NotContains(uriOutput, credentialSample);
        Assert.Contains(uriOutput, "https://***@github.com/owner/repo.git");
        Assert.NotContains(scpOutput, credentialSample);
        Assert.Contains(scpOutput, "***@github.com:owner/repo.git");
        Assert.Equal("git@github.com:owner/repo.git", GitRemoteService.RedactOutput("git@github.com:owner/repo.git"));
    }
}
