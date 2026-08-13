using System.IO;
using System.Text;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Tests;

/// <summary>发布最终门禁的源码级回归合同；这些测试不启动 Git 进程。</summary>
public static class SecurityWorkflowContractTests
{
    private static string ReadSource(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(SecurityWorkflowContractTests).Assembly.Location) ?? string.Empty,
            "..", "..", "..", "..", ".."));
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()), Encoding.UTF8);
    }

    [Test]
    public static void Workflow_BindsScannedIndexTreeToCommittedTree()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        var scan = source.IndexOf("var stagedScan = await ScanIndexAsync", StringComparison.Ordinal);
        var lockTree = source.IndexOf("var scannedTreeResult = await _git.WriteIndexTreeAsync", StringComparison.Ordinal);
        var commit = source.IndexOf("var commit = await _git.CommitAsync", StringComparison.Ordinal);
        var committedTree = source.IndexOf("var committedTreeResult = await _git.HeadTreeOidResultAsync", StringComparison.Ordinal);
        var compare = source.IndexOf("string.Equals(scannedTreeOid, committedTreeOid", StringComparison.Ordinal);

        Assert.True(scan >= 0 && scan < lockTree, "必须先扫描 index 再锁定 tree");
        Assert.True(lockTree < commit && commit < committedTree, "必须在 commit 前锁定 tree，并在 commit 后读取实际 tree");
        Assert.True(committedTree < compare, "必须比较已扫描 tree 与实际 commit tree");
    }

    [Test]
    public static void Workflow_DoesNotTrustBinaryFileExtensions_AndCleanupIsAGate()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PublishWorkflowService.cs");
        Assert.NotContains(source, "SecretScanner.IsKnownBinaryPath(", "最终 blob Gate 不得按扩展名跳过内容探测");
        Assert.Contains(source, "private static bool TryDeleteBlobTempFile", "临时 blob 清理必须返回可验证结果");
        Assert.Contains(source, "if (!TryDeleteBlobTempFile(temporaryPath))", "清理失败必须进入失败关闭分支");
    }

    [Test]
    public static void ConfirmData_ImageConfirmationCanBeCompletedInFinalDialog()
    {
        var data = new ConfirmPublishData
        {
            RepositoryRoot = @"C:\repo",
            ProjectPath = @"C:\repo",
            ChangeCount = 1,
            HasNewImages = true,
            RequiresImageConfirmation = true,
            ImageConfirmed = false
        };

        Assert.Contains(data.ImageConfirmedText, "禁止 Push");
        data.ImageConfirmed = true;
        Assert.Equal("已确认脱敏", data.ImageConfirmedText);
    }

    [Test]
    public static void Preflight_HighSecretUsesHardGateConsistentWithWorkflow()
    {
        var source = ReadSource("src", "SafeGitPublisher", "Services", "PreflightService.cs");
        var start = source.IndexOf("else if (highSecrets.Count > 0)", StringComparison.Ordinal);
        var end = source.IndexOf("else if (warnSecrets.Count > 0)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "应找到 High Secret 预检分支");
        var branch = source[start..end];
        Assert.Contains(branch, "CheckStatus.Blocked");
        Assert.Contains(branch, "blocksCommit: true, blocksPush: true");
    }
}
