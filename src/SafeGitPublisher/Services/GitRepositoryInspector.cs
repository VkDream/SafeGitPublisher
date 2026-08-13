using System.Text;
using SafeGitPublisher.Models;

namespace SafeGitPublisher.Services;

/// <summary>
/// Git 文本输出的解析器。发布路径优先消费 -z（NUL 分隔）输出，避免空格、引号和转义路径被误解析。
/// 同时保留非 -z 输入兼容，供旧日志和既有单元测试使用。
/// </summary>
public static class GitRepositoryInspector
{
    /// <summary>解析 git status --porcelain=v1 [-z] 输出并标记合并冲突。</summary>
    public static List<GitFileChange> ParseStatusPorcelain(IEnumerable<string> lines)
    {
        var source = lines.ToList();
        return source.Any(line => line.Contains('\0'))
            ? ParseStatusNullSeparated(SplitNullRecords(source))
            : ParseStatusLines(source);
    }

    private static List<GitFileChange> ParseStatusNullSeparated(IReadOnlyList<string> records)
    {
        var changes = new List<GitFileChange>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Length < 3) continue;

            var x = record[0];
            var y = record[1];
            var path = record.Length > 3 ? record[3..] : string.Empty;
            string? oldPath = null;

            // porcelain v1 -z：重命名字段顺序为“新路径\0旧路径\0”，不含 " -> "。
            var effective = x == ' ' ? y : x;
            if (effective is 'R' or 'C' && i + 1 < records.Count)
            {
                oldPath = records[++i];
            }

            changes.Add(CreateChange($"{x}{y}", path, oldPath));
        }
        return changes;
    }

    private static List<GitFileChange> ParseStatusLines(IEnumerable<string> lines)
    {
        var changes = new List<GitFileChange>();
        foreach (var raw in lines)
        {
            if (raw.Length < 3) continue;
            var x = raw[0];
            var y = raw[1];
            var pathPart = raw.Length > 3 ? raw[3..] : string.Empty;
            var effective = x == ' ' ? y : x;
            string path;
            string? oldPath = null;

            if (effective is 'R' or 'C' && pathPart.Contains(" -> ", StringComparison.Ordinal))
            {
                var parts = pathPart.Split(new[] { " -> " }, 2, StringSplitOptions.None);
                oldPath = UnquoteGitPath(parts[0].Trim());
                path = UnquoteGitPath(parts[1].Trim());
            }
            else
            {
                path = UnquoteGitPath(pathPart);
            }

            changes.Add(CreateChange($"{x}{y}", path, oldPath));
        }
        return changes;
    }

    private static GitFileChange CreateChange(string statusCode, string path, string? oldPath)
    {
        var effective = statusCode == "??" ? '?' : statusCode[0] == ' ' ? statusCode[1] : statusCode[0];
        var conflict = IsConflictCode(statusCode);
        return new GitFileChange
        {
            StatusCode = statusCode,
            StatusLabel = conflict ? "冲突" : Label(effective),
            Path = path,
            OldPath = oldPath,
            Risk = conflict ? RiskLevel.Blocked : RiskLevel.Normal
        };
    }

    private static bool IsConflictCode(string code) => code is "AA" or "UU" or "DD" or "AU" or "UA" or "DU" or "UD";

    /// <summary>解析 git status 行是否属于冲突（供后续直接使用）。</summary>
    public static bool LineIsConflict(string statusCode) => IsConflictCode(statusCode);

    /// <summary>解析 git diff --cached --name-status [-z] 输出；重命名固定为 OldPath=旧、Path=新。</summary>
    public static List<GitFileChange> ParseDiffCachedNameStatus(IEnumerable<string> lines)
    {
        var source = lines.ToList();
        if (!source.Any(line => line.Contains('\0'))) return ParseDiffLines(source);

        var records = SplitNullRecords(source);
        var changes = new List<GitFileChange>();
        for (var i = 0; i < records.Count;)
        {
            var statusRecord = records[i++];
            if (string.IsNullOrEmpty(statusRecord)) continue;

            string status;
            string? firstPath = null;
            var tab = statusRecord.IndexOf('\t');
            if (tab >= 0)
            {
                status = statusRecord[..tab];
                firstPath = statusRecord[(tab + 1)..];
            }
            else
            {
                status = statusRecord;
            }

            if (status.Length == 0) continue;
            var code = status[0];
            firstPath ??= i < records.Count ? records[i++] : null;
            if (firstPath == null) continue;

            string path = firstPath;
            string? oldPath = null;
            if (code is 'R' or 'C')
            {
                oldPath = firstPath;
                if (i >= records.Count) continue;
                path = records[i++];
            }

            changes.Add(CreateDiffChange(code, path, oldPath));
        }
        return changes;
    }

    private static List<GitFileChange> ParseDiffLines(IEnumerable<string> lines)
    {
        var changes = new List<GitFileChange>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0) continue;

            var code = parts[0][0];
            if (code is 'R' or 'C')
            {
                if (parts.Length < 3) continue;
                changes.Add(CreateDiffChange(code, UnquoteGitPath(parts[2]), UnquoteGitPath(parts[1])));
            }
            else
            {
                changes.Add(CreateDiffChange(code, UnquoteGitPath(parts[1]), null));
            }
        }
        return changes;
    }

    private static GitFileChange CreateDiffChange(char code, string path, string? oldPath) => new()
    {
        StatusCode = code.ToString(),
        StatusLabel = Label(code),
        Path = path,
        OldPath = oldPath
    };

    private static string Label(char code) => code switch
    {
        'A' => "新增",
        'M' => "修改",
        'D' => "删除",
        'R' => "重命名",
        'C' => "复制",
        'U' => "冲突",
        '?' => "未跟踪",
        _ => code.ToString()
    };

    /// <summary>解析 git ls-files [-z] 输出的跟踪文件列表。</summary>
    public static List<string> ParseLsFiles(IEnumerable<string> lines)
    {
        var source = lines.ToList();
        if (source.Any(line => line.Contains('\0'))) return SplitNullRecords(source);
        return source.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => UnquoteGitPath(line.Trim())).ToList();
    }

    /// <summary>
    /// 解析 git remote -v。仅 origin 满足 HasRemote；fetch/push 独立校验，实际发布目标优先使用 push URL。
    /// FetchUrl/PushUrl 只保存脱敏显示值，精确值仅在程序集内部供发布命令使用。
    /// </summary>
    public static RemoteInfo ParseRemoteV(IEnumerable<string> lines)
    {
        var fetchValues = new List<string>();
        var pushValues = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (!string.Equals(line[..tab].Trim(), "origin", StringComparison.Ordinal)) continue;

            var value = line[(tab + 1)..].Trim();
            if (value.EndsWith("(fetch)", StringComparison.Ordinal)) fetchValues.Add(value[..^7].Trim());
            else if (value.EndsWith("(push)", StringComparison.Ordinal)) pushValues.Add(value[..^6].Trim());
        }

        var fetchDistinct = fetchValues.Distinct(StringComparer.Ordinal).ToList();
        var pushDistinct = pushValues.Distinct(StringComparer.Ordinal).ToList();
        var exactFetch = fetchDistinct.FirstOrDefault();
        var exactPush = pushDistinct.FirstOrDefault();
        var fetchParsed = GitRemoteService.ParseUrl(exactFetch);
        var pushParsed = GitRemoteService.ParseUrl(exactPush);
        var multipleFetch = fetchDistinct.Count > 1;
        var multiplePush = pushDistinct.Count > 1;
        var effective = exactPush != null ? pushParsed : fetchParsed;
        var effectiveMalformed = exactPush != null ? pushParsed.Malformed : fetchParsed.Malformed;

        var reasons = new List<string>();
        if (multipleFetch) reasons.Add("origin 配置了多个不同的 fetch URL。");
        if (multiplePush) reasons.Add("origin 配置了多个不同的 push URL，无法唯一确定发布目标。");
        if (fetchParsed.Malformed) reasons.Add("fetch URL：" + fetchParsed.Reason);
        if (pushParsed.Malformed) reasons.Add("push URL：" + pushParsed.Reason);

        return new RemoteInfo
        {
            HasRemote = fetchDistinct.Count > 0 || pushDistinct.Count > 0,
            FetchUrl = exactFetch == null ? null : GitRemoteService.RedactForDisplay(exactFetch),
            PushUrl = exactPush == null ? null : GitRemoteService.RedactForDisplay(exactPush),
            ExactFetchUrl = exactFetch,
            ExactPushUrl = exactPush,
            Owner = effective.Owner,
            RepoName = effective.Repo,
            FetchIsMalformed = multipleFetch || fetchParsed.Malformed,
            PushIsMalformed = multiplePush || pushParsed.Malformed,
            // fetch/push 分开校验，但 origin 任一方向异常都阻断配置通过；
            // 实际目标与 owner/repo 仍按 push 优先展示。
            IsMalformed = multipleFetch || multiplePush || fetchParsed.Malformed || pushParsed.Malformed || effectiveMalformed,
            MalformedReason = string.Join(" ", reasons),
            SuggestedUrl = effective.Suggested
        };
    }

    private static List<string> SplitNullRecords(IEnumerable<string> lines) => lines
        .SelectMany(line => line.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        .ToList();

    /// <summary>兼容 Git 非 -z 输出的 C 风格双引号转义；无法识别时保留原文而不是猜路径。</summary>
    private static string UnquoteGitPath(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var sb = new StringBuilder(value.Length - 2);
        for (var i = 1; i < value.Length - 1; i++)
        {
            var c = value[i];
            if (c != '\\' || i + 1 >= value.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            c = value[++i];
            sb.Append(c switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                '\\' => '\\',
                '"' => '"',
                _ => c
            });
        }
        return sb.ToString();
    }
}
