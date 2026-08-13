namespace SafeGitPublisher.Models;

/// <summary>
/// Remote 检查结果（解析 git remote -v + URL 校验）。
/// </summary>
public sealed class RemoteInfo
{
    /// <summary>远端名称（通常为 origin）。</summary>
    public string Name { get; init; } = "origin";

    /// <summary>是否已配置名为 origin 的 remote。其他名称不满足发布合同。</summary>
    public bool HasRemote { get; init; }

    /// <summary>fetch URL 的安全显示文本；凭据、查询参数和片段已移除。</summary>
    public string? FetchUrl { get; init; }

    /// <summary>push URL 的安全显示文本；凭据、查询参数和片段已移除。</summary>
    public string? PushUrl { get; init; }

    /// <summary>仅供发布工作流使用的精确 fetch URL，不允许写入日志或 UI。</summary>
    internal string? ExactFetchUrl { get; set; }

    /// <summary>仅供发布工作流使用的精确 push URL，不允许写入日志或 UI。</summary>
    internal string? ExactPushUrl { get; set; }

    /// <summary>实际发布目标；显式 push URL 优先，否则使用 fetch URL。</summary>
    internal string? ExactEffectivePushUrl => ExactPushUrl ?? ExactFetchUrl;

    /// <summary>GitHub owner（无法解析时为空）。</summary>
    public string? Owner { get; init; }

    /// <summary>GitHub repo 名（无法解析时为空）。</summary>
    public string? RepoName { get; init; }

    /// <summary>URL 是否存在明显异常（如 https\:// 转义错误）。</summary>
    public bool IsMalformed { get; init; }

    /// <summary>fetch URL 是否异常；与 push URL 分开保留审计结果。</summary>
    public bool FetchIsMalformed { get; init; }

    /// <summary>push URL 是否异常；与 fetch URL 分开保留审计结果。</summary>
    public bool PushIsMalformed { get; init; }

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

    /// <summary>最终 push 目标的安全显示值（push URL 优先）。</summary>
    public string EffectivePushDisplay => !string.IsNullOrWhiteSpace(PushUrl) ? PushUrl : FetchDisplay;

    /// <summary>发布目标完成比较后立即清除精确 URL，避免凭据型异常配置在对象中长时间驻留。</summary>
    internal void ClearExactUrls()
    {
        ExactFetchUrl = null;
        ExactPushUrl = null;
    }
}
