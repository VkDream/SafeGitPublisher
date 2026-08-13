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
    /// <summary>
    /// Secret 扫描的绝对体积上限。上限内逐行流式扫描；超过上限时不静默跳过，而是按扫描不完整阻断。
    /// 该值与 GitHub 单文件 100 MiB 硬限制保持一致，避免在大文件检查前产生漏放行窗口。
    /// </summary>
    private const long MaxStreamScanBytes = 100L * 1024 * 1024;

    private const int EncodingProbeBytes = 8 * 1024;

    /// <summary>防止无换行超长文本令 ReadLine 持续扩容；超出时明确阻断，不降级为漏扫。</summary>
    private const int MaxLineCharacters = 1024 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(false, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, byteOrderMark: true, throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32Le = new UTF32Encoding(false, byteOrderMark: true, throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32Be = new UTF32Encoding(true, byteOrderMark: true, throwOnInvalidCharacters: true);

    static SecretScanner()
    {
        // .NET 默认不启用 Windows 代码页；显式注册后才能可靠读取中文项目常见的 GBK/GB18030 文件。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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

    /// <summary>单文件扫描处置，用于证明哪些文件真正被扫描、因二进制跳过或扫描失败。</summary>
    public enum ScanFileDisposition
    {
        Scanned,
        SkippedBinary,
        Error
    }

    /// <summary>单文件扫描处置记录。Detail 只描述扫描状态，不包含文件内容。</summary>
    public sealed record ScanFileOutcome(string File, ScanFileDisposition Disposition, string Detail);

    /// <summary>扫描结果集合。</summary>
    public sealed class ScanResult
    {
        public List<ScanFinding> Findings { get; } = new();

        public List<ScanFileOutcome> FileOutcomes { get; } = new();

        public int ScannedCount => FileOutcomes.Count(x => x.Disposition == ScanFileDisposition.Scanned);

        public int SkippedCount => FileOutcomes.Count(x => x.Disposition == ScanFileDisposition.SkippedBinary);

        public int ErrorCount => FileOutcomes.Count(x => x.Disposition == ScanFileDisposition.Error);

        /// <summary>只有安全识别出的二进制跳过不影响完整性；任何 Error 均代表扫描覆盖不完整。</summary>
        public bool IsComplete => ErrorCount == 0;

        public bool HasBlocked => Findings.Any(f => f.Severity == ScanSeverity.Blocked);

        public bool HasHigh => Findings.Any(f => f.Severity == ScanSeverity.High);
    }

    /// <summary>
    /// 扫描一批文件（相对 repoRoot 的相对路径）。
    /// </summary>
    public async Task<ScanResult> ScanFilesAsync(string repoRoot, IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        var result = new ScanResult();
        string normalizedRoot;
        try
        {
            normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            AddIncompleteFinding(result, "(repository)", $"仓库路径无效（{ex.GetType().Name}）");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in relativePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rel))
            {
                AddIncompleteFinding(result, "(unknown)", "待扫描文件路径为空");
                continue;
            }
            if (!seen.Add(rel)) continue;

            if (!TryResolveInsideRoot(normalizedRoot, rel, out var fullPath))
            {
                AddIncompleteFinding(result, rel, "文件路径越出仓库根目录");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                AddIncompleteFinding(result, rel, "扫描时文件不存在或已不可访问");
                continue;
            }

            if (!IsTextLike(fullPath))
            {
                result.FileOutcomes.Add(new ScanFileOutcome(rel, ScanFileDisposition.SkippedBinary, "按已知二进制扩展名安全跳过"));
                continue;
            }

            try
            {
                if (ContainsReparsePoint(normalizedRoot, fullPath))
                {
                    AddIncompleteFinding(result, rel, "路径包含符号链接或重解析点，无法证明扫描内容与待提交对象一致");
                    continue;
                }

                var length = new FileInfo(fullPath).Length;
                if (length > MaxStreamScanBytes)
                {
                    AddIncompleteFinding(result, rel,
                        $"文件大小超过 Secret 流式扫描上限 {MaxStreamScanBytes / (1024 * 1024)} MiB");
                    continue;
                }

                var encodingProbe = await ProbeEncodingAsync(fullPath, ct);
                if (encodingProbe.IsBinary)
                {
                    result.FileOutcomes.Add(new ScanFileOutcome(rel, ScanFileDisposition.SkippedBinary, "内容探测确认为二进制"));
                    continue;
                }

                IReadOnlyList<ScanFinding> findings;
                var encodingName = encodingProbe.DisplayName;
                try
                {
                    findings = await ScanDecodedFileAsync(fullPath, rel, encodingProbe.Encoding ?? StrictUtf8,
                        encodingProbe.PreambleLength, ct);
                }
                catch (DecoderFallbackException) when (encodingProbe.AllowGb18030Fallback)
                {
                    var gb18030 = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                    findings = await ScanDecodedFileAsync(fullPath, rel, gb18030, preambleLength: 0, ct);
                    encodingName = "GB18030";
                }

                result.Findings.AddRange(findings);
                result.FileOutcomes.Add(new ScanFileOutcome(rel, ScanFileDisposition.Scanned,
                    $"已完成文本扫描（{encodingName}）"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DecoderFallbackException)
            {
                AddIncompleteFinding(result, rel, "文件无法按 UTF-8、GB18030 或带 BOM 的 UTF-16/UTF-32 安全解码");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                AddIncompleteFinding(result, rel, $"文件读取失败（{ex.GetType().Name}）");
            }
        }
        return result;
    }

    /// <summary>
    /// 扫描已经按原始字节落盘的 Git blob。physicalPath 只用于读取，logicalPath 用于规则和报告；
    /// 不要求临时文件扩展名，始终通过 BOM/UTF-8/GB18030/二进制内容探测决定处置。
    /// </summary>
    public async Task<ScanResult> ScanRawBlobFileAsync(string physicalPath, string logicalPath, CancellationToken ct = default)
    {
        var result = new ScanResult();
        if (string.IsNullOrWhiteSpace(logicalPath))
        {
            AddIncompleteFinding(result, "(unknown)", "Git blob 逻辑路径为空");
            return result;
        }

        try
        {
            var fullPath = Path.GetFullPath(physicalPath);
            var fileInfo = new FileInfo(fullPath);
            if (!fileInfo.Exists)
            {
                AddIncompleteFinding(result, logicalPath, "Git blob 临时文件不存在");
                return result;
            }
            if (fileInfo.Length > MaxStreamScanBytes)
            {
                AddIncompleteFinding(result, logicalPath,
                    $"Git blob 超过 Secret 扫描上限 {MaxStreamScanBytes / (1024 * 1024)} MiB");
                return result;
            }

            var encodingProbe = await ProbeEncodingAsync(fullPath, ct);
            if (encodingProbe.IsBinary)
            {
                result.FileOutcomes.Add(new ScanFileOutcome(logicalPath, ScanFileDisposition.SkippedBinary,
                    "Git blob 内容探测确认为二进制"));
                return result;
            }

            IReadOnlyList<ScanFinding> findings;
            var encodingName = encodingProbe.DisplayName;
            try
            {
                findings = await ScanDecodedFileAsync(fullPath, logicalPath, encodingProbe.Encoding ?? StrictUtf8,
                    encodingProbe.PreambleLength, ct);
            }
            catch (DecoderFallbackException) when (encodingProbe.AllowGb18030Fallback)
            {
                var gb18030 = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                findings = await ScanDecodedFileAsync(fullPath, logicalPath, gb18030, preambleLength: 0, ct);
                encodingName = "GB18030";
            }

            result.Findings.AddRange(findings);
            result.FileOutcomes.Add(new ScanFileOutcome(logicalPath, ScanFileDisposition.Scanned,
                $"Git blob 已完成文本扫描（{encodingName}）"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DecoderFallbackException)
        {
            AddIncompleteFinding(result, logicalPath, "Git blob 无法按 UTF-8、GB18030 或带 BOM 的 UTF-16/UTF-32 安全解码");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidDataException)
        {
            AddIncompleteFinding(result, logicalPath, $"Git blob 扫描失败（{ex.GetType().Name}）");
        }
        return result;
    }

    /// <summary>按逻辑文件名判断是否属于已知二进制格式；无扩展名文件必须进入内容探测。</summary>
    public static bool IsKnownBinaryPath(string logicalPath)
    {
        var extension = Path.GetExtension(logicalPath);
        return extension.Length > 0 && BinaryExtensions.Contains(extension);
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

    /// <summary>
    /// 是否为 Secret 扫描候选。只按已知二进制扩展名排除；体积、编码和内容二进制判定由扫描流程记录处置结果。
    /// </summary>
    public static bool IsTextLike(string fullPath)
    {
        var fi = new FileInfo(fullPath);
        if (!fi.Exists) return false;

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext.Length == 0)
        {
            // Dockerfile/Makefile/Jenkinsfile 等常见文本都无扩展名；其余无扩展名文件交由内容探测确认。
            return true;
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

    private sealed record EncodingProbe(
        bool IsBinary, Encoding? Encoding, bool AllowGb18030Fallback, string DisplayName, int PreambleLength);

    /// <summary>
    /// 探测 BOM 和二进制特征。UTF-16/UTF-32 的 NUL 字节属于正常编码结构，必须先识别 BOM 再判断二进制。
    /// </summary>
    private static async Task<EncodingProbe> ProbeEncodingAsync(string fullPath, CancellationToken ct)
    {
        var buffer = new byte[EncodingProbeBytes];
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: EncodingProbeBytes, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

        if (count >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
        {
            return new EncodingProbe(false, StrictUtf32Be, false, "UTF-32 BE BOM", 4);
        }
        if (count >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            return new EncodingProbe(false, StrictUtf32Le, false, "UTF-32 LE BOM", 4);
        }
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return new EncodingProbe(false, StrictUtf8, false, "UTF-8 BOM", 3);
        }
        if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return new EncodingProbe(false, StrictUtf16Le, false, "UTF-16 LE BOM", 2);
        }
        if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return new EncodingProbe(false, StrictUtf16Be, false, "UTF-16 BE BOM", 2);
        }

        for (var i = 0; i < count; i++)
        {
            if (buffer[i] == 0)
            {
                return new EncodingProbe(true, null, false, "binary", 0);
            }
        }

        return new EncodingProbe(false, StrictUtf8, true, "UTF-8", 0);
    }

    /// <summary>用严格解码器逐行扫描，避免将整个大文本一次性载入内存。</summary>
    private async Task<IReadOnlyList<ScanFinding>> ScanDecodedFileAsync(
        string fullPath, string relativePath, Encoding encoding, int preambleLength, CancellationToken ct)
    {
        var findings = new List<ScanFinding>();
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (preambleLength > 0) stream.Position = preambleLength;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: false);

        var lineNo = 0;
        var buffer = new char[64 * 1024];
        var lineBuilder = new StringBuilder();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (count == 0) break;

            for (var i = 0; i < count; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    lineNo++;
                    if (lineBuilder.Length > 0 && lineBuilder[^1] == '\r') lineBuilder.Length--;
                    findings.AddRange(AnalyzeLine(relativePath, lineBuilder.ToString(), lineNo));
                    lineBuilder.Clear();
                    continue;
                }

                if (lineBuilder.Length >= MaxLineCharacters)
                {
                    throw new InvalidDataException(
                        $"第 {lineNo + 1} 行超过 {MaxLineCharacters} 字符，无法在固定内存上限内完整扫描。");
                }
                lineBuilder.Append(ch);
            }
        }

        if (lineBuilder.Length > 0)
        {
            lineNo++;
            if (lineBuilder[^1] == '\r') lineBuilder.Length--;
            findings.AddRange(AnalyzeLine(relativePath, lineBuilder.ToString(), lineNo));
        }
        return findings;
    }

    private static void AddIncompleteFinding(ScanResult result, string relativePath, string detail)
    {
        result.FileOutcomes.Add(new ScanFileOutcome(relativePath, ScanFileDisposition.Error, detail));
        result.Findings.Add(new ScanFinding(relativePath, "secret-scan-incomplete", ScanSeverity.Blocked,
            $"Secret 扫描未完整覆盖：{detail}。为避免未扫描内容被提交，已阻断。"));
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

    /// <summary>拒绝经由符号链接/重解析点读取仓库外或可竞态替换的内容。</summary>
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
