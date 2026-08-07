using System.IO;
using System.Text;

namespace SafeGitPublisher.Services;

/// <summary>
/// .gitignore 管理：推荐规则、读取、合并（只追加缺失规则，不覆盖、不重复）。
/// </summary>
public sealed class GitIgnoreService
{
    /// <summary>
    /// .NET 项目推荐规则（按任务书）。
    /// 目录规则（末尾 /）需要按"任意层级"匹配：git 规则中裸目录名匹配任意层级。
    /// </summary>
    public static readonly string[] RequiredRules =
    {
        "[Bb]in/",
        "[Oo]bj/",
        ".vs/",
        "publish/",
        "tmp/",
        "*.db",
        "*.db-shm",
        "*.db-wal",
        "*.db-journal",
        "*.sqlite",
        "*.sqlite3",
        ".env",
        ".env.*",
        "secrets.json",
        "appsettings.Local.json",
        "*.pfx",
        "*.p12",
        "*.key",
        "*.pem",
        ".claude/",
        ".reasonix/",
        ".serena/",
        "*.log"
    };

    /// <summary>计算缺失的推荐规则（大小写与空白敏感，逐行比较）。</summary>
    public static List<string> ComputeMissingRules(string existingContent, IEnumerable<string> requiredRules)
    {
        var existing = new HashSet<string>(
            existingContent.Split('\n').Select(l => l.TrimEnd('\r').Trim()),
            StringComparer.Ordinal);

        return requiredRules.Where(r => !existing.Contains(r)).ToList();
    }

    /// <summary>生成合并后的完整内容（现有内容 + 缺失规则），并保证以换行结尾。</summary>
    public static string BuildMergedContent(string existingContent, IReadOnlyList<string> missingRules)
    {
        var sb = new StringBuilder();
        var existing = existingContent ?? string.Empty;
        sb.Append(existing);
        if (!string.IsNullOrEmpty(existing) && !existing.EndsWith('\n'))
        {
            sb.Append('\n');
        }

        var section = "\n# --- SafeGitPublisher 推荐规则（追加） ---\n";
        sb.Append(section);
        foreach (var rule in missingRules)
        {
            sb.Append(rule).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>合并缺失规则（供调用方直接写文件）。</summary>
    public static string Merge(string existingContent, IEnumerable<string> requiredRules)
    {
        var missing = ComputeMissingRules(existingContent, requiredRules);
        if (missing.Count == 0) return existingContent;
        return BuildMergedContent(existingContent, missing);
    }

    /// <summary>
    /// 为仓库追加缺失的推荐规则并写回 .gitignore。
    /// 返回写入内容（或 null 表示无需修改）。
    /// </summary>
    public static async Task<string?> ApplyAsync(string repoRoot, CancellationToken ct = default)
    {
        var gitignorePath = Path.Combine(repoRoot, ".gitignore");
        var existing = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath, ct) : string.Empty;
        var missing = ComputeMissingRules(existing, RequiredRules);
        if (missing.Count == 0) return null;

        var merged = BuildMergedContent(existing, missing);
        await File.WriteAllTextAsync(gitignorePath, merged, Encoding.UTF8, ct);
        return merged;
    }
}