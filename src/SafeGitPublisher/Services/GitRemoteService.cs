using System.Text.RegularExpressions;

namespace SafeGitPublisher.Services;

/// <summary>
/// Git Remote URL 解析与校验。纯静态逻辑。
/// 兼容 https://github.com/owner/repo.git、git@github.com:owner/repo.git、
/// ssh://git@github.com/owner/repo.git 等常见 GitHub URL。
/// </summary>
public static class GitRemoteService
{
    /// <summary>
    /// 解析 GitHub 仓库 URL。
    /// </summary>
    /// <returns>(owner, repo, isMalformed, reason, suggestedUrl)。owner/repo 为 null 表示非标准 GitHub URL。</returns>
    public static (string? Owner, string? Repo, bool Malformed, string Reason, string? Suggested) ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (null, null, false, string.Empty, null);
        }

        var u = url.Trim();

        // 明显的转义/格式错误：反斜杠（https\://）、不完整 https:
        // 仅当 URL 明显是网络 URL（含 github.com 或 //）才判畸形；本地 Windows 路径含反斜杠不在此列。
        if (u.Contains('\\') &&
            (u.Contains("github.com", StringComparison.OrdinalIgnoreCase) || u.Contains("://", StringComparison.Ordinal)))
        {
            var fixedUrl = u.Replace("\\", string.Empty);
            return (null, null, true, "URL 包含反斜杠转义（疑似 https\\:// 错误输入），建议使用标准的 https:// 或 git@ 格式。", FixUrl(fixedUrl));
        }

        if (u.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, true, "URL 缺少 // 分隔（疑似 https:github.com/... 错误输入）。", FixUrl(u));
        }

        var (owner, repo) = ExtractOwnerRepo(u);
        if (owner == null || repo == null)
        {
            return (null, null, false, "非 GitHub 标准 URL（可能为本地路径或其它托管平台）。", null);
        }

        return (owner, repo, false, string.Empty, null);
    }

    /// <summary>提取 owner/repo。非 GitHub URL 返回 null。</summary>
    public static (string? Owner, string? Repo) ExtractOwnerRepo(string url)
    {
        var u = url.Trim();

        string? githubPart = null;

        if (u.StartsWith("git@", StringComparison.Ordinal) && u.Contains(':'))
        {
            // git@github.com:owner/repo.git
            githubPart = u[(u.IndexOf(':') + 1)..];
        }
        else if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var afterScheme = u[(u.IndexOf("//", StringComparison.Ordinal) + 2)..];
            var slash = afterScheme.IndexOf('/');
            if (slash < 0) return (null, null);
            var host = afterScheme[..slash];
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return (null, null);
            githubPart = afterScheme[(slash + 1)..];
        }
        else if (u.StartsWith("ssh://", StringComparison.Ordinal))
        {
            var rest = u["ssh://".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return (null, null);
            rest = rest[(slash + 1)..];
            githubPart = rest;
        }
        else
        {
            // 本地路径、file:// 等
            return (null, null);
        }

        return SplitOwnerRepo(githubPart);
    }

    private static (string? Owner, string? Repo) SplitOwnerRepo(string part)
    {
        part = part.TrimEnd('/');
        if (part.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) part = part[..^4];

        var segments = part.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return (null, null);

        var owner = segments[^2];
        var repo = segments[^1];

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return (null, null);

        // owner/repo 之后多余路径视为异常
        if (segments.Length > 2) return (null, null);

        return (owner, repo);
    }

    /// <summary>给畸形 URL 生成建议修复值。</summary>
    private static string? FixUrl(string raw)
    {
        var u = raw.Trim();
        if (string.IsNullOrWhiteSpace(u)) return null;

        var trimmed = u.TrimEnd('\\', '/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[..^4];

        if (trimmed.Contains("github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = trimmed.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase) + "github.com/".Length;
            var pair = trimmed[idx..].Trim('/');
            if (pair.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            {
                return $"https://github.com/{pair}.git";
            }
        }
        return null;
    }

    /// <summary>生成标准的 GitHub 新建仓库 URL。</summary>
    public static string BuildOriginUrl(string owner, string repo)
    {
        return $"https://github.com/{owner}/{repo}.git";
    }

    /// <summary>
    /// 判断 URL 是否属于 GitHub（用于决定“设置 origin”按钮可用性）。
    /// </summary>
    public static bool IsGitHubUrl(string url)
    {
        var (owner, repo, _, _, _) = ParseUrl(url);
        return owner != null && repo != null;
    }
}