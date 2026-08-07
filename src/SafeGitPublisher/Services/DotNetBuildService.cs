using System.IO;
using System.Text.RegularExpressions;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// .NET 项目检测与构建服务（直接调用 dotnet CLI）。
/// 构建目标解析委托 BuildTargetResolver，绝不假定 csproj 位于仓库根目录。
/// </summary>
public sealed class DotNetBuildService
{
    private readonly ProcessRunner _runner;

    public DotNetBuildService(ProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// 在仓库中解析构建目标（sln / slnx / csproj，规则见 BuildTargetResolver）。
    /// </summary>
    public static BuildTargetInfo FindBuildTarget(string repoRoot) => BuildTargetResolver.Resolve(repoRoot);

    /// <summary>是否为 .NET 项目（存在 solution/csproj；Ambiguous 视为 .NET 项目但需人工选择）。</summary>
    public static bool IsDotNetProject(string repoRoot)
    {
        var target = BuildTargetResolver.Resolve(repoRoot);
        return target.Kind is BuildTargetKind.Solution or BuildTargetKind.Project or BuildTargetKind.Ambiguous;
    }

    /// <summary>
    /// 构建整个仓库（自动解析构建目标）。无 .NET 项目或目标歧义时不执行构建。
    /// </summary>
    public async Task<BuildResult> BuildRepositoryAsync(string repoRoot, bool skipBuild, CancellationToken ct = default)
    {
        if (skipBuild)
        {
            return new BuildResult { BuildRun = false, TargetKind = BuildTargetKind.None, SkipReason = "根据设置“提交前构建 = 否”，已跳过构建。" };
        }

        var target = BuildTargetResolver.Resolve(repoRoot);
        switch (target.Kind)
        {
            case BuildTargetKind.None:
                return new BuildResult { BuildRun = false, TargetKind = BuildTargetKind.None, SkipReason = "未发现 .sln/.slnx/.csproj，判定为非 .NET 项目，跳过构建。" };
            case BuildTargetKind.Ambiguous:
                return new BuildResult
                {
                    BuildRun = false,
                    TargetKind = BuildTargetKind.Ambiguous,
                    SkipReason = $"存在多个构建目标，无法自动确定：{target.Reason}"
                };
            case BuildTargetKind.Solution:
            case BuildTargetKind.Project:
                break;
            default:
                return new BuildResult { BuildRun = false, TargetKind = BuildTargetKind.None, SkipReason = "无法解析构建目标。" };
        }

        var project = target.Path!;
        var start = DateTime.UtcNow;
        var output = new List<string>();

        // self-host 缺陷修复：构建输出全部隔离到 %TEMP%\SafeGitPublisher\PreflightBuild\<GUID>。
        // 即使 SafeGitPublisher.exe 自身正在运行（正式 bin\Debug 输出被锁定），
        // --artifacts-path 也会把 apphost/exe/bin/obj 写入隔离目录，不会触碰仓库 bin/obj 与运行中 EXE。
        // 若隔离目录创建失败，绝不静默降级为传统构建（否则会复现 MSB3027/MSB3021 自我锁定），而是明确返回未执行原因。
        var isolationRoot = TempBuildRoot.CreateRoot();
        if (isolationRoot == null)
        {
            return new BuildResult
            {
                BuildRun = false,
                TargetKind = target.Kind,
                TargetDisplay = target.FileName,
                ProjectPath = project,
                SkipReason = "无法创建隔离构建输出目录（%TEMP% 不可写或空间不足），已跳过构建，请检查临时目录后重新检查。"
            };
        }

        var result = await _runner.RunAsync(new ProcessRequest
        {
            FileName = "dotnet",
            Arguments = new List<string>
            {
                "build", project, "--nologo", "-v:m",
                "--artifacts-path", isolationRoot
            },
            WorkingDirectory = repoRoot,
            Timeout = TimeSpan.FromMinutes(10),
            Utf8Output = true,
            OnStdoutLine = line => { lock (output) output.Add(line); },
            OnStderrLine = line => { lock (output) output.Add(line); }
        }, ct);

        // best-effort 清理：失败不影响构建结果判定，仅标记供日志提示
        var cleanupOk = TempBuildRoot.TryCleanup(isolationRoot);
        if (!cleanupOk)
        {
            output.Add($"Warning: 隔离构建临时目录清理失败（不影响构建结果）：{isolationRoot}");
        }

        var duration = DateTime.UtcNow - start;
        var text = string.Join("\n", output);

        // 中文/英文构建输出都识别：优先退出码，辅助关键字
        var failedKeywords = new[] { "Build FAILED", "生成失败", "error CS", "error MSB" };
        var succeededKeywords = new[] { "Build succeeded", "生成成功", "已成功生成" };
        bool succeeded;
        if (result.Canceled || result.TimedOut)
        {
            succeeded = false;
        }
        else if (result.ExitCode == 0 && !failedKeywords.Any(text.Contains))
        {
            succeeded = true;
        }
        else if (succeededKeywords.Any(text.Contains) && result.ExitCode == 0)
        {
            succeeded = true;
        }
        else
        {
            succeeded = false;
        }

        var warningCount = CountOccurrences(text, "warning");
        var errorCount = CountOccurrences(text, "error");

        var summary = string.Join(" | ", output.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3));
        var errorLines = output
            .Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(l => l.Trim())
            .ToList();

        return new BuildResult
        {
            BuildRun = true,
            TargetKind = target.Kind,
            ProjectPath = project,
            TargetDisplay = target.FileName,
            CommandSummary = $"dotnet build {target.FileName}（隔离输出）",
            BuildMode = "Isolated Temporary Output",
            IsolationRoot = isolationRoot,
            Succeeded = succeeded,
            ExitCode = result.ExitCode ?? -1,
            WarningCount = warningCount,
            ErrorCount = errorCount,
            Duration = duration,
            TimedOut = result.TimedOut,
            Summary = summary,
            ErrorLines = errorLines,
            CleanupFailed = !cleanupOk
        };
    }

    private static int CountOccurrences(string text, string token)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var m = Regex.Matches(text, @"\b" + Regex.Escape(token) + @"\b");
        return m.Count;
    }
}
