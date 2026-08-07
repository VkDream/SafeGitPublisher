namespace SafeGitPublisher.Models;

/// <summary>
/// 一次 git 仓库检测的结果。
/// </summary>
public sealed class RepositoryInfo
{
    /// <summary>git 是否可用（git.exe 找到）。</summary>
    public bool GitAvailable { get; init; }

    /// <summary>git 版本字符串，如 "2.54.0"。</summary>
    public string GitVersion { get; init; } = string.Empty;

    /// <summary>git rev-parse --show-toplevel 的结果（仓库根目录绝对路径）。空表示不是仓库。</summary>
    public string? TopLevel { get; init; }

    /// <summary>是否为 Git 仓库（TopLevel 非空）。</summary>
    public bool IsRepository => !string.IsNullOrWhiteSpace(TopLevel);

    /// <summary>用户选择的路径不在仓库根，但位于仓库内部（TopLevel 是上级目录）。</summary>
    public bool InsideRepoRoot => !string.IsNullOrWhiteSpace(TopLevel) &&
                                  !string.Equals(Normalize(TopLevel), Normalize(SelectedPath ?? string.Empty), StringComparison.OrdinalIgnoreCase);

    /// <summary>用户当前选择的路径。</summary>
    public string? SelectedPath { get; init; }

    private static string Normalize(string p) => p.TrimEnd('\\', '/');
}