using System.IO;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 敏感文件规则（纯逻辑，可单测）。
/// </summary>
public static class SensitiveFileRules
{
    /// <summary>硬阻断：目录名（任意层级命中即阻断）。</summary>
    public static readonly string[] BlockedDirectoryNames =
    {
        "bin", "obj", "publish", "tmp", ".vs", ".idea", ".claude", ".reasonix", ".serena"
    };

    /// <summary>硬阻断：文件名（精确或通配）。</summary>
    public static readonly string[] BlockedFileNames =
    {
        "*.db", "*.db-shm", "*.db-wal", "*.db-journal",
        "*.sqlite", "*.sqlite3", "*.mdf", "*.ldf",
        ".env", ".env.*",
        "secrets.json", "appsettings.Local.json",
        "*.pfx", "*.p12", "*.key", "*.pem",
        "*.log"
    };

    private static readonly string[] BlockedDirRegex = BlockedDirectoryNames
        .Select(d => $"^{RegexEscape(d)}$").ToArray();

    private static readonly (string Pattern, string RegexName)[] BlockedFileRegex = BlockedFileNames
        .Select(f => (f, WildcardToRegex(f)))
        .ToArray();

    private static string RegexEscape(string s) => System.Text.RegularExpressions.Regex.Escape(s);

    private static string WildcardToRegex(string pattern)
    {
        // 通配符转正则：* → .*
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c == '*' ? ".*" : System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            // 保持大小写不敏感
        }
        sb.Append('$');
        return sb.ToString();
    }

    /// <summary>
    /// 判断一个相对路径（用 / 或 \ 分隔均可）是否命中阻断规则。
    /// </summary>
    public static bool IsBlockedPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        var fileName = segments[^1];
        string? hitRule = null;

        // 目录命中
        foreach (var seg in segments)
        {
            foreach (var dir in BlockedDirectoryNames)
            {
                if (string.Equals(seg, dir, StringComparison.OrdinalIgnoreCase))
                {
                    hitRule ??= $"{dir}/（目录）";
                    break;
                }
            }
            if (hitRule != null) break;
        }

        // 文件名命中（含 wildcard）
        foreach (var (pattern, nameRegex) in BlockedFileRegex)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, nameRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                hitRule ??= pattern;
                break;
            }
        }
        if (hitRule == null) return false;

        return true;
    }

    /// <summary>返回命中的规则说明。</summary>
    public static string BlockReason(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        foreach (var seg in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var d in BlockedDirectoryNames)
            {
                if (string.Equals(seg, d, StringComparison.OrdinalIgnoreCase))
                {
                    return d switch
                    {
                        "bin" or "obj" => "编译输出目录（bin/obj）不应上传",
                        "publish" => "发布输出目录（publish）不应上传",
                        "tmp" => "临时目录（tmp）不应上传",
                        ".vs" or ".idea" => "IDE 元数据目录不应上传",
                        ".claude" or ".reasonix" or ".serena" => "本机 AI 工具元数据不应上传",
                        _ => "本地输出目录不应上传"
                    };
                }
            }
        }

        var name = normalized.Split('/').Last();
        if (WildcardMatch(name, "*.db") || WildcardMatch(name, "*.db-*") ||
            WildcardMatch(name, "*.sqlite") || WildcardMatch(name, "*.sqlite3") ||
            WildcardMatch(name, "*.mdf") || WildcardMatch(name, "*.ldf"))
            return "数据库文件不应上传（SQLite/MDF/LDF）";
        if (WildcardMatch(name, "*.pfx") || WildcardMatch(name, "*.p12") ||
            WildcardMatch(name, "*.key") || WildcardMatch(name, "*.pem"))
            return "密钥/证书文件不应上传";
        if (name == ".env" || NameWithPrefix(name, ".env."))
            return "环境变量文件不应上传（可能包含密钥）";
        if (name == "secrets.json" || NameOrdinalIgnoreCase(name, "appsettings.Local.json"))
            return "本地机密配置不应上传";
        if (WildcardMatch(name, "*.log"))
            return "日志文件不应上传（可能包含敏感信息）";

        return "敏感文件不应上传";
    }

    private static bool WildcardMatch(string name, string pattern)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(name, WildcardToRegex(pattern), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool NameWithPrefix(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool NameOrdinalIgnoreCase(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 敏感文件扫描结果。
/// </summary>
public sealed class SensitiveScanResult
{
    public List<ScanFinding> Findings { get; } = new();

    /// <summary>已被 .gitignore 排除但磁盘上存在的危险文件（展示用，不阻断）。</summary>
    public List<string> IgnoredSafePaths { get; } = new();
}

/// <summary>
/// 敏感文件扫描器：对变更文件、已跟踪文件、磁盘文件三来源检查。
/// 结合 .gitignore，只对“实际存在提交风险”的路径阻断。
/// </summary>
public sealed class SensitiveFileScanner
{
    private readonly GitService _git;

    public SensitiveFileScanner(GitService git)
    {
        _git = git;
    }

    /// <summary>
    /// 执行敏感文件扫描。
    /// </summary>
    /// <param name="repoRoot">仓库根目录。</param>
    /// <param name="changes">当前 status 变更（工作区）。</param>
    /// <param name="trackedFiles">git ls-files 输出（已跟踪文件）。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task<SensitiveScanResult> ScanAsync(
        string repoRoot,
        IReadOnlyList<GitFileChange> changes,
        IReadOnlyList<string>? trackedFiles,
        CancellationToken ct = default)
    {
        var result = new SensitiveScanResult();
        var badPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 变更中的敏感文件（工作区未忽略，存在实际提交风险）
        foreach (var change in changes)
        {
            if (change.IsConflict) continue;
            if (!SensitiveFileRules.IsBlockedPath(change.Path)) continue;
            result.Findings.Add(new ScanFinding(
                change.Path, "sensitive-file", ScanSeverity.Blocked,
                SensitiveFileRules.BlockReason(change.Path)));
            badPaths.Add(change.Path);
        }

        // 2) 已跟踪文件中的敏感文件（历史上已经入库，也需警告）
        if (trackedFiles != null)
        {
            foreach (var f in trackedFiles)
            {
                if (SensitiveFileRules.IsBlockedPath(f) && badPaths.Add(f))
                {
                    result.Findings.Add(new ScanFinding(
                        f, "sensitive-tracked", ScanSeverity.Blocked,
                        "已跟踪的敏感文件：" + SensitiveFileRules.BlockReason(f)));
                }
            }
        }

        // 3) 磁盘上存在、但已被 .gitignore 排除 → 仅提示安全
        try
        {
            var diskCandidates = CollectDiskCandidates(repoRoot, ct);
            var ignored = await _git.GetIgnoredPathsAsync(repoRoot, diskCandidates, ct);
            foreach (var p in ignored)
            {
                result.IgnoredSafePaths.Add(p);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 磁盘枚举失败不影响已发现的结果
        }

        return result;
    }

    private static List<string> CollectDiskCandidates(string repoRoot, CancellationToken ct)
    {
        var root = new DirectoryInfo(repoRoot);

        // 递归枚举可能命中的目录/文件。避开 .git 与阻塞目录（其内部即使有 .db 也属于被忽略场景，仍可能需提示，但为避免枚举爆炸直接跳过）
        var candidates = new List<string>();
        Collect(root, repoRoot, 0, candidates, ct);
        return candidates;
    }

    private static void Collect(DirectoryInfo dir, string repoRoot, int depth, List<string> candidates, CancellationToken ct)
    {
        if (depth > 12) return;
        ct.ThrowIfCancellationRequested();

        IEnumerable<FileInfo> files;
        IEnumerable<DirectoryInfo> dirs;
        try
        {
            files = dir.EnumerateFiles();
            dirs = dir.EnumerateDirectories();
        }
        catch
        {
            return; // 无权限目录跳过
        }

        foreach (var f in files)
        {
            if (SensitiveFileRules.IsBlockedPath(Relative(repoRoot, f.FullName)))
            {
                candidates.Add(Relative(repoRoot, f.FullName));
            }
        }

        foreach (var d in dirs)
        {
            var name = d.Name;
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            // 不进入阻塞目录内部：其内容本就被 gitignore 忽略
            if (SensitiveFileRules.IsBlockedPath(name)) continue;
            Collect(d, repoRoot, depth + 1, candidates, ct);
        }
    }

    private static string Relative(string root, string full)
    {
        var rel = full.Substring(root.Length).TrimStart('\\', '/');
        return rel.Replace('\\', '/');
    }
}