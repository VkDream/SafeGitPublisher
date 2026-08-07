using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// .serena 本机 AI 工具元数据门禁测试（SERENA-01/02）与隔离构建临时目录安全测试。
/// </summary>
public static class SerenaAndIsolationTests
{
    // ---------- SERENA-01：SensitiveFileRules 识别 .serena ----------

    [Test]
    public static void Serena01_SensitiveRule_BlocksProjectYml()
    {
        Assert.True(SensitiveFileRules.IsBlockedPath(".serena/project.yml"),
            ".serena/project.yml 应被敏感规则阻断");
        Assert.True(!string.IsNullOrEmpty(SensitiveFileRules.BlockReason(".serena/project.yml")),
            "BlockReason 不得为空");
        Assert.True(SensitiveFileRules.BlockReason(".serena/project.yml").Contains("AI 工具元数据"),
            $"BlockReason 应说明本机 AI 工具元数据，实际：{SensitiveFileRules.BlockReason(".serena/project.yml")}");
    }

    [Test]
    public static void Serena01b_SensitiveRule_BlocksNestedSerena()
    {
        // 任意层级命中（路径语义）
        Assert.True(SensitiveFileRules.IsBlockedPath("sub/dir/.serena/memory.md"),
            "任意层级 .serena 目录都应被阻断");
    }

    [Test]
    public static void Serena01c_SensitiveRule_BlockedDirectory_NotFileName()
    {
        // 合同：规则必须是目录路径语义，不得误伤文件名/内容含 serena 的合法业务文件
        Assert.True(!SensitiveFileRules.IsBlockedPath("docs/serena-notes.md"),
            "docs/serena-notes.md 不得被阻断（文件名含 serena 不是 .serena 目录）");
        Assert.True(!SensitiveFileRules.IsBlockedPath("src/SerenaParser.cs"),
            "src/SerenaParser.cs 不得被阻断（类名含 Serena 不是 .serena 目录）");
    }

    // ---------- SERENA-02：GitIgnore 推荐规则包含 .serena/ ----------

    [Test]
    public static void Serena02_GitignoreRequired_ContainsSerena()
    {
        Assert.True(GitIgnoreService.RequiredRules.Contains(".serena/"),
            "RequiredRules 必须包含 .serena/");
        Assert.True(GitIgnoreService.RequiredRules.Contains(".claude/"),
            "对照：.claude/ 必须仍存在（同策略）");
        Assert.True(GitIgnoreService.RequiredRules.Contains(".reasonix/"),
            "对照：.reasonix/ 必须仍存在（同策略）");
    }

    [Test]
    public static void Serena02b_ComputeMissing_DetectsMissingSerena()
    {
        var missing = GitIgnoreService.ComputeMissingRules("", GitIgnoreService.RequiredRules);
        Assert.True(missing.Contains(".serena/"),
            "空 .gitignore 时 ComputeMissingRules 应报 .serena/ 缺失");
    }

    // ---------- TempBuildRoot 安全（隔离构建临时目录） ----------

    [Test]
    public static void Tbr01_CreateRoot_IsUniqueUnderBuildRoot()
    {
        var root = TempBuildRoot.CreateRoot();
        Assert.NotNull(root, "CreateRoot 应成功");
        Assert.True(System.IO.Directory.Exists(root!), "创建的目录应存在");
        var fullRoot = System.IO.Path.GetFullPath(TempBuildRoot.BuildRoot);
        var full = System.IO.Path.GetFullPath(root!);
        Assert.True(full.StartsWith(fullRoot.TrimEnd('\\', '/') + "\\", System.StringComparison.OrdinalIgnoreCase),
            "隔离目录必须位于预构建根之下");
        Assert.Equal(32, System.IO.Path.GetFileName(root!.TrimEnd('\\', '/')).Length,
            "目录名应为 32 位 GUID");
        TempBuildRoot.TryCleanup(root);
    }

    [Test]
    public static void Tbr02_Cleanup_RemovesOwnDirOnly()
    {
        var root = TempBuildRoot.CreateRoot();
        Assert.NotNull(root);
        System.IO.File.WriteAllText(System.IO.Path.Combine(root!, "x.txt"), "x");
        Assert.True(TempBuildRoot.TryCleanup(root), "cleanup 应成功");
        Assert.True(!System.IO.Directory.Exists(root!), "cleanup 后目录应删除");
    }

    [Test]
    public static void Tbr03_Cleanup_RejectsUnsafePaths()
    {
        // 绝不误删用户目录：非 GUID 名 / 非隔离根下路径一律拒绝
        Assert.True(!TempBuildRoot.TryCleanup(System.IO.Path.Combine(TempBuildRoot.BuildRoot, "userdir")),
            "非 GUID 目录名不得删除");
        Assert.True(!TempBuildRoot.TryCleanup("C:\\Users\\Public"),
            "隔离根之外的路径不得删除");
        Assert.True(TempBuildRoot.TryCleanup(null), "null 输入视为无需清理（返回 true 不抛）");
        Assert.True(TempBuildRoot.TryCleanup(""), "空字符串视为无需清理（返回 true 不抛）");
    }

    [Test]
    public static void Tbr04_Cleanup_NotExisting_IsOk()
    {
        var root = TempBuildRoot.CreateRoot();
        Assert.NotNull(root);
        Assert.True(TempBuildRoot.TryCleanup(root), "首次清理应成功");
        Assert.True(TempBuildRoot.TryCleanup(root), "重复清理（目录已不存在）应视为成功，不抛异常");
    }
}
