namespace SafeGitPublisher.Models;

/// <summary>
/// Git 作者配置来源。
/// </summary>
public enum ConfigSource
{
    NotSet,
    Local,
    Global,
    Effective
}

/// <summary>
/// Git 作者检查结果（name + email）。
/// </summary>
public sealed class GitIdentityInfo
{
    public string? Name { get; init; }

    public ConfigSource NameSource { get; init; } = ConfigSource.NotSet;

    public string? Email { get; init; }

    public ConfigSource EmailSource { get; init; } = ConfigSource.NotSet;

    /// <summary>推荐作者名（来自设置）。</summary>
    public string? RecommendedName { get; init; }

    /// <summary>推荐作者邮箱（来自设置）。</summary>
    public string? RecommendedEmail { get; init; }

    public bool NameMatches => !string.IsNullOrWhiteSpace(Name) &&
                               string.Equals(Name, RecommendedName, StringComparison.Ordinal);

    public bool EmailMatches => !string.IsNullOrWhiteSpace(Email) &&
                                string.Equals(Email, RecommendedEmail, StringComparison.Ordinal);

    /// <summary>是否任一配置缺失或不匹配。</summary>
    public bool HasIssue => !NameMatches || !EmailMatches;

    public string NameDisplay => string.IsNullOrEmpty(Name) ? "（未设置）" : Name;

    public string EmailDisplay => string.IsNullOrEmpty(Email) ? "（未设置）" : Email;

    public string NameSourceDisplay => SourceDisplay(NameSource);

    public string EmailSourceDisplay => SourceDisplay(EmailSource);

    private static string SourceDisplay(ConfigSource s) => s switch
    {
        ConfigSource.Local => "LOCAL",
        ConfigSource.Global => "GLOBAL",
        ConfigSource.Effective => "EFFECTIVE",
        _ => "NOT SET"
    };
}