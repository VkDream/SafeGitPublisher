using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// Git 作者身份服务：读取 local/global 配置，与推荐身份比对，并提供一键修正（仅 local 范围）。
/// </summary>
public sealed class GitIdentityService
{
    private readonly GitService _git;

    public GitIdentityService(GitService git)
    {
        _git = git;
    }

    /// <summary>
    /// 读取仓库作者身份。优先 local，其次 effective（global），都没有则为 NotSet。
    /// </summary>
    public async Task<GitIdentityInfo> GetIdentityAsync(string repoRoot, string recommendedName, string recommendedEmail, CancellationToken ct = default)
    {
        var localName = await _git.ConfigGetAsync(repoRoot, "user.name", ConfigScope.Local, ct);
        var localEmail = await _git.ConfigGetAsync(repoRoot, "user.email", ConfigScope.Local, ct);
        var globalName = await _git.ConfigGetAsync(repoRoot, "user.name", ConfigScope.Global, ct);
        var globalEmail = await _git.ConfigGetAsync(repoRoot, "user.email", ConfigScope.Global, ct);

        var nameSource = localName != null ? ConfigSource.Local : globalName != null ? ConfigSource.Global : ConfigSource.NotSet;
        var emailSource = localEmail != null ? ConfigSource.Local : globalEmail != null ? ConfigSource.Global : ConfigSource.NotSet;

        return new GitIdentityInfo
        {
            Name = localName ?? globalName,
            NameSource = nameSource,
            Email = localEmail ?? globalEmail,
            EmailSource = emailSource,
            RecommendedName = recommendedName,
            RecommendedEmail = recommendedEmail
        };
    }

    /// <summary>
    /// 将推荐身份写入 repository local config（绝不写 global）。
    /// </summary>
    public async Task<(bool Ok, string Error)> ApplyRecommendedAsync(string repoRoot, string name, string email, CancellationToken ct = default)
    {
        var r1 = await _git.ConfigSetLocalAsync(repoRoot, "user.name", name, ct);
        if (!r1.Success)
        {
            return (false, $"设置 user.name 失败：{GitRemoteService.RedactOutput(r1.StdErrText)}");
        }
        var r2 = await _git.ConfigSetLocalAsync(repoRoot, "user.email", email, ct);
        if (!r2.Success)
        {
            return (false, $"设置 user.email 失败：{GitRemoteService.RedactOutput(r2.StdErrText)}");
        }
        return (true, string.Empty);
    }
}
