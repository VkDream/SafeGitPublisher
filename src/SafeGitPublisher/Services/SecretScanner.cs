using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// Secret/Token 扫描器。
/// 只扫描“准备提交的文本文件”，不会全盘扫描。
/// 输出统一脱敏（Preview 仅含前缀 + **** + 末尾 4 位），日志/UI 永远不出现凭据原文。
/// 严重度：Info（敏感关键字提示）→ Warning（内网地址等）→ High（疑似凭据）→ Blocked（明确 Token 格式）。
/// </summary>
public sealed class SecretScanner
{
    /// <summary>单文件最大扫描体积（超过视为二进制/大文件，跳过）。</summary>
    private const int MaxScanBytes = 2 * 1024 * 1024;

    /// <summary>明确的 Token 格式（→ Blocked）。</summary>
    private static readonly (string Id, Regex Regex, string Label)[] TokenRules =
    {
        ("github_pat", new Regex(@"github_pat_[A-Za-z0-9_]{10,}"), "GitHub personal access token（github_pat_）"),
        ("ghp", new Regex(@"\bghp_[A-Za-z0-9]{10,}"), "GitHub classic token（ghp_）"),
        ("openai", new Regex(@"\bsk-[A-Za-z0-9]{4,}"), "OpenAI / AI API key（sk-）"),
        ("aws", new Regex(@"\bAKIA[0-9A-Z]{16}\b"), "AWS Access Key（AKIA…）"),
        ("bearer", new Regex(@"(?i)\bBearer\s+[A-Za-z0-9._~+\-/]{6,}"), "Bearer Token")
    };

    /// <summary>赋值类敏感键（键名 + 字面量值 → High）。密码类键值放宽长度下限以捕获连接串。</summary>
    private static readonly (string Id, Regex Regex, string Label, bool IsPasswordKey)[] AssignmentRules =
    {
        ("secret", new Regex(@"(?i)\b(secret|client_secret|access_token|private_key|api[_-]?key|apikey|token)\s*[=:]\s*(?<val>[^;\r\n]+)"), "密钥/令牌类配置", false),
        ("password", new Regex(@"(?i)\b(password|passwd|pwd)\s*[=:]\s*(?<val>[^;\r\n]+)"), "密码/口令类配置", true)
    };

    /// <summary>内网地址（Warning）。</summary>
    private static readonly Regex PrivateIpRegex = new(
        @"\b(192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b");

    /// <summary>连接串 Server / User Id（非本机地址时为 Warning）。</summary>
    private static readonly Regex ServerAssignRegex = new(
        @"(?i)\b(server|user\s*id)\s*=\s*(?<value>[^;\r\n]+)");

    /// <summary>敏感关键字（Info，用于输出“已扫描”证据，不阻断）。</summary>
    private static readonly Regex KeywordRegex = new(
        @"(?i)\b(password|passwd|pwd|secret|token|apikey|api[_-]?key|client_secret|access_token|private_key|authorization|bearer|credential)\b");

    /// <summary>常见占位符/示例值（匹配则视为非真实凭据）。</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "password", "passwd", "pwd", "secret", "token", "xxxx", "xxxxx", "xxxxxx",
        "123456", "000000", "1234567", "789456", "654321", "123123", "changeme", "changeit",
        "example", "example123", "sample", "sample123", "demo", "demo123", "test", "test123",
        "admin", "admin123", "root", "yourpassword", "your_password", "your-secret",
        "your_token", "your-token", "your_api_key", "your-apikey", "placeholder",
        "placeholder123", "n/a", "na", "null", "false", "true", "empty", "default",
        "defaultpassword", "dbpassword", "mysql", "postgres", "sa", "pass", "p@ss",
        "p@ssw0rd", "12345", "1234", "54321", "qwerty", "iloveyou", "letmein", "monkey",
        "dragon", "base64", "encrypted", "loremipsum", "foobar", "foo", "bar", "dummy",
        "dummy123", "abc123", "abcdef", "abcd1234", "password123", "random", "generated",
        "undefined", "unknown", "default123", "changeme123", "secret123", "your password here"
    };

    /// <summary>扫描结果集合。</summary>
    public sealed class ScanResult
    {
        public List<ScanFinding> Findings { get; } = new();

        public bool HasBlocked => Findings.Any(f => f.Severity == ScanSeverity.Blocked);

        public bool HasHigh => Findings.Any(f => f.Severity == ScanSeverity.High);
    }

    /// <summary>
    /// 扫描一批文件（相对 repoRoot 的相对路径）。
    /// </summary>
    public async Task<ScanResult> ScanFilesAsync(string repoRoot, IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        var result = new ScanResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(rel)) continue;

            var fullPath = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) continue;
            if (!IsTextLike(fullPath)) continue;

            try
            {
                var content = await SafeReadAsync(fullPath, ct);
                if (content == null) continue;
                foreach (var finding in ScanContent(rel, content))
                {
                    result.Findings.Add(finding);
                }
            }
            catch
            {
                // 单个文件读取失败不影响整体扫描
            }
        }
        return result;
    }

    /// <summary>扫描单个文件内容（供测试直接调用）。</summary>
    public IReadOnlyList<ScanFinding> ScanContent(string relativePath, string content)
    {
        var findings = new List<ScanFinding>();
        var lines = content.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var finding in AnalyzeLine(relativePath, lines[i], i + 1))
            {
                findings.Add(finding);
            }
        }
        return findings;
    }

    private IEnumerable<ScanFinding> AnalyzeLine(string rel, string line, int lineNo)
    {
        // 1) 明确的 Token 格式（→ Blocked）
        foreach (var (id, regex, label, _) in TokenRulesWithFlag())
        {
            var m = regex.Match(line);
            if (m.Success)
            {
                var preview = RedactToken(m.Value);
                yield return new ScanFinding(rel, id, ScanSeverity.Blocked,
                    $"命中{label}（行 {lineNo}），疑似真实凭据，详细信息已脱敏。", preview, line: lineNo);
                yield break; // 一行命中多个 Token 时只报第一个
            }
        }

        // 2) 赋值类敏感键（字面量才判 High）
        foreach (var (id, regex, label, isPasswordKey) in AssignmentRules)
        {
            var m = regex.Match(line);
            if (m.Success && IsSecretValue(m.Groups["val"].Value, isPasswordKey))
            {
                yield return new ScanFinding(rel, id, ScanSeverity.High,
                    $"{label}疑似包含明文值（行 {lineNo}），请确认不是真实凭据。",
                    $"{id}=****{RedactTail(m.Groups["val"].Value)}", line: lineNo);
            }
        }

        // 3) 连接串 Server / User Id（非本机地址 → Warning）
        var serverMatchResult = ServerAssignRegex.Match(line);
        if (serverMatchResult.Success && !IsLocalServer(serverMatchResult.Groups["value"].Value.Trim()))
        {
            var value = serverMatchResult.Groups["value"].Value.Trim().Trim('"', '\'');
            yield return new ScanFinding(rel, "server-host", ScanSeverity.Warning,
                $"连接串指定了非本机服务器地址（行 {lineNo}），公开仓库可能泄露内网信息。", value, line: lineNo);
        }

        // 4) 内网私有地址（→ Warning）
        var ipMatch = PrivateIpRegex.Match(line);
        if (ipMatch.Success)
        {
            yield return new ScanFinding(rel, "private-ip", ScanSeverity.Warning,
                $"出现内网地址（行 {lineNo}），公开仓库可能泄露内部网络信息。", ipMatch.Value, line: lineNo);
        }

        // 5) 仅关键字（→ Info）
        if (KeywordRegex.IsMatch(line))
        {
            yield return new ScanFinding(rel, "keyword", ScanSeverity.Info,
                $"包含敏感关键字（行 {lineNo}），请人工确认非真实凭据。", line: lineNo);
        }
    }

    private static IEnumerable<(string Id, Regex Regex, string Label, bool IsPasswordKey)> TokenRulesWithFlag()
    {
        foreach (var (id, regex, label) in TokenRules)
        {
            yield return (id, regex, label, false);
        }
    }

    private static bool IsLocalServer(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
               value == "127.0.0.1" ||
               value.StartsWith("(local)", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(".\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断一行中赋值右侧是否像真实凭据。
    /// 正常代码形式（var password = form["password"]、PasswordHash = HashPassword(...)）会被排除。
    /// </summary>
    private static bool IsSecretValue(string rawValue, bool isPasswordKey)
    {
        var value = rawValue.Trim();
        // 去掉尾部注释 / 分隔符
        var cutIdx = value.IndexOfAny(new[] { ';', ',', ')' });
        if (cutIdx >= 0) value = value[..cutIdx];
        value = value.Trim();

        if (value.Length == 0) return false;

        // 去除成对引号
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
        value = value.Trim();

        if (value.Length == 0) return false;

        // 索引/方法/配置引用 → 是代码引用而非字面量
        if (value.Contains('[') || value.Contains(']') || value.Contains('(') || value.Contains(')'))
            return false;

        // 环境变量引用
        if (value.StartsWith('$')) return false;

        var lower = value.ToLowerInvariant();
        if (lower.Contains("getenvironmentvariable") || lower.Contains("environment.")
            || lower.Contains("configuration") || lower.Contains("appsettings")
            || lower.Contains("form[") || lower.Contains("request[") || lower.Contains("user[")
            || lower.Contains("settings[") || lower.Contains("obj.") || lower.Contains("this.")
            || lower.Contains("new "))
            return false;

        // 占位符直接排除
        if (Placeholders.Contains(lower)) return false;

        // 裸变量名引用（如 Password 对 password 赋值、api_key 对 token 赋值）
        if (IsKnownVarWord(lower)) return false;

        // 短纯数字：连接串场景（Password 紧接 123 纯数字形式）需捕获，其余键太长无意义
        if (Regex.IsMatch(value, @"^\d{1,5}$"))
        {
            return isPasswordKey; // 密码键捕获（连接串常见短密码），其它键忽略
        }

        // 太长且含空格的更像自然语言
        if (value.Contains(' ') && value.Length > 30) return false;

        // 普通键值最小长度 4；密码键最小长度 1
        var minLen = isPasswordKey ? 1 : 4;
        if (value.Length < minLen) return false;

        return true;
    }

    private static bool IsKnownVarWord(string lower)
    {
        return lower is "password" or "passwd" or "pwd" or "token" or "secret" or "apikey"
            or "api_key" or "api-key" or "client_secret" or "access_token" or "private_key"
            or "value" or "val" or "item" or "data" or "result" or "name" or "user" or "p";
    }

    /// <summary>Token 脱敏：保留公开前缀与末尾 4 位。</summary>
    public static string RedactToken(string raw)
    {
        if (raw.Length <= 8) return "****";
        string head;
        if (raw.StartsWith("github_pat_", StringComparison.Ordinal)) head = "github_pat_";
        else if (raw.StartsWith("ghp_", StringComparison.Ordinal)) head = "ghp_";
        else if (raw.StartsWith("sk-", StringComparison.Ordinal)) head = "sk-";
        else if (raw.StartsWith("AKIA", StringComparison.Ordinal)) head = raw[..4];
        else head = raw[..Math.Min(8, raw.Length)];

        var tail = raw[^4..];
        return $"{head}****{tail}";
    }

    /// <summary>赋值值脱敏（尾部 4 位）。</summary>
    public static string RedactTail(string raw, int keepTail = 4)
    {
        var v = raw.Trim().Trim('"', '\'');
        if (v.Length <= keepTail) return "****";
        return $"****{v[^keepTail..]}";
    }

    /// <summary>是否为可扫描的文本文件（大小受限 + 扩展名过滤）。</summary>
    public static bool IsTextLike(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists || fi.Length > MaxScanBytes) return false;

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext.Length == 0)
        {
            var name = Path.GetFileName(fullPath);
            return name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(".bash", StringComparison.OrdinalIgnoreCase);
        }

        return !BinaryExtensions.Contains(ext);
    }

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".ico", ".tif", ".tiff",
        ".dll", ".exe", ".obj", ".pdb", ".lib", ".so", ".o",
        ".pdf", ".zip", ".tar", ".gz", ".7z", ".rar", ".bz2", ".xz",
        ".db", ".sqlite", ".sqlite3", ".mdb", ".ldf", ".mdf", ".pfx", ".p12", ".xlsx", ".xls",
        ".doc", ".docx", ".ppt", ".pptx", ".mp3", ".mp4", ".wav", ".flac", ".bin", ".iso"
    };

    /// <summary>安全读取文本：含 null 字节视为二进制；GBK 文件回退 GB18030 解码（中文项目常见）。</summary>
    private static async Task<string?> SafeReadAsync(string fullPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0) return null;
        }
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}