using System.Diagnostics;
using System.Text;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 进程执行请求。统一用 ArgumentList 传递参数，严禁手工拼接 cmd 字符串，
/// 以正确支持含中文、空格、特殊字符的路径。
/// </summary>
public sealed class ProcessRequest
{
    /// <summary>可执行文件完整路径或 PATH 中的命令名（如 "git"、"dotnet"）。</summary>
    public required string FileName { get; init; }

    /// <summary>参数列表（每项一个参数，不做 shell 拼接）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>工作目录。为 null 时使用当前进程目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>需要写入 stdin 的内容（如 git check-ignore --stdin）。为空则不写。</summary>
    public string? StandardInputText { get; init; }

    /// <summary>超时时间。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>是否按 UTF-8 读取输出（git/dotnet 均按 UTF-8 输出）。</summary>
    public bool Utf8Output { get; init; } = true;

    /// <summary>stdout 逐行回调（可用于 UI 实时日志）。</summary>
    public Action<string>? OnStdoutLine { get; init; }

    /// <summary>stderr 逐行回调。</summary>
    public Action<string>? OnStderrLine { get; init; }
}

/// <summary>
/// 进程执行器。异步执行外部进程，支持取消、超时、stdin、UTF-8 输出。
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// 异步执行一个进程。
    /// </summary>
    /// <param name="request">执行请求。</param>
    /// <param name="cancellationToken">取消令牌：取消或超时均会 Kill 整个进程树。</param>
    /// <returns>执行结果。</returns>
    public async Task<CommandResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInputText != null,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (request.Utf8Output)
        {
            // 显式按 UTF-8 解码，避免中文输出在管道中乱码
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
        }

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var startedAt = DateTime.UtcNow;

        try
        {
            if (!proc.Start())
            {
                return new CommandResult { ExitCode = null, Duration = DateTime.UtcNow - startedAt };
            }
        }
        catch (Exception ex)
        {
            // git/dotnet 未安装或路径无效：Start 会抛 Win32Exception
            throw new ProcessLaunchException(request.FileName, ex);
        }

        void OnOut(string? data)
        {
            if (data == null) return;
            lock (stdoutLines) stdoutLines.Add(data);
            request.OnStdoutLine?.Invoke(data);
        }

        void OnErr(string? data)
        {
            if (data == null) return;
            lock (stderrLines) stderrLines.Add(data);
            request.OnStderrLine?.Invoke(data);
        }

        proc.OutputDataReceived += (_, e) => OnOut(e.Data);
        proc.ErrorDataReceived += (_, e) => OnErr(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // 写 stdin（少量内容，同步写即可）
        if (request.StandardInputText != null)
        {
            try
            {
                await proc.StandardInput.WriteAsync(request.StandardInputText);
                proc.StandardInput.Close();
            }
            catch
            {
                // 进程提前退出导致管道关闭，忽略
            }
        }

        // 取消/超时联动的令牌
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        bool timedOut = false;
        bool canceled = false;
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
            }
            else
            {
                timedOut = true;
            }

            try
            {
                // 进程树整体 Kill，避免残留子进程（如 dotnet build 的子任务）
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Kill 失败时忽略，进程最终会被系统回收
            }
        }

        var duration = DateTime.UtcNow - startedAt;

        // 确保输出缓冲读取完成
        await Task.Delay(50);

        return new CommandResult
        {
            ExitCode = proc.HasExited ? proc.ExitCode : null,
            Canceled = canceled,
            TimedOut = timedOut,
            Duration = duration,
            Stdout = stdoutLines.ToArray(),
            Stderr = stderrLines.ToArray()
        };
    }
}

/// <summary>
/// 进程无法启动（可执行文件不存在 / 权限等）。
/// </summary>
public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(string fileName, Exception inner)
        : base($"无法启动进程：{fileName}。请确认该程序已安装并位于 PATH 中。", inner)
    {
    }
}
