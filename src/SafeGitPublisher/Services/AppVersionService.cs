using System.Reflection;

namespace SafeGitPublisher.Services;

/// <summary>
/// 应用版本信息服务：统一从程序集元数据读取版本，禁止在 ViewModel/XAML 硬编码。
/// </summary>
public static class AppVersionService
{
    private static readonly Assembly Assembly = typeof(AppVersionService).Assembly;

    /// <summary>InformationalVersion（例如 "1.0.0"）。</summary>
    public static string ProductVersion =>
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0";

    /// <summary>AssemblyVersion（例如 "1.0.0.0"）。</summary>
    public static string AssemblyVersion => Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

    /// <summary>FileVersion（例如 "1.0.0.0"）。</summary>
    public static string FileVersion =>
        Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "1.0.0.0";

    /// <summary>界面显示用（例如 "v1.0.0"）。</summary>
    public static string DisplayVersion => $"v{ProductVersion}";
}
