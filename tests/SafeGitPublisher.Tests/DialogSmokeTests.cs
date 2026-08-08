using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SafeGitPublisher.Models;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;
using SafeGitPublisher.Views;

namespace SafeGitPublisher.Tests;

/// <summary>
/// 对话框冒烟测试：关于/设置/详细报告/最终确认/.gitignore 预览 五个对话框。
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

        RunGitignorePreviewScenarios(app);
    }

    // ---------- SGP-UI-001：.gitignore 预览对话框按钮可见性 ----------

    /// <summary>
    /// 在已创建的预览对话框中断言"取消/应用"按钮真实可见、可用且位于窗口可视区域内。
    /// </summary>
    private static void AssertPreviewButtonsVisible(Window dlgWindow, string scenario)
    {
        var dlg = (GitignorePreviewDialog)dlgWindow;
        var cancel = (Button)dlg.FindName("CancelButton");
        var apply = (Button)dlg.FindName("ApplyButton");
        Assert.NotNull(cancel, $"{scenario}：找不到取消按钮");
        Assert.NotNull(apply, $"{scenario}：找不到应用按钮");

        Assert.True(cancel.IsVisible, $"{scenario}：取消按钮必须可见");
        Assert.True(cancel.IsEnabled, $"{scenario}：取消按钮必须可用（IsEnabled=true）");
        Assert.True(apply.IsVisible, $"{scenario}：应用按钮必须可见");
        Assert.True(apply.IsEnabled, $"{scenario}：应用按钮必须可用（IsEnabled=true）");

        // 关键：按钮必须位于窗口可视区域内（不被内容控件挤出可视区域）
        var cancelBottom = cancel.TranslatePoint(new Point(0, cancel.ActualHeight), dlg);
        var applyBottom = apply.TranslatePoint(new Point(0, apply.ActualHeight), dlg);
        Assert.True(cancelBottom.Y <= dlg.ActualHeight + 0.5,
            $"{scenario}：取消按钮底部超出窗口可视区域（y={cancelBottom.Y:F1} > 窗口高={dlg.ActualHeight:F1}）");
        Assert.True(applyBottom.Y <= dlg.ActualHeight + 0.5,
            $"{scenario}：应用按钮底部超出窗口可视区域（y={applyBottom.Y:F1} > 窗口高={dlg.ActualHeight:F1}）");
        Assert.True(applyBottom.X < dlg.ActualWidth - 0.5,
            $"{scenario}：应用按钮超出窗口右边界（x={applyBottom.X:F1} >= 窗口宽={dlg.ActualWidth:F1}）");
    }

    /// <summary>
    /// UI-001：默认尺寸创建后，取消/应用按钮在视觉树中可见且 IsEnabled=true。
    /// UI-002：内容很多时 TextBox 可滚动且按钮仍处于可见区域。
    /// UI-003/UI-004（对话框层）：点击应用 → Confirmed=true；点击取消 / 关闭 → Confirmed=false。
    /// </summary>
    private static void RunGitignorePreviewScenarios(App app)
    {
        var data = new GitignorePreviewData { RepoRoot = @"C:\test\r", NewContent = "bin/\nobj/\n" };
        RunDialog(app, () => new GitignorePreviewDialog(data), dlg =>
        {
            // UI-001：默认尺寸 + 普通内容
            AssertPreviewButtonsVisible(dlg, "UI-001 默认尺寸");
            var box = (TextBox)dlg.FindName("ContentBox");
            Assert.True(box.MinHeight >= 200, "UI-001：内容区应保证最小高度，防止挤压按钮行");
        });

        // UI-002：大量内容 → 内容可滚动 + 按钮仍可见
        var sb = new StringBuilder();
        for (var i = 0; i < 3000; i++) sb.AppendLine($"rule_{i}/");
        var longData = new GitignorePreviewData { RepoRoot = @"C:\test\r", NewContent = sb.ToString() };
        RunDialog(app, () => new GitignorePreviewDialog(longData), dlg =>
        {
            var box = (TextBox)dlg.FindName("ContentBox");
            var scroll = FindDescendant<ScrollViewer>(box);
            Assert.NotNull(scroll, "UI-002：TextBox 内部应存在 ScrollViewer");
            Assert.True(scroll!.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "UI-002：内容多时垂直滚动条必须出现（内容可滚动）");
            Assert.True(box.ActualHeight > 150, $"UI-002：内容区应保持可读高度（ActualHeight={box.ActualHeight:F1}）");

            // 内容必须真实超出视口（可滚动性判定，不依赖布局时序）
            Assert.True(scroll.ExtentHeight > scroll.ViewportHeight + 1,
                $"UI-002：内容必须超出视口（Extent={scroll.ExtentHeight:F1} > Viewport={scroll.ViewportHeight:F1}）");
            AssertPreviewButtonsVisible(dlg, "UI-002 大量内容");
        });

        // UI-003（对话框层）：点击"应用" → Confirmed=true（上层据此写入文件）
        var applyData = new GitignorePreviewData { RepoRoot = @"C:\test\r", NewContent = "bin/\n" };
        RunDialog(app, () => new GitignorePreviewDialog(applyData), dlg =>
        {
            var apply = (Button)dlg.FindName("ApplyButton");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(applyData.Confirmed, "UI-003：点击应用后 Confirmed 必须为 true（上层据此写入文件）");
        }, modal: true);

        // UI-004a（对话框层）：点击"取消" → Confirmed=false，DialogResult=false
        var cancelData = new GitignorePreviewData { RepoRoot = @"C:\test\r", NewContent = "bin/\n" };
        RunDialog(app, () => new GitignorePreviewDialog(cancelData), dlg =>
        {
            var cancel = (Button)dlg.FindName("CancelButton");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(!cancelData.Confirmed, "UI-004：点击取消后 Confirmed 必须为 false（不得写文件）");
        }, modal: true);

        // UI-004b（对话框层）：右上角 X 等价取消 → Confirmed 保持 false
        var closeData = new GitignorePreviewData { RepoRoot = @"C:\test\r", NewContent = "bin/\n" };
        RunDialog(app, () => new GitignorePreviewDialog(closeData), dlg =>
        {
            dlg.Close();
            Assert.True(!closeData.Confirmed, "UI-004：直接关闭（X）后 Confirmed 必须保持 false（等价取消，不写文件）");
        });
    }

    /// <summary>在视觉树中查找指定类型的后代元素。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // ---------- 基础设施 ----------

    /// <summary>
    /// 运行一个对话框场景。modal=false：Show + 空闲布局后验证；
    /// modal=true：真实 ShowDialog（按钮 Click 处理器需要 DialogResult 合法才能执行），
    /// 通过 Dispatcher 空闲回调驱动验证与关闭，避免阻塞。
    /// </summary>
    private static void RunDialog(App app, Func<Window> create, Action<Window>? verify = null, bool modal = false)
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
            if (modal)
            {
                Exception? verifyError = null;
                dlg.Loaded += (_, _) =>
                {
                    dlg.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            verify?.Invoke(dlg);
                        }
                        catch (Exception ex)
                        {
                            verifyError = ex;
                        }
                        if (dlg.IsVisible) dlg.Close();
                    }), DispatcherPriority.ApplicationIdle);
                };
                dlg.ShowDialog();
                if (!dlg.IsLoaded)
                {
                    throw new Exception("对话框未完成加载（IsLoaded == false）");
                }
                if (verifyError != null) throw verifyError;
                dlg.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            else
            {
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
