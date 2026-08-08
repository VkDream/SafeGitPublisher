using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.E2E;

/// <summary>
/// SGP-UI-001 修复的端到端验证：.gitignore 预览 → 应用/取消 的真实文件语义。
/// UI-003：Apply 后写入预期 .gitignore；
/// UI-004：Cancel / Close 不写文件；
/// UI-005：原文件已有内容时只追加缺失规则，不覆盖。
/// 通过真实 git 临时仓库 + MainViewModel.GenerateGitignoreAsync
/// + GitignorePreviewRequested 事件（等价于真实 GUI 对话框的确认/取消）验证。
/// </summary>
public static class GitignorePreviewScenarios
{
    private static readonly ProcessRunner Runner = new();
    private static readonly GitService Git = new(Runner);

    private static async Task<string> NewRepo(string name, bool generateGitignore)
    {
        var dir = Path.Combine(Program.RootDir, name);
        Directory.CreateDirectory(dir);
        Program.Track(dir);
        var init = await Git.InitAsync(dir);
        E2EAssert.True(init.Success, $"git init 失败：{init.StdErrText}");
        var (ok, err) = await new GitIdentityService(Git).ApplyRecommendedAsync(dir, "VkDream", "312913839+VkDream@users.noreply.github.com");
        E2EAssert.True(ok, $"设置本地身份失败：{err}");
        if (generateGitignore)
        {
            await GitIgnoreService.ApplyAsync(dir);
        }
        return dir;
    }

    private static void Write(string repo, string rel, string content)
    {
        var full = Path.Combine(repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
    }

    /// <summary>真实完整路径：RunChecksAsync 建立上下文 → 预览钩子 = 用户在 GUI 对话框上的确认/取消 → 写/不写文件。</summary>
    private static async Task<MainViewModel> RunViewModelAsync(string repo, Func<GitignorePreviewData, Task<bool>> onPreview)
    {
        var vm = new MainViewModel();
        vm.GitignorePreviewRequested += onPreview;
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();
        return vm;
    }

    // ---------- UI-003：Apply 后写入预期 .gitignore ----------
    [E2ETest]
    public static async Task UI003_Apply_WritesExpectedGitignore()
    {
        var repo = await NewRepo("ui003_apply", generateGitignore: false);
        var previewCalled = false;
        var vm = await RunViewModelAsync(repo, _ =>
        {
            previewCalled = true;
            return Task.FromResult(true); // 等价于用户点击"应用"
        });

        await vm.GenerateGitignoreAsync();

        E2EAssert.True(previewCalled, "预览对话框必须被展示（钩子被调用）");
        var path = Path.Combine(repo, ".gitignore");
        E2EAssert.True(File.Exists(path), "应用后必须创建 .gitignore");
        var content = await File.ReadAllTextAsync(path);
        foreach (var rule in GitIgnoreService.RequiredRules)
        {
            E2EAssert.True(content.Contains(rule, StringComparison.Ordinal),
                $"写入内容必须包含缺失规则：{rule}");
        }
        E2EAssert.True(content.Contains("SafeGitPublisher 推荐规则", StringComparison.Ordinal),
            "写入内容应包含推荐规则小节标记");

        // 所有推荐规则应恰好出现一次（无重复）
        foreach (var rule in GitIgnoreService.RequiredRules)
        {
            var cnt = Regex.Matches(content, "^" + Regex.Escape(rule) + "$", RegexOptions.Multiline).Count;
            E2EAssert.True(cnt == 1, $"规则 {rule} 应恰好出现 1 次，实际 {cnt}");
        }
    }

    // ---------- UI-004：Cancel / Close 不写文件 ----------
    [E2ETest]
    public static async Task UI004_CancelNoPreview_DoesNotWrite()
    {
        var repo = await NewRepo("ui004_cancel", generateGitignore: false);
        var previewCalled = false;
        var vm = await RunViewModelAsync(repo, _ =>
        {
            previewCalled = true;
            return Task.FromResult(false); // 等价于对话框取消 / 右上角 X
        });

        await vm.GenerateGitignoreAsync();

        E2EAssert.True(previewCalled, "预览对话框应被展示");
        E2EAssert.True(!File.Exists(Path.Combine(repo, ".gitignore")),
            "取消后不得创建 .gitignore（不写文件）");
    }

    // ---------- UI-005：原文件已有内容时只追加缺失规则，不覆盖 ----------
    [E2ETest]
    public static async Task UI005_ExistingContent_AppendOnly()
    {
        var repo = await NewRepo("ui005_append", generateGitignore: false);
        var custom = "# 用户自定义规则（必须原样保留）\nbin/\n*.db\ncustom-keep/\n";
        Write(repo, ".gitignore", custom);

        var vm = await RunViewModelAsync(repo, d => Task.FromResult(true));
        await vm.GenerateGitignoreAsync();

        var content = await File.ReadAllTextAsync(Path.Combine(repo, ".gitignore"));

        // 用户原有每一行必须被原样保留
        E2EAssert.True(content.Contains("# 用户自定义规则（必须原样保留）", StringComparison.Ordinal),
            "原有注释必须原样保留");
        E2EAssert.True(content.Contains("custom-keep/", StringComparison.Ordinal),
            "原有自定义规则必须原样保留");

        // 原有已存在的规则行只出现一次（只追加缺失规则，不重复追加）
        var binCount = Regex.Matches(content, "^bin/$", RegexOptions.Multiline).Count;
        E2EAssert.True(binCount == 1, $"已有 bin/ 不得重复出现，实际 {binCount} 次");
        var dbCount = Regex.Matches(content, "^[*][.]db$", RegexOptions.Multiline).Count;
        E2EAssert.True(dbCount == 1, $"已有 *.db 不得重复出现，实际 {dbCount} 次");

        // 追加闭包：应用后所有推荐规则应已覆盖且无重复
        foreach (var rule in GitIgnoreService.RequiredRules)
        {
            var cnt = Regex.Matches(content, "^" + Regex.Escape(rule) + "$", RegexOptions.Multiline).Count;
            E2EAssert.True(cnt == 1, $"规则 {rule} 应恰好出现 1 次，实际 {cnt}");
        }
        E2EAssert.True(GitIgnoreService.ComputeMissingRules(content, GitIgnoreService.RequiredRules).Count == 0,
            "应用后所有推荐规则应已覆盖");
    }

    // ---------- SGP-UI-002：未配置 origin（Warning+BlocksPush）→ PUBLISH BLOCKED 文案不得为 0 项阻断 ----------
    [E2ETest]
    public static async Task BUG002_NoOrigin_WarningBlocksPush_BannerDetailNeverZero()
    {
        var repo = await NewRepo("bug002_no_origin", generateGitignore: true);
        Write(repo, "feature.txt", "v1");

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();

        E2EAssert.Equal("PUBLISH BLOCKED", vm.PublishBannerTitle,
            "未配置 origin 且有待发布变更 → Banner 必须为 PUBLISH BLOCKED（Push 被硬性禁止）");
        E2EAssert.True(!vm.CanPush, "未配置 origin → CanPush 必须 false（安全语义不变）");
        E2EAssert.True(!vm.PublishBannerDetail.Contains("0 项", StringComparison.Ordinal),
            $"Detail 绝不能出现 0 项：{vm.PublishBannerDetail}");
        E2EAssert.True(vm.PublishBannerDetail.Contains("需处理问题", StringComparison.Ordinal),
            $"Detail 应提示需处理问题：{vm.PublishBannerDetail}");
    }

    // ---------- SGP-UI-002 对照：真实 Blocked（Secret）→ Detail 显示 N 项阻断问题 ----------
    [E2ETest]
    public static async Task BUG002_RealBlocked_DetailShowsN()
    {
        var repo = await NewRepo("bug002_blocked", generateGitignore: false);
        // 字符串拆分拼接，避免测试源码自身被 Secret 自扫误报（既有约定）
        Write(repo, "appsettings.json", "{ \"Token\": \"" + "ghp_" + "abcdef1234567890XYZ99\" }\n");

        var vm = new MainViewModel();
        vm.ProjectPath = repo;
        await vm.RunChecksAsync();

        var report = vm.LastContext!.Report;
        E2EAssert.True(report.BlockedCount > 0, "secret 应产生真实 Blocked 状态");
        E2EAssert.Equal("PUBLISH BLOCKED", vm.PublishBannerTitle, "真阻断 → PUBLISH BLOCKED");
        E2EAssert.True(vm.PublishBannerDetail.Contains("项阻断问题", StringComparison.Ordinal),
            $"真实 Blocked 时应显示阻断问题文案：{vm.PublishBannerDetail}，实际 BlockedCount={report.BlockedCount}");
    }
}