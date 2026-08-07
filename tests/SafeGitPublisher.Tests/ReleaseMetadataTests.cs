using System.IO;
using System.Reflection;
using System.Text;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// V1.0.0 发布元数据测试：版本号、应用图标、csproj 声明。
/// 版本必须来自程序集元数据（AppVersionService），测试锁定 1.0.0 / 1.0.0.0。
/// </summary>
public static class ReleaseMetadataTests
{
    /// <summary>仓库根目录（由测试程序集位置上推得出）。</summary>
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(
        Path.GetDirectoryName(typeof(ReleaseMetadataTests).Assembly.Location) ?? string.Empty,
        "..", "..", "..", "..", ".."));

    [Test]
    public static void VersionService_Matches_1_0_0()
    {
        Assert.Equal("1.0.0", AppVersionService.ProductVersion);
        Assert.Equal("1.0.0.0", AppVersionService.AssemblyVersion);
        Assert.Equal("1.0.0.0", AppVersionService.FileVersion);
        Assert.Equal("v1.0.0", AppVersionService.DisplayVersion);
    }

    [Test]
    public static void AssemblyInformationalVersion_Exact()
    {
        var attr = typeof(AppVersionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        Assert.NotNull(attr, "应存在 AssemblyInformationalVersionAttribute");
        Assert.Equal("1.0.0", attr!.InformationalVersion, "InformationalVersion 必须恰好为 1.0.0（不得附加 +commit 后缀）");
    }

    [Test]
    public static void Icon_File_Valid_WithAllSizes()
    {
        var ico = Path.Combine(RepoRoot, "assets", "SafeGitPublisher.ico");
        Assert.True(File.Exists(ico), $"图标文件不存在：{ico}");
        using var fs = File.OpenRead(ico);
        using var br = new BinaryReader(fs);
        var reserved = br.ReadUInt16();
        var type = br.ReadUInt16();
        var count = br.ReadUInt16();
        Assert.Equal(0, reserved, "ICO 头 reserved 应为 0");
        Assert.Equal(1, type, "ICO 头 type 应为 1");
        Assert.Equal(7, count, "应包含 16/24/32/48/64/128/256 共 7 个尺寸");

        var has256 = false;
        for (var i = 0; i < count; i++)
        {
            var w = br.ReadByte();
            var h = br.ReadByte();
            if (w == 0 && h == 0) has256 = true; // 256 编码为 0
            br.BaseStream.Position += 14; // palette/reserved/planes/bpp/bytes/offset
        }
        Assert.True(has256, "必须包含 256x256 尺寸（桌面/任务栏用）");
    }

    [Test]
    public static void Icon_SourcePng_Exists()
    {
        var png = Path.Combine(RepoRoot, "assets", "SafeGitPublisher-source.png");
        Assert.True(File.Exists(png), $"源图不存在：{png}");
    }

    [Test]
    public static void Csproj_DeclaresIconAndVersion()
    {
        var csproj = Path.Combine(RepoRoot, "src", "SafeGitPublisher", "SafeGitPublisher.csproj");
        Assert.True(File.Exists(csproj), $"csproj 不存在：{csproj}");
        var text = File.ReadAllText(csproj, Encoding.UTF8);
        Assert.Contains(text, "SafeGitPublisher.ico", "csproj 必须引用图标文件");
        Assert.Contains(text, "ApplicationIcon", "csproj 必须声明 ApplicationIcon");
        Assert.Contains(text, "<Version>1.0.0</Version>", "Version 应为 1.0.0");
        Assert.Contains(text, "<AssemblyVersion>1.0.0.0</AssemblyVersion>", "AssemblyVersion 应为 1.0.0.0");
        Assert.Contains(text, "<FileVersion>1.0.0.0</FileVersion>", "FileVersion 应为 1.0.0.0");
        Assert.Contains(text, "<InformationalVersion>1.0.0</InformationalVersion>", "InformationalVersion 应为 1.0.0");
    }

    [Test]
    public static void ReleaseExe_HasEmbeddedIcon()
    {
        var exe = Path.Combine(RepoRoot, "src", "SafeGitPublisher", "bin", "Release", "net10.0-windows", "SafeGitPublisher.exe");
        Assert.True(File.Exists(exe), $"Release exe 不存在（需先构建 Release）：{exe}");
        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
        Assert.NotNull(ico, "Release exe 必须携带应用图标（ApplicationIcon）");
        Assert.True(ico!.Width > 0, "exe 图标尺寸异常");
    }
}
