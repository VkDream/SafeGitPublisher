using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// Git 文本输出的解析器。全部为纯静态逻辑，便于单元测试。
/// </summary>
public static class GitRepositoryInspector
{
    /// <summary>
    /// 解析 git status --porcelain=v1 输出（未加 -z）。
    /// 每行格式：XY 空格 路径；重命名时含 " -> "。
    /// 同时检测合并冲突（AA/UU/DD/AU/UA/DU/UD）并标记风险。
    /// </summary>
    public static List<GitFileChange> ParseStatusPorcelain(IEnumerable<string> lines)
    {
        var changes = new List<GitFileChange>();

        foreach (var raw in lines)
        {
            var line = raw;
            if (line.Length < 3) continue;

            var x = line[0];
            var y = line[1];
            var pathPart = line.Length > 3 ? line[3..] : string.Empty;

            var isUntracked = x == '?' && y == '?';

            string statusCode;
            string label;
            string path;
            string? oldPath = null;

            if (isUntracked)
            {
                statusCode = "??";
                label = "未跟踪";
                path = pathPart;
            }
            else
            {
                statusCode = $"{x}{y}";
                var xIsSpace = x == ' ';
                var xCode = xIsSpace ? y : x;
                label = xCode switch
                {
                    'A' => "新增",
                    'M' => "修改",
                    'D' => "删除",
                    'R' => "重命名",
                    'C' => "复制",
                    'U' => "冲突",
                    '?' => "未跟踪",
                    _ => xCode.ToString()
                };

                path = pathPart;
                if (xCode is 'R' or 'C' && path.Contains(" -> ", StringComparison.Ordinal))
                {
                    // 重命名格式：old -> new
                    var parts = path.Split(new[] { " -> " }, StringSplitOptions.None);
                    oldPath = parts[0].Trim();
                    path = parts[1].Trim();
                }
            }

            var change = new GitFileChange
            {
                StatusCode = statusCode,
                StatusLabel = label,
                Path = path,
                OldPath = oldPath
            };

            if (IsConflictCode(statusCode))
            {
                change.Risk = RiskLevel.Blocked;
                change.StatusLabel = "冲突";
            }

            changes.Add(change);
        }

        return changes;
    }

    private static bool IsConflictCode(string code)
    {
        return code is "AA" or "UU" or "DD" or "AU" or "UA" or "DU" or "UD";
    }

    /// <summary>解析 git status 行是否属于冲突（供后续直接使用）。</summary>
    public static bool LineIsConflict(string statusCode) => IsConflictCode(statusCode);

    /// <summary>
    /// 解析 git diff --cached --name-status 输出。
    /// 格式：X\tpath     或 R\told\tnew
    /// </summary>
    public static List<GitFileChange> ParseDiffCachedNameStatus(IEnumerable<string> lines)
    {
        var changes = new List<GitFileChange>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2) continue;

            var code = parts[0][0];
            var path = parts[1];
            string? oldPath = parts.Length >= 3 ? parts[2] : null;

            changes.Add(new GitFileChange
            {
                StatusCode = code.ToString(),
                StatusLabel = code switch
                {
                    'A' => "新增",
                    'M' => "修改",
                    'D' => "删除",
                    'R' => "重命名",
                    'C' => "复制",
                    _ => code.ToString()
                },
                Path = path,
                OldPath = oldPath
            });
        }
        return changes;
    }

    /// <summary>解析 git ls-files 输出的跟踪文件列表（去掉空行）。</summary>
    public static List<string> ParseLsFiles(IEnumerable<string> lines)
    {
        return lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
    }

    /// <summary>解析 git remote -v 输出，识别 origin 的 fetch/push URL。</summary>
    public static RemoteInfo ParseRemoteV(IEnumerable<string> lines)
    {
        string? fetch = null;
        string? push = null;
        var hasAny = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            hasAny = true;

            // 形如：origin\thttps://github.com/o/r.git (fetch)
            var mid = trimmed.IndexOf('\t');
            if (mid < 0) continue;
            var url = trimmed[(mid + 1)..].Trim();
            if (url.EndsWith("(fetch)", StringComparison.Ordinal)) fetch = url[..^7].Trim();
            else if (url.EndsWith("(push)", StringComparison.Ordinal)) push = url[..^6].Trim();
            else fetch ??= url;
        }

        var urlToParse = fetch ?? push;
        var (owner, repo, malformed, reason, suggested) = GitRemoteService.ParseUrl(urlToParse);

        return new RemoteInfo
        {
            HasRemote = hasAny,
            FetchUrl = fetch,
            PushUrl = push,
            Owner = owner,
            RepoName = repo,
            IsMalformed = malformed,
            MalformedReason = reason,
            SuggestedUrl = suggested
        };
    }
}