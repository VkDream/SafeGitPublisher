namespace SafeGitPublisher.Models;

/// <summary>
/// 文件风险等级（用于变更列表显示）。
/// </summary>
public enum RiskLevel
{
    Normal,
    Warning,
    Blocked
}

/// <summary>
/// Git 工作区中的一个文件变更（来自 git status --porcelain）。
/// </summary>
public sealed class GitFileChange
{
    /// <summary>porcelain 状态码：A/M/D/R/C/? 。</summary>
    public required string StatusCode { get; init; }

    /// <summary>友好状态名（中文）：新增/修改/删除/重命名/未跟踪。</summary>
    public required string StatusLabel { get; set; }

    /// <summary>相对仓库根目录的路径。</summary>
    public required string Path { get; init; }

    /// <summary>重命名时的旧路径。</summary>
    public string? OldPath { get; init; }

    /// <summary>文件大小（字节）。不存在或未知时为 -1。</summary>
    public long SizeBytes { get; set; } = -1;

    public string SizeDisplay => SizeBytes < 0 ? "-" : FormatSize(SizeBytes);

    /// <summary>风险等级（由各扫描器填充）。</summary>
    public RiskLevel Risk { get; set; } = RiskLevel.Normal;

    /// <summary>是否属于合并冲突状态（AA/UU/DD/AU/UA/DU/UD）。</summary>
    public bool IsConflict => StatusCode is "AA" or "UU" or "DD" or "AU" or "UA" or "DU" or "UD";

    /// <summary>是否已暂存（状态码非空格且非 ?）。</summary>
    public bool IsStaged => StatusCode.Length >= 2 && StatusCode[0] != ' ' && StatusCode[0] != '?';

    /// <summary>是否未跟踪。</summary>
    public bool IsUntracked => StatusCode == "??";

    /// <summary>
    /// 是否属于不会向下一次提交新增文件内容的删除状态。
    /// porcelain v1 使用两列状态："D " 表示暂存区删除，" D" 表示工作区删除；
    /// diff --cached --name-status 则使用单字符 "D"。合并冲突（DD/DU/UD 等）必须由冲突门处理，
    /// 不能当作普通删除跳过安全检查。
    /// </summary>
    public bool IsDeletedLike()
    {
        if (IsConflict || StatusCode.Length == 0) return false;
        return StatusCode[0] == 'D' || (StatusCode.Length >= 2 && StatusCode[1] == 'D');
    }

    /// <summary>兼容现有显示/调用方的删除属性，语义与 IsDeletedLike 保持一致。</summary>
    public bool IsDeleted => IsDeletedLike();

    /// <summary>是否为常见图片扩展名。</summary>
    public bool IsImage => IsImagePath(Path);

    public static bool IsImagePath(string path)
    {
        var ext = System.IO.Path.GetExtension(path)?.ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        var mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F2} MB";
        var gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }
}
