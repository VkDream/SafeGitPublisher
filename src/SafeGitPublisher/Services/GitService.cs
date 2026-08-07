using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 封装对 git.exe 的调用。所有命令统一通过 ProcessRunner 执行，
/// 并使用 -c core.quotepath=false 保证中文文件名按原文输出。
/// </summary>
public sealed class GitService
{
    private readonly ProcessRunner _runner;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(60);

    public GitService(ProcessRunner runner)
    {
        _runner = runner;
    }

    /// <summary>判断 git 是否可用，并返回版本字符串（失败返回 null）。</summary>
    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _runner.RunAsync(new ProcessRequest
            {
                FileName = "git",
                Arguments = new[] { "--version" },
                Timeout = TimeSpan.FromSeconds(15)
            }, ct);

            if (!result.Success) return null;
            var line = result.Stdout.FirstOrDefault() ?? string.Empty;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"git version\s+([\d.]+)");
            return m.Success ? m.Groups[1].Value : line.Trim();
        }
        catch (ProcessLaunchException)
        {
            return null;
        }
    }

    /// <summary>统一入口：执行 git 命令。自动附带 -c core.quotepath=false。</summary>
    public async Task<CommandResult> RunGitAsync(
        string workDir,
        IReadOnlyList<string> args,
        CancellationToken ct = default,
        TimeSpan? timeout = null,
        string? stdin = null,
        Action<string>? onOut = null,
        Action<string>? onErr = null)
    {
        var fullArgs = new List<string> { "-c", "core.quotepath=false" };
        fullArgs.AddRange(args);
        return await _runner.RunAsync(new ProcessRequest
        {
            FileName = "git",
            Arguments = fullArgs,
            WorkingDirectory = workDir,
            Timeout = timeout ?? _defaultTimeout,
            StandardInputText = stdin,
            OnStdoutLine = onOut,
            OnStderrLine = onErr
        }, ct);
    }

    /// <summary>检查某路径是否为 git 仓库，返回仓库根绝对路径（非仓库时 null）。</summary>
    public async Task<string?> GetTopLevelAsync(string path, CancellationToken ct = default)
    {
        var result = await RunGitAsync(path, new[] { "rev-parse", "--show-toplevel" }, ct);
        if (!result.Success) return null;
        var line = result.Stdout.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line)) return null;
        return NormalizeGitPath(line);
    }

    private static string NormalizeGitPath(string line)
    {
        var p = line.Trim();
        if (p.Length >= 2 && p[1] == ':') p = p.Replace('/', '\\');
        return p;
    }

    /// <summary>git init，默认分支 main（仅用于初始化 Git 仓库流程）。</summary>
    public async Task<CommandResult> InitAsync(string path, CancellationToken ct = default)
    {
        var args = new List<string> { "init", "-b", "main" };
        if (path.TrimEnd('\\', '/').EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("--bare");
        }
        var result = await RunGitAsync(path, args, ct);
        if (!result.Success && !result.Canceled && !result.TimedOut)
        {
            // 旧版 git 兜底：不支持 -b 时退化为默认分支名称
            result = await RunGitAsync(path, new[] { "init" }, ct);
        }
        return result;
    }

    /// <summary>git status --porcelain=v1，未跟踪以单个文件列出。</summary>
    public async Task<CommandResult> StatusPorcelainAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, ct);
    }

    /// <summary>git diff --cached --name-status [-M]：已暂存文件。</summary>
    public async Task<CommandResult> DiffCachedNameStatusAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "diff", "--cached", "--name-status", "-M" }, ct);
    }

    /// <summary>git ls-files：已跟踪文件。</summary>
    public async Task<CommandResult> LsFilesAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "ls-files" }, ct);
    }

    /// <summary>批量查 .gitignore 忽略项，返回被忽略的相对路径集合。</summary>
    public async Task<HashSet<string>> GetIgnoredPathsAsync(string workDir, IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        var joined = string.Join("\n", relativePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(joined)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = await RunGitAsync(workDir, new[] { "check-ignore", "--stdin", "-v" }, ct, stdin: joined);
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Stdout)
        {
            // 输出形如 "<pathname>\t<source>:<line>:<pattern>" 或 "<pathname>"，
            // 取路径名部分（TAB 之后为匹配规则细节）。
            string path;
            var tabIdx = line.LastIndexOf('\t');
            if (tabIdx >= 0) path = line[(tabIdx + 1)..].Trim();
            else path = line.Split(':', 2)[0].Trim();
            if (path.Length > 0) ignored.Add(path);
        }
        return ignored;
    }

    /// <summary>读取 git 配置。scope：Local / Global / Effective。</summary>
    public async Task<string?> ConfigGetAsync(string workDir, string key, ConfigScope scope, CancellationToken ct = default)
    {
        var args = new List<string> { "config" };
        switch (scope)
        {
            case ConfigScope.Local: args.Add("--local"); break;
            case ConfigScope.Global: args.Add("--global"); break;
        }
        args.Add("--get");
        args.Add(key);
        var result = await RunGitAsync(workDir, args, ct);
        return result.Success ? result.Stdout.FirstOrDefault()?.Trim() : null;
    }

    /// <summary>写入 git local 配置。</summary>
    public async Task<CommandResult> ConfigSetLocalAsync(string workDir, string key, string value, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "config", "--local", key, value }, ct);
    }

    /// <summary>git remote -v。</summary>
    public async Task<CommandResult> RemoteVAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "remote", "-v" }, ct);
    }

    /// <summary>git remote add &lt;name&gt; &lt;url&gt;。</summary>
    public async Task<CommandResult> RemoteAddAsync(string workDir, string name, string url, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "remote", "add", name, url }, ct);
    }

    /// <summary>git remote set-url。</summary>
    public async Task<CommandResult> RemoteSetUrlAsync(string workDir, string name, string url, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "remote", "set-url", name, url }, ct);
    }

    /// <summary>当前分支名。</summary>
    public async Task<string?> CurrentBranchAsync(string workDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync(workDir, new[] { "branch", "--show-current" }, ct);
        return result.Success ? result.Stdout.FirstOrDefault()?.Trim() : null;
    }

    /// <summary>当前分支是否有 upstream。</summary>
    public async Task<bool> HasUpstreamAsync(string workDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync(workDir, new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, ct);
        return result.Success;
    }

    /// <summary>git add --all。</summary>
    public async Task<CommandResult> AddAllAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "add", "--all" }, ct);
    }

    /// <summary>git reset（--mixed，仅取消暂存，保留工作区）。</summary>
    public async Task<CommandResult> ResetToUnstageAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "reset" }, ct);
    }

    /// <summary>git commit -m message。</summary>
    public async Task<CommandResult> CommitAsync(string workDir, string message, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "commit", "-m", message }, ct, TimeSpan.FromSeconds(120));
    }

    /// <summary>git push（已配置 upstream）。</summary>
    public async Task<CommandResult> PushAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "push" }, ct, TimeSpan.FromMinutes(5));
    }

    /// <summary>git push -u origin &lt;branch&gt;（首次发布设置 upstream）。</summary>
    public async Task<CommandResult> PushSetUpstreamAsync(string workDir, string branch, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "push", "-u", "origin", branch }, ct, TimeSpan.FromMinutes(5));
    }

    /// <summary>git pull --ff-only（不自动 merge）。</summary>
    public async Task<CommandResult> PullFfOnlyAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "pull", "--ff-only" }, ct, TimeSpan.FromMinutes(5));
    }

    /// <summary>当前 HEAD 短哈希。</summary>
    public async Task<string?> HeadShortAsync(string workDir, CancellationToken ct = default)
    {
        var result = await RunGitAsync(workDir, new[] { "rev-parse", "--short", "HEAD" }, ct);
        return result.Success ? result.Stdout.FirstOrDefault()?.Trim() : null;
    }
}

/// <summary>
/// Git 配置作用域。
/// </summary>
public enum ConfigScope
{
    Local,
    Global,
    Effective
}