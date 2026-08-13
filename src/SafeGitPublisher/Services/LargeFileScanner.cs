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
    /// <summary>GitHub 普通 Git 仓库的单文件硬限制，不能由用户设置调高。</summary>
    private const double GitHubHardLimitMB = 100;

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
        if (mb > GitHubHardLimitMB)
        {
            return (RiskLevel.Blocked,
                $"文件大小为 {mb:F1} MB，超过 GitHub 100 MB 硬限制，推送会被 GitHub 拒绝。");
        }
        if (mb > _blockingMB)
        {
            return (RiskLevel.Blocked,
                $"文件大小为 {mb:F1} MB，超过已配置阻断阈值 {_blockingMB:F0} MB。请使用 Git LFS、拆分或移除该文件。");
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
        var normalizedRoot = TryNormalizeRoot(repoRoot);
        foreach (var change in changes)
        {
            if (change.IsDeletedLike()) continue;

            if (normalizedRoot == null)
            {
                AddIncompleteFinding(findings, change.Path, "仓库根路径无效");
                change.Risk = RiskLevel.Blocked;
                continue;
            }
            if (!TryResolveInsideRoot(normalizedRoot, change.Path, out var full))
            {
                AddIncompleteFinding(findings, change.Path, "文件路径无效或越出仓库根目录");
                change.Risk = RiskLevel.Blocked;
                continue;
            }
            if (!File.Exists(full))
            {
                AddIncompleteFinding(findings, change.Path, "扫描时文件不存在或已不可访问");
                change.Risk = RiskLevel.Blocked;
                continue;
            }

            long size;
            try
            {
                if (ContainsReparsePoint(normalizedRoot, full))
                {
                    AddIncompleteFinding(findings, change.Path, "路径包含符号链接或重解析点");
                    change.Risk = RiskLevel.Blocked;
                    continue;
                }
                size = new FileInfo(full).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                AddIncompleteFinding(findings, change.Path, $"无法读取文件大小（{ex.GetType().Name}）");
                change.Risk = RiskLevel.Blocked;
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

    private static void AddIncompleteFinding(List<ScanFinding> findings, string path, string reason)
    {
        findings.Add(new ScanFinding(path, "large-file-scan-incomplete", ScanSeverity.Blocked,
            $"大文件扫描未完整覆盖：{reason}。为避免未检查内容被提交，已阻断。"));
    }

    private static string? TryNormalizeRoot(string repoRoot)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool TryResolveInsideRoot(string normalizedRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            if (Path.IsPathRooted(relativePath)) return false;
            fullPath = Path.GetFullPath(Path.Combine(normalizedRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool ContainsReparsePoint(string normalizedRoot, string fullPath)
    {
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        var current = normalizedRoot;
        foreach (var part in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }
}

/// <summary>GitFileChange 扩展：删除判断。</summary>
public static class GitFileChangeExtensions
{
    public static bool IsDeleted(this GitFileChange c) => c.IsDeletedLike();

    /// <summary>重命名、删除等无新增内容的情况（大文件检查跳过）。</summary>
    public static bool IsDeletedOrRenamedOnly(this GitFileChange c) => c.IsDeletedLike() || c.StatusCode.StartsWith("R", StringComparison.Ordinal);
}
