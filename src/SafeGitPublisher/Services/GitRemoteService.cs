using System.Text.RegularExpressions;

namespace SafeGitPublisher.Services;

/// <summary>
/// Git Remote URL 解析与校验。只把 host 精确为 github.com 的网络地址识别为 GitHub；
/// 本地路径允许用于本地验证，但未知网络协议和伪装的 SSH host 会判为异常。
/// </summary>
public static class GitRemoteService
{
    private static readonly Regex ScpLikeRegex = new(
        @"^(?<user>[^@/\s:]+)@(?<host>[^:/\s]+):(?<path>.+)$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析 GitHub 仓库 URL。
    /// </summary>
    /// <returns>(owner, repo, isMalformed, reason, suggestedUrl)。owner/repo 为 null 表示本地路径或非 GitHub 地址。</returns>
    public static (string? Owner, string? Repo, bool Malformed, string Reason, string? Suggested) ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return (null, null, false, string.Empty, null);
        }

        var value = url.Trim();

        // Windows 盘符、UNC 与普通相对路径是本地 Remote。必须先于 Uri 解析，
        // 否则 D:\repo 会被误识别为自定义协议 d:。
        if (IsLocalPath(value))
        {
            return (null, null, false, "本地路径 Remote。", null);
        }

        // 明显的转义/格式错误：https\://github.com/...。
        if (value.Contains('\\') &&
            (value.Contains("github.com", StringComparison.OrdinalIgnoreCase) || value.Contains("://", StringComparison.Ordinal)))
        {
            var fixedUrl = value.Replace("\\", string.Empty);
            return (null, null, true, "URL 包含反斜杠转义，建议使用标准 https:// 或 git@ 格式。", FixUrl(fixedUrl));
        }

        if (value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, true, "URL 缺少 // 分隔。", FixUrl(value));
        }

        var scp = ScpLikeRegex.Match(value);
        if (scp.Success)
        {
            var user = scp.Groups["user"].Value;
            if (!string.Equals(user, "git", StringComparison.Ordinal))
            {
                return (null, null, true, "SSH Remote 用户必须为 git。", null);
            }
            var host = scp.Groups["host"].Value;
            if (!IsGitHubHost(host))
            {
                return (null, null, true, "SSH Remote host 不是 github.com。", null);
            }

            var (owner, repo) = SplitOwnerRepo(scp.Groups["path"].Value);
            return owner == null || repo == null
                ? (null, null, true, "GitHub SSH URL 缺少有效的 owner/repo。", null)
                : (owner, repo, false, string.Empty, null);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme is "https" or "http" or "ssh")
            {
                if (scheme == "http")
                {
                    return (null, null, true, "GitHub Remote 不允许使用明文 HTTP，请改用 HTTPS 或 SSH。", null);
                }
                if (!IsGitHubHost(uri.Host))
                {
                    return (null, null, true, $"{scheme.ToUpperInvariant()} Remote host 不是 github.com。", null);
                }

                var (owner, repo) = SplitOwnerRepo(uri.AbsolutePath);
                if (owner == null || repo == null)
                {
                    return (null, null, true, "GitHub URL 缺少有效的 owner/repo。", null);
                }

                // HTTPS user-info 可能携带账号/Token，始终拒绝；SSH 的标准 git 用户名不是凭据，
                // 仅允许精确的 git（不含冒号/密码）。
                var allowedSshUser = scheme == "ssh" && string.Equals(uri.UserInfo, "git", StringComparison.Ordinal);
                if (!string.IsNullOrEmpty(uri.UserInfo) && !allowedSshUser)
                {
                    return (owner, repo, true, "Remote URL 包含用户信息或凭据，请改用不含凭据的地址。", BuildOriginUrl(owner, repo));
                }

                if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                {
                    return (owner, repo, true, "Remote URL 不应包含查询参数或片段。", BuildOriginUrl(owner, repo));
                }

                return (owner, repo, false, string.Empty, null);
            }

            if (scheme == "file")
            {
                return (null, null, false, "本地 file:// Remote。", null);
            }

            return (null, null, true, $"不允许的 Remote 协议：{scheme}。", null);
        }

        if (LooksLikeNetworkUrl(value))
        {
            return (null, null, true, "Remote URL 格式或协议不受支持。", null);
        }

        // Windows/UNC/相对路径供本地 bare 仓库验证使用，不冒充 GitHub URL。
        return (null, null, false, "本地路径 Remote。", null);
    }

    /// <summary>提取 GitHub owner/repo；非 GitHub 或异常 URL 返回 null。</summary>
    public static (string? Owner, string? Repo) ExtractOwnerRepo(string url)
    {
        var (owner, repo, malformed, _, _) = ParseUrl(url);
        return malformed ? (null, null) : (owner, repo);
    }

    /// <summary>生成标准 GitHub HTTPS URL。</summary>
    public static string BuildOriginUrl(string owner, string repo)
    {
        return $"https://github.com/{owner}/{repo}.git";
    }

    /// <summary>判断 URL 是否为校验通过的 GitHub URL。</summary>
    public static bool IsGitHubUrl(string url)
    {
        var (owner, repo, malformed, _, _) = ParseUrl(url);
        return !malformed && owner != null && repo != null;
    }

    /// <summary>
    /// 生成可写入日志或确认页的安全显示值。HTTPS/SSH URL 会删除 user-info、query 和 fragment。
    /// </summary>
    public static string RedactForDisplay(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "（未设置）";
        var value = url.Trim();

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ssh")
        {
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host}{port}{uri.AbsolutePath}";
        }

        var at = value.IndexOf('@');
        if (at > 0)
        {
            var suffix = value[at..];
            return value.StartsWith("git@", StringComparison.Ordinal) ? "git" + suffix : "***" + suffix;
        }

        return value;
    }

    /// <summary>
    /// 脱敏准备进入 UI、日志或错误对话的 Git 输出。
    /// 移除 HTTP(S)/SSH URI 中的 user-info，并隐藏 scp-like 地址中非标准 git 用户。
    /// </summary>
    public static string RedactOutput(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var redacted = Regex.Replace(text,
            @"(?<scheme>https?|ssh)://(?<userinfo>[^\s/@]+(?:\:[^\s/@]*)?)@(?<host>[^\s/:]+)",
            match => $"{match.Groups["scheme"].Value}://***@{match.Groups["host"].Value}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        redacted = Regex.Replace(redacted,
            @"(?<![\w.-])(?<user>[^\s/@:]+)@(?<host>[^\s/:]+):(?<path>[^\s]+)",
            match => string.Equals(match.Groups["user"].Value, "git", StringComparison.Ordinal)
                ? match.Value
                : $"***@{match.Groups["host"].Value}:{match.Groups["path"].Value}",
            RegexOptions.CultureInvariant);
        return redacted;
    }

    private static bool IsGitHubHost(string host) =>
        string.Equals(host.TrimEnd('.'), "github.com", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeNetworkUrl(string value) =>
        value.Contains("://", StringComparison.Ordinal) ||
        value.Contains("::", StringComparison.Ordinal) ||
        value.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    private static (string? Owner, string? Repo) SplitOwnerRepo(string part)
    {
        var normalized = part.Trim().Trim('/');
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) return (null, null);

        var owner = Uri.UnescapeDataString(segments[0]);
        var repo = Uri.UnescapeDataString(segments[1]);
        if (!IsSafeRepositorySegment(owner) || !IsSafeRepositorySegment(repo)) return (null, null);
        return (owner, repo);
    }

    private static bool IsSafeRepositorySegment(string value) =>
        value.Length > 0 &&
        value is not "." and not ".." &&
        value.IndexOfAny(new[] { '/', '\\', ':', '@', '?', '#', '\0' }) < 0;

    private static bool IsLocalPath(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal) ||
        value.StartsWith("./", StringComparison.Ordinal) ||
        value.StartsWith("../", StringComparison.Ordinal) ||
        value.StartsWith(@".\", StringComparison.Ordinal) ||
        value.StartsWith(@"..\", StringComparison.Ordinal) ||
        (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/')) ||
        (!value.Contains("://", StringComparison.Ordinal) && !value.Contains('@') && !value.Contains(':'));

    private static string? FixUrl(string raw)
    {
        var value = raw.Trim();
        var marker = "github.com/";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;

        var pair = value[(index + marker.Length)..].Trim('/', '\\');
        if (pair.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) pair = pair[..^4];
        var (owner, repo) = SplitOwnerRepo(pair);
        return owner == null || repo == null ? null : BuildOriginUrl(owner, repo);
    }
}
