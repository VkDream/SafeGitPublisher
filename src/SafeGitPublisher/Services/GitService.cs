using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// 封装对 git.exe 的调用。所有命令统一通过 ProcessRunner 执行，
/// 并使用 -c core.quotepath=false 保证中文文件名按原文输出。
/// </summary>
public sealed class GitService
{
    private const long MaxRawBlobBytes = 100L * 1024 * 1024;
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

    /// <summary>
    /// 执行 Git 并把 stdout 原始字节写入新建文件；不经过 StreamReader 或字符串转换。
    /// 文件上限固定 100 MiB，任何超限/写入失败都返回非成功结果。
    /// </summary>
    private async Task<CommandResult> RunGitRawToFileAsync(string workDir, IReadOnlyList<string> args, string outputPath, CancellationToken ct)
    {
        var fullArgs = new List<string> { "-c", "core.quotepath=false" };
        fullArgs.AddRange(args);
        return await _runner.RunRawStdoutToFileAsync(new ProcessRequest
        {
            FileName = "git",
            Arguments = fullArgs,
            WorkingDirectory = workDir,
            Timeout = TimeSpan.FromSeconds(120)
        }, outputPath, MaxRawBlobBytes, ct);
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

    /// <summary>git status --porcelain=v1 -z，路径使用 NUL 分隔，不受空格或 C 风格转义影响。</summary>
    public async Task<CommandResult> StatusPorcelainAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }, ct);
    }

    /// <summary>git diff --cached --name-status -z -M：已暂存文件。</summary>
    public async Task<CommandResult> DiffCachedNameStatusAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "diff", "--cached", "--name-status", "-z", "-M" }, ct);
    }

    /// <summary>git ls-files -z：已跟踪文件。</summary>
    public async Task<CommandResult> LsFilesAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "ls-files", "-z" }, ct);
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

    /// <summary>读取当前分支命令的完整结果，供安全工作流区分 detached HEAD 与命令失败。</summary>
    public async Task<CommandResult> CurrentBranchResultAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "branch", "--show-current" }, ct);
    }

    /// <summary>当前分支是否有 upstream。</summary>
    public async Task<bool> HasUpstreamAsync(string workDir, CancellationToken ct = default)
    {
        var result = await UpstreamResultAsync(workDir, ct);
        return result.Success;
    }

    /// <summary>读取 upstream 的完整命令结果；退出码非零通常表示尚未配置。</summary>
    public async Task<CommandResult> UpstreamResultAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, ct);
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

    /// <summary>把当前 index 写成 tree 对象，返回的 OID 可用于精确恢复用户操作前暂存状态。</summary>
    public async Task<CommandResult> WriteIndexTreeAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "write-tree" }, ct);
    }

    /// <summary>从指定 tree 精确恢复 index；不修改工作区文件。</summary>
    public async Task<CommandResult> RestoreIndexTreeAsync(string workDir, string treeOid, CancellationToken ct = default)
    {
        if (!IsObjectId(treeOid)) throw new ArgumentException("tree OID 格式无效。", nameof(treeOid));
        return await RunGitAsync(workDir, new[] { "read-tree", treeOid }, ct);
    }

    /// <summary>读取暂存区指定路径的 blob 大小；路径作为 ArgumentList 单项传递，不经过 shell。</summary>
    public async Task<CommandResult> IndexBlobSizeAsync(string workDir, string path, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "cat-file", "-s", IndexObject(path) }, ct);
    }

    /// <summary>把暂存区指定路径的 blob 原始字节写入新建文件；调用方负责 finally 删除。</summary>
    public async Task<CommandResult> WriteIndexBlobToFileAsync(string workDir, string path, string outputPath, CancellationToken ct = default)
    {
        return await RunGitRawToFileAsync(workDir, new[] { "cat-file", "blob", IndexObject(path) }, outputPath, ct);
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

    /// <summary>
    /// 显式推送当前 HEAD 到 origin 的同名分支。remote、源 ref、目标 ref 均固定，
    /// 不受 push.default、remote.push 或 branch.pushRemote 改写。
    /// </summary>
    public async Task<CommandResult> PushExplicitTargetAsync(string workDir, string exactTarget, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exactTarget)) throw new ArgumentException("push 目标不能为空。", nameof(exactTarget));
        if (!IsSafeBranchName(branch))
        {
            throw new ArgumentException("分支名称包含不允许的字符。", nameof(branch));
        }
        return await RunGitAsync(workDir, new[] { "push", exactTarget, $"HEAD:refs/heads/{branch}" }, ct, TimeSpan.FromMinutes(5));
    }

    /// <summary>显式把本地分支 upstream 设为 origin 的同名远端跟踪分支。</summary>
    public async Task<CommandResult> SetOriginUpstreamAsync(string workDir, string branch, CancellationToken ct = default)
    {
        if (!IsSafeBranchName(branch)) throw new ArgumentException("分支名称包含不允许的字符。", nameof(branch));
        return await RunGitAsync(workDir, new[] { "branch", "--set-upstream-to", $"origin/{branch}", branch }, ct);
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

    /// <summary>读取 HEAD 完整 OID；命令失败（包括尚无首次提交）返回 null。</summary>
    public async Task<string?> HeadOidAsync(string workDir, CancellationToken ct = default)
    {
        var result = await HeadOidResultAsync(workDir, ct);
        return result.Success ? result.Stdout.FirstOrDefault()?.Trim() : null;
    }

    /// <summary>读取 HEAD 完整 OID 的完整命令结果，供发布门禁区分首次提交与读取失败。</summary>
    public async Task<CommandResult> HeadOidResultAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "rev-parse", "--verify", "HEAD" }, ct);
    }

    /// <summary>读取当前 HEAD 提交的 tree OID；用于确认 hook 没有在扫描后改写实际提交内容。</summary>
    public async Task<CommandResult> HeadTreeOidResultAsync(string workDir, CancellationToken ct = default)
    {
        return await RunGitAsync(workDir, new[] { "rev-parse", "--verify", "HEAD^{tree}" }, ct);
    }

    /// <summary>
    /// 查询 origin 同名分支当前远端 OID，不更新本地 refs。用于识别本地 tracking ref 过期，
    /// 避免仅扫描 tracking..HEAD 而漏掉需要被重新推送的历史。
    /// </summary>
    public async Task<CommandResult> RemoteBranchOidAsync(string workDir, string exactTarget, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(exactTarget)) throw new ArgumentException("远端目标不能为空。", nameof(exactTarget));
        if (!IsSafeBranchName(branch)) throw new ArgumentException("分支名称包含不允许的字符。", nameof(branch));
        return await RunGitAsync(workDir, new[] { "ls-remote", "--heads", exactTarget, $"refs/heads/{branch}" }, ct, TimeSpan.FromMinutes(2));
    }

    /// <summary>按远端 OID 计算真正待推送提交；远端分支不存在时 remoteOid 传 null，将扫描 HEAD 全历史。</summary>
    public async Task<CommandResult> OutgoingCommitsFromRemoteOidAsync(string workDir, string? remoteOid, CancellationToken ct = default)
    {
        if (remoteOid != null && !IsObjectId(remoteOid)) throw new ArgumentException("远端 OID 格式无效。", nameof(remoteOid));
        var range = remoteOid == null ? "HEAD" : $"{remoteOid}..HEAD";
        return await RunGitAsync(workDir, new[] { "rev-list", "--reverse", range }, ct);
    }

    /// <summary>枚举一组提交可达的全部 blob（OID、大小、路径），使用 NUL 分隔路径。</summary>
    public async Task<CommandResult> ListCommitBlobsAsync(string workDir, string commitOid, CancellationToken ct = default)
    {
        if (!IsObjectId(commitOid)) throw new ArgumentException("commit OID 格式无效。", nameof(commitOid));
        return await RunGitAsync(workDir, new[] { "ls-tree", "-r", "-l", "-z", commitOid }, ct, TimeSpan.FromMinutes(2));
    }

    /// <summary>按 blob OID 把对象原始字节写入新建文件；调用方负责 finally 删除。</summary>
    public async Task<CommandResult> WriteBlobObjectToFileAsync(string workDir, string blobOid, string outputPath, CancellationToken ct = default)
    {
        if (!IsObjectId(blobOid)) throw new ArgumentException("blob OID 格式无效。", nameof(blobOid));
        return await RunGitRawToFileAsync(workDir, new[] { "cat-file", "blob", blobOid }, outputPath, ct);
    }

    private static string IndexObject(string path) => ":" + path.Replace('\\', '/');

    private static bool IsSafeBranchName(string branch) =>
        !string.IsNullOrWhiteSpace(branch) &&
        branch.IndexOfAny(new[] { '\0', '\r', '\n', ' ', '~', '^', ':', '?', '*', '[', '\\' }) < 0 &&
        !branch.StartsWith("-", StringComparison.Ordinal) &&
        !branch.StartsWith("/", StringComparison.Ordinal) &&
        !branch.EndsWith("/", StringComparison.Ordinal) &&
        !branch.EndsWith(".", StringComparison.Ordinal) &&
        !branch.Contains("..", StringComparison.Ordinal) &&
        !branch.Contains("@{", StringComparison.Ordinal) &&
        !branch.Contains("//", StringComparison.Ordinal) &&
        !branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectId(string value) =>
        value.Length is >= 40 and <= 64 && value.All(Uri.IsHexDigit);

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
