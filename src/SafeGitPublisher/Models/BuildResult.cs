using SafeGitPublisher.Services;

namespace SafeGitPublisher.Models;

/// <summary>
/// dotnet build 的执行结果。
/// </summary>
public sealed class BuildResult
{
    /// <summary>是否执行了构建（跳过时 false）。</summary>
    public bool BuildRun { get; init; }

    /// <summary>构建目标类型（Solution/Project/None/Ambiguous）。</summary>
    public BuildTargetKind TargetKind { get; init; } = BuildTargetKind.None;

    /// <summary>构建目标完整路径（sln / csproj），未执行时为空。</summary>
    public string? ProjectPath { get; init; }

    /// <summary>构建目标文件名（不含路径，用于界面展示，避免刷满完整路径）。</summary>
    public string? TargetDisplay { get; init; }

    /// <summary>命令摘要，例如 "dotnet build SafeGitPublisher.slnx"。</summary>
    public string CommandSummary { get; init; } = string.Empty;

    /// <summary>构建是否成功（退出码 0）。</summary>
    public bool Succeeded { get; init; }

    /// <summary>退出码。</summary>
    public int ExitCode { get; init; }

    /// <summary>警告数量（从输出中统计，可能为 0）。</summary>
    public int WarningCount { get; init; }

    /// <summary>错误数量。</summary>
    public int ErrorCount { get; init; }

    /// <summary>执行耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>是否被取消/超时。</summary>
    public bool TimedOut { get; init; }

    /// <summary>输出尾部摘要（供日志展示）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>关键错误行摘要（最多 3 条，供失败详情展示，不刷满完整输出）。</summary>
    public List<string> ErrorLines { get; init; } = new();

    /// <summary>未执行构建时的原因（None/Skip/Ambiguous）。</summary>
    public string SkipReason { get; init; } = string.Empty;

    /// <summary>构建模式：执行时为 "Isolated Temporary Output"（隔离临时输出）；未执行时为空。</summary>
    public string BuildMode { get; init; } = string.Empty;

    /// <summary>本次隔离构建使用的临时输出根（%TEMP%\SafeGitPublisher\PreflightBuild\&lt;GUID&gt;），供排错/测试断言。</summary>
    public string? IsolationRoot { get; init; }

    /// <summary>隔离目录清理是否失败（不影响构建成功判定，仅提示）。</summary>
    public bool CleanupFailed { get; init; }
}