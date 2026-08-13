using SafeGitPublisher.Models;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Tests;

/// <summary>
/// 仓库总体积门禁（V1.0.1 新增）纯函数与集成合同测试。
/// 设计动机：单文件阈值无法拦截"大量中等文件合计超大"（真实现场：113 张 14MB 位图共 1.6GB），
/// 需要独立的总量门禁；预检（第 13 项 repo_size）与最终发布门禁使用同一合同。
/// </summary>
public static class RepoSizeGateTests
{
    private static GitFileChange Change(string path, long sizeBytes, string statusCode = "??")
        => new() { StatusCode = statusCode, StatusLabel = "未跟踪", Path = path, SizeBytes = sizeBytes };

    private static long Mb(double mb) => (long)(mb * 1024 * 1024);

    // ---------- REPO-SIZE-01：总量分类阈值 ----------

    [Test]
    public static void REPOSIZE01_ClassifyTotalSize_Thresholds()
    {
        // 正常：低于警告阈值
        var (r1, _) = LargeFileScanner.ClassifyTotalSize(Mb(100), warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Normal, r1, "100MB < 500MB 应 Normal");

        // 边界：等于警告阈值不警告（严格大于才触发）
        var (r2, _) = LargeFileScanner.ClassifyTotalSize(Mb(500), warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Normal, r2, "恰好 500MB 不应触发警告");

        // 警告：超过警告阈值但不超过阻断阈值
        var (r3, d3) = LargeFileScanner.ClassifyTotalSize(Mb(600), warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Warning, r3, "600MB > 500MB 应 Warning");
        Assert.Contains(d3, "500", "警告文案应含阈值");

        // 阻断：超过阻断阈值
        var (r4, d4) = LargeFileScanner.ClassifyTotalSize(Mb(1600), warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Blocked, r4, "1.6GB > 1000MB 应 Blocked");
        Assert.Contains(d4, ".gitignore", "阻断文案应指导 .gitignore 排除");

        // 最终门禁用法：warningMB=MaxValue 时只有 Normal/Blocked 两种结果
        var (r5, _) = LargeFileScanner.ClassifyTotalSize(Mb(600), double.MaxValue, blockingMB: 1000);
        Assert.Equal(RiskLevel.Normal, r5, "最终门禁模式下 600MB 不应 Warning");
        var (r6, _) = LargeFileScanner.ClassifyTotalSize(Mb(1001), double.MaxValue, blockingMB: 1000);
        Assert.Equal(RiskLevel.Blocked, r6, "最终门禁模式下 1001MB 应 Blocked");
    }

    // ---------- REPO-SIZE-02：总量求和 ----------

    [Test]
    public static void REPOSIZE02_ComputeTotalBytes()
    {
        var changes = new List<GitFileChange>
        {
            Change("a.cpp", Mb(1)),
            Change("b.bmp", Mb(14)),
            Change("c.bin", Mb(100)),
            Change("deleted.txt", Mb(50), statusCode: "D "),  // 删除不计入
            Change("unknown.txt", -1),                          // 未知大小不计入
        };
        var total = LargeFileScanner.ComputeTotalBytes(changes);
        Assert.Equal(Mb(115), total, "总量应跳过删除与未知大小");
    }

    // ---------- REPO-SIZE-03：扩展名 Top 汇总 ----------

    [Test]
    public static void REPOSIZE03_SummarizeByExtension()
    {
        var changes = new List<GitFileChange>
        {
            Change("img/1.bmp", Mb(14)),
            Change("img/2.bmp", Mb(14)),
            Change("bin/halcon.dll", Mb(33)),
            Change("src/main.cpp", Mb(1)),
            Change("noext", Mb(2)),
        };
        var text = LargeFileScanner.SummarizeByExtension(changes, top: 3);
        var lines = text.Split('\n');
        Assert.Equal(3, lines.Length, "Top 3 应只有 3 行");
        Assert.True(lines[0].Contains(".dll", StringComparison.OrdinalIgnoreCase), "最大体积扩展名应排第一");
        Assert.True(lines[1].Contains(".bmp", StringComparison.OrdinalIgnoreCase) && lines[1].Contains("×2"), "bmp 应汇总数量");
        Assert.Contains(text, "(无扩展名)", "无扩展名文件应有明确归类");
    }

    // ---------- REPO-SIZE-04：真实现场复现（113 张 14MB 位图 = 1.58GB） ----------

    [Test]
    public static void REPOSIZE04_RealWorldIncident_113BmpImages()
    {
        // 复现 2026-08-13 ReadCode 真实现场：805 个变更中 113 张 14.42MB 位图，
        // 单文件检查只给 Warning（每张 14MB < 50MB 高危线），但合计 1.59GB 必须被总量门禁拦截。
        var changes = new List<GitFileChange>();
        for (var i = 0; i < 113; i++)
        {
            changes.Add(Change($"runtime/images/扫描图片/{i}.bmp", Mb(14.42)));
        }
        changes.Add(Change("BarcodeScanTester.sdf", Mb(14.88)));
        changes.Add(Change("runtime/halcon.dll", Mb(33.64)));

        var total = LargeFileScanner.ComputeTotalBytes(changes);
        var (risk, desc) = LargeFileScanner.ClassifyTotalSize(total, warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Blocked, risk, "真实事故现场（约 1.68GB）必须被总量门禁阻断");
        Assert.Contains(desc, "1000", "阻断文案应含阻断阈值");

        // 排除图片和运行时后（模拟 .gitignore 生效），剩余源码应放行
        var sourceOnly = new List<GitFileChange>
        {
            Change("BarcodeScanTester.sln", 972),
            Change("BarcodeScanTester.vcxproj", Mb(0.009)),
            Change("main.cpp", Mb(0.01)),
        };
        var (risk2, _) = LargeFileScanner.ClassifyTotalSize(
            LargeFileScanner.ComputeTotalBytes(sourceOnly), warningMB: 500, blockingMB: 1000);
        Assert.Equal(RiskLevel.Normal, risk2, "排除构建产物后的纯源码应放行");
    }

    // ---------- REPO-SIZE-05：设置项默认值合同 ----------

    [Test]
    public static void REPOSIZE05_SettingsDefaults()
    {
        var settings = new AppSettings();
        Assert.Equal(500.0, settings.RepoSizeWarningMB, "默认警告阈值 500MB");
        Assert.Equal(1000.0, settings.RepoSizeBlockingMB, "默认阻断阈值 1000MB");

        // 序列化/反序列化保留（设置持久化合同）
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(500.0, back.RepoSizeWarningMB, "序列化后警告阈值保留");
        Assert.Equal(1000.0, back.RepoSizeBlockingMB, "序列化后阻断阈值保留");
    }

    // ---------- REPO-SIZE-06：Gate 文案映射 ----------

    [Test]
    public static void REPOSIZE06_GateReasonMapping()
    {
        var report = new PreflightReport();
        report.Checks.Add(new PreflightCheck
        {
            Id = "repo_size",
            Name = "仓库总体积",
            Status = CheckStatus.Blocked,
            Summary = "待提交总体积超限",
            BlocksCommit = true,
            BlocksPush = true
        });

        var gate = PublishGateEvaluator.Evaluate(
            report, committableChanges: 5, commitMessage: "feat: x",
            busy: false, newImageCount: 0, imageConfirmed: false, requireImageConfirmation: true);
        Assert.True(!gate.CanCommit, "repo_size 阻断应禁止提交");
        Assert.True(!gate.CanPush, "repo_size 阻断应禁止推送");
        Assert.Contains(gate.CommitReason, "阻断", "提交禁用原因应说明存在阻断项");
    }
}
