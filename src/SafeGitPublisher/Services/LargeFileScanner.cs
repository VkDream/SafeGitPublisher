using System.IO;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 大文件扫描器（纯规则可单测）。
/// 阈值：&gt; warningMB → Warning；&gt; highWarningMB → 高危 Warning；&gt; blockingMB → Blocked。
/// blockingMB 默认 100（GitHub 单文件硬限制）。
/// </summary>
public sealed class LargeFileScanner
{
    private readonly double _warningMB;
    private readonly double _highWarningMB;
    private readonly double _blockingMB;

    public LargeFileScanner(double warningMB = 10, double highWarningMB = 50, double blockingMB = 100)
    {
        _warningMB = warningMB;
        _highWarningMB = highWarningMB;
        _blockingMB = blockingMB;
    }

    /// <summary>根据字节数判断风险级别（纯逻辑，供测试）。</summary>
    public (RiskLevel Risk, string Description) Classify(long sizeBytes)
    {
        var mb = sizeBytes / (1024.0 * 1024.0);
        if (mb > _blockingMB)
        {
            return (RiskLevel.Blocked, $"文件大小为 {mb:F1} MB，超过 GitHub 100MB 限制（{_blockingMB:F0} MB），推送会被 GitHub 拒绝。");
        }
        if (mb > _highWarningMB)
        {
            return (RiskLevel.Warning, $"文件大小为 {mb:F1} MB，超过高危阈值 {_highWarningMB:F0} MB，请确认是否需要 Git LFS 或拆分。");
        }
        if (mb > _warningMB)
        {
            return (RiskLevel.Warning, $"文件大小为 {mb:F1} MB，超过警告阈值 {_warningMB:F0} MB，请注意仓库体积。");
        }
        return (RiskLevel.Normal, $"{GitFileChange.FormatSize(sizeBytes)}");
    }

    /// <summary>
    /// 扫描一批变更文件中新增/修改且存在于磁盘的文件。
    /// </summary>
    public List<ScanFinding> Scan(string repoRoot, IEnumerable<GitFileChange> changes)
    {
        var findings = new List<ScanFinding>();
        foreach (var change in changes)
        {
            if (change.IsDeletedLike()) continue;

            var full = Path.Combine(repoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;

            long size;
            try
            {
                size = new FileInfo(full).Length;
            }
            catch
            {
                continue;
            }
            change.SizeBytes = size;
            change.Risk = Classify(size).Risk;

            var (risk, desc) = Classify(size);
            if (risk != RiskLevel.Normal)
            {
                findings.Add(new ScanFinding(change.Path, "large-file", ToSeverity(risk), desc));
            }
        }
        return findings;
    }

    /// <summary>补充文件大小字段（不产生阻断），用于列表显示。</summary>
    public void PopulateSizes(string repoRoot, IEnumerable<GitFileChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.IsDeleted() || change.SizeBytes >= 0) continue;
            var full = Path.Combine(repoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try { change.SizeBytes = new FileInfo(full).Length; } catch { }
            }
        }
    }

    private static ScanSeverity ToSeverity(RiskLevel risk) => risk switch
    {
        RiskLevel.Blocked => ScanSeverity.Blocked,
        _ => ScanSeverity.Warning
    };
}

/// <summary>GitFileChange 扩展：删除判断。</summary>
public static class GitFileChangeExtensions
{
    public static bool IsDeleted(this GitFileChange c) => c.StatusCode.StartsWith("D", StringComparison.Ordinal);

    /// <summary>重命名、删除等无新增内容的情况（大文件检查跳过）。</summary>
    public static bool IsDeletedOrRenamedOnly(this GitFileChange c) => IsDeleted(c) || c.StatusCode.StartsWith("R", StringComparison.Ordinal);
}