using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;
using SafeGitPublisher.Views;

namespace SafeGitPublisher.Tests;

/// <summary>
/// 对话框冒烟测试：关于/设置/详细报告/最终确认 四个对话框。
/// 由 GuiSmokeHost 在同一 STA 线程、同一 App 实例上调用（Application 为 AppDomain 单例）。
/// 每个对话框：构造 → Show → 布局周期 → 断言 → Close；
/// 捕获 DataBinding 运行时错误与 Dispatcher 未处理异常。
/// </summary>
public static class DialogSmokeTests
{
    /// <summary>顺序执行全部对话框冒烟场景。</summary>
    public static void RunAllDialogs(App app)
    {
        Assert.True(app.Resources.Contains("PageBg"), "App 资源字典未加载");

        RunDialog(app, () => new AboutDialog(), dlg =>
        {
            var text = (TextBlock)dlg.FindName("VersionText");
            Assert.Equal($"Version {AppVersionService.ProductVersion}", text.Text, "关于页版本应来自程序集元数据");
        });

        RunDialog(app, () => new SettingsDialog(new SettingsData
        {
            Settings = new AppSettings(),
            SettingsPath = @"C:\test\settings.json"
        }), dlg =>
        {
            var save = (Button)dlg.FindName("SaveButton");
            Assert.True(save.IsEnabled, "默认阈值 10/50/100 应满足 0 < w < h < b，保存按钮应可用");
        });

        RunDialog(app, () => new ReportDialog(new ReportData
        {
            Context = new PreflightContext { ProjectPath = "C:\\test\\p", Settings = new AppSettings() }
        }));

        // ZERO-05：0 变更时最终确认页的确认按钮必须禁用并给出 Tooltip 说明
        RunDialog(app, () => new ConfirmPublishDialog(new ConfirmPublishData
        {
            RepositoryRoot = @"C:\test\r",
            ProjectPath = @"C:\test\r",
            CommitMessage = "test: 1",
            ChangeCount = 0
        }), dlg =>
        {
            var btn = (Button)dlg.FindName("ConfirmButton");
            Assert.True(!btn.IsEnabled, "0 变更 → 确认按钮必须禁用");
            Assert.Equal("当前没有可提交的变更", btn.ToolTip as string);
        });

        RunDialog(app, () => new ConfirmPublishDialog(new ConfirmPublishData
        {
            RepositoryRoot = @"C:\test\r",
            ProjectPath = @"C:\test\r",
            CommitMessage = "feat: x",
            ChangeCount = 2
        }), dlg =>
        {
            var btn = (Button)dlg.FindName("ConfirmButton");
            Assert.True(btn.IsEnabled, "有变更 → 确认按钮应可用");
            Assert.Null(btn.ToolTip, "可用时不应有禁用提示");
        });
    }

    // ---------- 基础设施 ----------

    private static void RunDialog(App app, Func<Window> create, Action<Window>? verify = null)
    {
        var bindingLines = new ConcurrentBag<string>();
        var unhandled = new ConcurrentBag<Exception>();
        var listener = new GuiStartupSmoke.CollectingTraceListener(bindingLines);

        DispatcherUnhandledExceptionEventHandler unhandledHandler = (_, e) =>
        {
            unhandled.Add(e.Exception);
            e.Handled = true;
        };
        app.DispatcherUnhandledException += unhandledHandler;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var prevLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var dlg = create();
            dlg.Show();
            for (var i = 0; i < 2; i++)
            {
                dlg.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(150);
            }
            if (!dlg.IsLoaded)
            {
                throw new Exception("对话框未完成加载（IsLoaded == false）");
            }
            verify?.Invoke(dlg);
            dlg.Close();
            dlg.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            app.DispatcherUnhandledException -= unhandledHandler;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = prevLevel;
        }

        var errors = bindingLines
            .Where(l => l.Contains("System.Windows.Data Error", StringComparison.Ordinal))
            .ToList();
        if (errors.Count > 0)
        {
            throw new Exception("发现 WPF DataBinding 运行时错误：" +
                string.Join(" | ", errors.Take(5).Select(l => l.Trim())));
        }
        if (!unhandled.IsEmpty)
        {
            throw new Exception("Dispatcher 未处理异常：" +
                string.Join(" | ", unhandled.Take(5).Select(e => e.Message)));
        }
    }
}
