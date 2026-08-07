namespace SafeGitPublisher.Models;

/// <summary>
/// Remote 检查结果（解析 git remote -v + URL 校验）。
/// </summary>
public sealed class RemoteInfo
{
    /// <summary>远端名称（通常为 origin）。</summary>
    public string Name { get; init; } = "origin";

    /// <summary>是否已配置名为 origin（或任意名称）的 remote。</summary>
    public bool HasRemote { get; init; }

    /// <summary>fetch URL 原始文本。</summary>
    public string? FetchUrl { get; init; }

    /// <summary>push URL 原始文本。</summary>
    public string? PushUrl { get; init; }

    /// <summary>GitHub owner（无法解析时为空）。</summary>
    public string? Owner { get; init; }

    /// <summary>GitHub repo 名（无法解析时为空）。</summary>
    public string? RepoName { get; init; }

    /// <summary>URL 是否存在明显异常（如 https\:// 转义错误）。</summary>
    public bool IsMalformed { get; init; }

    /// <summary>异常原因描述。</summary>
    public string MalformedReason { get; init; } = string.Empty;

    /// <summary>建议修复的 URL（异常时给出）。</summary>
    public string? SuggestedUrl { get; init; }

    /// <summary>仓库全名（owner/repo），可显示。</summary>
    public string DisplayName => Owner is null || RepoName is null
        ? (string.IsNullOrWhiteSpace(FetchUrl) ? "（无 origin）" : "（非标准 URL）")
        : $"{Owner}/{RepoName}";

    public string FetchDisplay => string.IsNullOrWhiteSpace(FetchUrl) ? "（未设置）" : FetchUrl;

    public string PushDisplay => string.IsNullOrWhiteSpace(PushUrl) ? "（未设置）" : PushUrl;
}