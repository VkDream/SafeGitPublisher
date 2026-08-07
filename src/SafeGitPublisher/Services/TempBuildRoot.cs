using System.IO;

namespace SafeGitPublisher.Services;

/// <summary>
/// 隔离构建临时目录管理（self-host 缺陷修复）。
/// 每次 Preflight Build 使用独立唯一临时根：%TEMP%\SafeGitPublisher\PreflightBuild\&lt;GUID&gt;，
/// 配合 dotnet build --artifacts-path 将全部构建输出（bin/obj/apphost/exe）隔离到该目录，
/// 绝不触碰仓库源码目录中的正式 bin/obj，也绝不覆盖当前正在运行的 SafeGitPublisher.exe。
/// 仅删除"由 SafeGitPublisher 自己创建"的本次构建目录，绝不递归删除用户目录。
/// </summary>
public static class TempBuildRoot
{
    /// <summary>隔离构建根目录（所有 GUID 子目录的父目录）。</summary>
    public static string BuildRoot =>
        Path.Combine(Path.GetTempPath(), "SafeGitPublisher", "PreflightBuild");

    /// <summary>
    /// 创建本次构建的独立唯一临时目录。
    /// </summary>
    /// <returns>新建目录完整路径；失败时返回 null（调用方应明确报错而非降级为传统构建）。</returns>
    public static string? CreateRoot()
    {
        try
        {
            var root = Path.Combine(BuildRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// best-effort 清理本次构建目录。
    /// 安全护栏：仅当目录完整位于 BuildRoot 之下且目录名是 32 位 GUID 形态时才删除；
    /// 任何失败（文件占用、权限等）静默返回 false，绝不抛出、绝不误删其他目录。
    /// </summary>
    /// <param name="root">由 CreateRoot 返回的目录。</param>
    /// <returns>是否已成功删除；false 表示清理失败（不影响构建结果判定）。</returns>
    public static bool TryCleanup(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return true;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(root);
        }
        catch
        {
            return false;
        }

        // 护栏 1：必须位于隔离根之下
        var buildRootFull = Path.GetFullPath(BuildRoot).TrimEnd('\\', '/');
        var rootDir = Path.GetDirectoryName(normalized.TrimEnd('\\', '/')) ?? string.Empty;
        if (!string.Equals(rootDir.TrimEnd('\\', '/'), buildRootFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 护栏 2：目录名必须是 GUID（32 位十六进制），防止误删任何用户命名目录
        var name = Path.GetFileName(normalized.TrimEnd('\\', '/'));
        if (name.Length != 32 || !name.All(Uri.IsHexDigit))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(normalized))
            {
                Directory.Delete(normalized, recursive: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
