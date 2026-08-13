using System.Diagnostics;
using System.IO;
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
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        void OnOut(string data)
        {
            lock (stdoutLines) stdoutLines.Add(data);
            request.OnStdoutLine?.Invoke(data);
        }

        void OnErr(string data)
        {
            lock (stderrLines) stderrLines.Add(data);
            request.OnStderrLine?.Invoke(data);
        }

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) stdoutClosed.TrySetResult();
            else OnOut(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) stderrClosed.TrySetResult();
            else OnErr(e.Data);
        };
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

        // 进程退出不等于异步输出事件已经全部派发。等待两个重定向管道报告 EOF，
        // 避免用固定延时猜测，从而丢失尾部的构建或 Git 错误信息。
        var outputDrainFailed = false;
        try
        {
            await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // 安全检查不能依赖可能缺少尾部内容的 stdout/stderr。
            // 将管道未完整关闭标记为超时失败，让上层 Gate 失败关闭。
            outputDrainFailed = true;
        }

        var duration = DateTime.UtcNow - startedAt;

        string[] stdoutSnapshot;
        string[] stderrSnapshot;
        lock (stdoutLines) stdoutSnapshot = stdoutLines.ToArray();
        lock (stderrLines) stderrSnapshot = stderrLines.ToArray();
        if (outputDrainFailed)
        {
            stderrSnapshot = stderrSnapshot.Append("进程输出管道未完整关闭，结果不可信。").ToArray();
        }

        return new CommandResult
        {
            ExitCode = !outputDrainFailed && proc.HasExited ? proc.ExitCode : null,
            Canceled = canceled,
            TimedOut = timedOut || outputDrainFailed,
            Duration = duration,
            Stdout = stdoutSnapshot,
            Stderr = stderrSnapshot
        };
    }

    /// <summary>
    /// 执行进程并把 stdout 原始字节直接写入指定文件。此路径禁止文本解码，适用于 Git blob；
    /// stderr 仍按严格 UTF-8 行读取，仅用于脱敏后的错误摘要。输出文件由调用方负责 finally 清理。
    /// </summary>
    public async Task<CommandResult> RunRawStdoutToFileAsync(ProcessRequest request, string outputPath, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var fullOutputPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("原始 stdout 输出目录不存在。");
        }

        var psi = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInputText != null,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in request.Arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrLines = new List<string>();
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedAt = DateTime.UtcNow;
        try
        {
            if (!process.Start()) return new CommandResult { ExitCode = null, Duration = DateTime.UtcNow - startedAt };
        }
        catch (Exception ex)
        {
            throw new ProcessLaunchException(request.FileName, ex);
        }

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data == null)
            {
                stderrClosed.TrySetResult();
                return;
            }
            lock (stderrLines) stderrLines.Add(eventArgs.Data);
            request.OnStderrLine?.Invoke(eventArgs.Data);
        };
        process.BeginErrorReadLine();

        if (request.StandardInputText != null)
        {
            try
            {
                await process.StandardInput.WriteAsync(request.StandardInputText);
                process.StandardInput.Close();
            }
            catch
            {
                // 进程提前退出，最终以退出码失败关闭。
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);
        var canceled = false;
        var timedOut = false;
        Exception? copyFailure = null;
        try
        {
            await using var output = new FileStream(fullOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copyTask = CopyRawWithLimitAsync(process.StandardOutput.BaseStream, output, maxBytes, timeoutCts.Token);
            var waitTask = process.WaitForExitAsync(timeoutCts.Token);
            try
            {
                await Task.WhenAll(copyTask, waitTask);
                await output.FlushAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = cancellationToken.IsCancellationRequested;
                timedOut = !canceled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                copyFailure = ex;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // 文件创建、Flush 或 Dispose 失败同样必须失败关闭，
            // 并由 finally 终止可能仍在输出的子进程。
            copyFailure = ex;
        }
        finally
        {
            if ((canceled || timedOut || copyFailure != null) && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Kill 失败会由 ExitCode=null/原始复制错误继续失败关闭。
                }
            }
        }

        try
        {
            await stderrClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            copyFailure ??= new IOException("stderr 管道未完整关闭。");
        }

        string[] stderrSnapshot;
        lock (stderrLines) stderrSnapshot = stderrLines.ToArray();
        if (copyFailure != null)
        {
            stderrSnapshot = stderrSnapshot.Append($"原始 stdout 写入失败（{copyFailure.GetType().Name}）。").ToArray();
        }
        return new CommandResult
        {
            ExitCode = copyFailure == null && process.HasExited ? process.ExitCode : null,
            Canceled = canceled,
            TimedOut = timedOut,
            Duration = DateTime.UtcNow - startedAt,
            Stdout = Array.Empty<string>(),
            Stderr = stderrSnapshot
        };
    }

    private static async Task CopyRawWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (count == 0) break;
            total = checked(total + count);
            if (total > maxBytes) throw new InvalidDataException($"原始 stdout 超过允许上限 {maxBytes} 字节。");
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
        }
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
