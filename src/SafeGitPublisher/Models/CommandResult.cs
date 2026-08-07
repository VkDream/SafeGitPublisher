namespace SafeGitPublisher.Models;

/// <summary>
/// 一次外部进程（git.exe / dotnet.exe）执行的完整结果。
/// </summary>
public sealed class CommandResult
{
    /// <summary>进程退出码。null 表示进程未成功启动。</summary>
    public int? ExitCode { get; init; }

    /// <summary>是否被调用方主动取消。</summary>
    public bool Canceled { get; init; }

    /// <summary>是否因超时被杀掉。</summary>
    public bool TimedOut { get; init; }

    /// <summary>执行耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>标准输出行。</summary>
    public IReadOnlyList<string> Stdout { get; init; } = Array.Empty<string>();

    /// <summary>标准错误行。</summary>
    public IReadOnlyList<string> Stderr { get; init; } = Array.Empty<string>();

    public bool Started => ExitCode != null;

    /// <summary>是否成功（退出码为 0 且未被取消/超时）。</summary>
    public bool Success => ExitCode == 0 && !Canceled && !TimedOut;

    public string StdOutText => string.Join(Environment.NewLine, Stdout);

    public string StdErrText => string.Join(Environment.NewLine, Stderr);
}