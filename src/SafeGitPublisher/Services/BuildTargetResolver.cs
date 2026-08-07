using System.IO;

namespace SafeGitPublisher.Services;

/// <summary>构建目标解析结果类型。</summary>
public enum BuildTargetKind
{
    /// <summary>无任何 .NET solution/project。</summary>
    None,

    /// <summary>solution（.sln / .slnx）。</summary>
    Solution,

    /// <summary>单个 csproj。</summary>
    Project,

    /// <summary>存在多个候选且无法自动确定（需用户选择）。</summary>
    Ambiguous
}

/// <summary>
/// 构建目标解析结果。
/// </summary>
public sealed record BuildTargetInfo(BuildTargetKind Kind, string? Path, string? FileName, string? Reason)
{
    public static BuildTargetInfo Found(BuildTargetKind kind, string path) =>
        new(kind, path, System.IO.Path.GetFileName(path), null);

    public static BuildTargetInfo Ambiguous(string reason) => new(BuildTargetKind.Ambiguous, null, null, reason);

    public static BuildTargetInfo None(string reason) => new(BuildTargetKind.None, null, null, reason);
}

/// <summary>
/// Build Target 解析器（纯静态逻辑，可单测）。
/// 合同：
/// 1) 仓库根目录存在唯一 *.sln / *.slnx → 构建该 solution；
/// 2) 根目录存在多个 solution → 名称与仓库名匹配者优先，仍歧义则 Ambiguous；
/// 3) 无 solution → 递归搜索 *.csproj（排除 bin/obj/.git/node_modules）；
/// 4) 唯一 csproj → 构建该 csproj；
/// 5) 多个 csproj → 与仓库名匹配的主应用候选优先，仍歧义则 Ambiguous；
/// 6) 完全无 .NET 项目 → None（跳过构建，绝不报 MSB1009）。
/// 绝不假定 &lt;RepositoryRoot&gt;\&lt;RepositoryName&gt;.csproj 一定存在。
/// </summary>
public static class BuildTargetResolver
{
    private static readonly string[] SolutionExtensions = { ".sln", ".slnx" };
    private static readonly string[] ExcludedDirectories =
        { ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".claude", ".reasonix" };

    private const int MaxDepth = 8;

    /// <summary>
    /// 解析仓库的构建目标。
    /// </summary>
    /// <param name="repoRoot">仓库根目录（Git Repository Root）。</param>
    public static BuildTargetInfo Resolve(string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return BuildTargetInfo.None("仓库目录不存在或不可访问。");
        }

        var repoName = new DirectoryInfo(repoRoot).Name;

        // ---- 1/2) 根目录 solution（*.sln / *.slnx）----
        var rootSolutions = ListRootSolutions(repoRoot);
        if (rootSolutions.Count == 1)
        {
            return BuildTargetInfo.Found(BuildTargetKind.Solution, rootSolutions[0]);
        }
        if (rootSolutions.Count > 1)
        {
            var matched = rootSolutions.FirstOrDefault(s =>
                NameWithoutSolutionExtension(s).Equals(repoName, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                return BuildTargetInfo.Found(BuildTargetKind.Solution, matched);
            }
            return BuildTargetInfo.Ambiguous(
                "根目录存在多个 solution，无法自动确定：" + string.Join("、", rootSolutions.Select(System.IO.Path.GetFileName)));
        }

        // ---- 3/4/5) 递归搜索 csproj ----
        var projects = new List<string>();
        try
        {
            Walk(new DirectoryInfo(repoRoot), repoRoot, 0, projects);
        }
        catch
        {
            return BuildTargetInfo.None("枚举项目文件失败。");
        }

        if (projects.Count == 0)
        {
            return BuildTargetInfo.None("未发现 .sln/.slnx/.csproj，判定为非 .NET 项目。");
        }
        if (projects.Count == 1)
        {
            return BuildTargetInfo.Found(BuildTargetKind.Project, projects[0]);
        }

        // 多 csproj：主应用候选优先（与仓库名匹配，例如 src\RepoName\RepoName.csproj）
        var primary = projects.FirstOrDefault(p =>
            System.IO.Path.GetFileNameWithoutExtension(p).Equals(repoName, StringComparison.OrdinalIgnoreCase));
        if (primary != null)
        {
            return BuildTargetInfo.Found(BuildTargetKind.Project, primary);
        }

        return BuildTargetInfo.Ambiguous(
            "未发现 solution 且存在多个 csproj，无法自动确定：" +
            string.Join("、", projects.Select(System.IO.Path.GetFileName)) +
            "（请人工确认构建目标）");
    }

    private static List<string> ListRootSolutions(string repoRoot)
    {
        var result = new List<string>();
        try
        {
            foreach (var f in new DirectoryInfo(repoRoot).EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
            {
                if (SolutionExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(f.FullName);
                }
            }
        }
        catch
        {
            // 无权限时按无 solution 处理
        }
        return result;
    }

    private static void Walk(DirectoryInfo dir, string repoRoot, int depth, List<string> projects)
    {
        if (depth > MaxDepth) return;

        foreach (var f in dir.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
        {
            if (f.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projects.Add(f.FullName);
            }
        }

        foreach (var d in dir.EnumerateDirectories())
        {
            if (ExcludedDirectories.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            Walk(d, repoRoot, depth + 1, projects);
        }
    }

    private static string NameWithoutSolutionExtension(string path)
    {
        foreach (var ext in SolutionExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFileNameWithoutExtension(path);
            }
        }
        return System.IO.Path.GetFileName(path);
    }
}
