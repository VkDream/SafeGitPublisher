namespace SafeGitPublisher.Models;

/// <summary>
/// 敏感扫描发现的严重级别。
/// Info=关键字提示；Warning=需注意；High=高危疑似；Blocked=确认为凭据类，阻断。
/// </summary>
public enum ScanSeverity
{
    Info,
    Warning,
    High,
    Blocked
}

/// <summary>
/// 一条扫描发现（Secret 扫描 / 敏感文件 / 大文件共用）。
/// 注意：Preview 字段只允许存放脱敏摘要，禁止存放凭据原文。
/// </summary>
public sealed class ScanFinding
{
    public ScanFinding(string file, string ruleId, ScanSeverity severity, string message, string? preview = null, int line = 0)
    {
        File = file;
        RuleId = ruleId;
        Severity = severity;
        Message = message;
        Preview = preview;
        Line = line;
    }

    /// <summary>相对仓库根的路径。</summary>
    public string File { get; }

    /// <summary>行号（Secret 扫描等行级规则提供；文件级规则为 0）。</summary>
    public int Line { get; }

    /// <summary>命中规则标识。</summary>
    public string RuleId { get; }

    public ScanSeverity Severity { get; }

    /// <summary>人工可读说明。</summary>
    public string Message { get; }

    /// <summary>脱敏摘要（仅允许脱敏后内容）。</summary>
    public string? Preview { get; }

    public string SeverityDisplay => Severity switch
    {
        ScanSeverity.Info => "INFO",
        ScanSeverity.Warning => "WARNING",
        ScanSeverity.High => "HIGH",
        _ => "BLOCKED"
    };
}