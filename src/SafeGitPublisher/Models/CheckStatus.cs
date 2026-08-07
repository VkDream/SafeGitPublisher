namespace SafeGitPublisher.Models;

/// <summary>
/// 检查项状态级别。
/// Pass=通过；Info=提示；Warning=警告（不阻断，可视需要确认继续）；Blocked=阻断。
/// </summary>
public enum CheckStatus
{
    Pass,
    Info,
    Warning,
    Blocked
}