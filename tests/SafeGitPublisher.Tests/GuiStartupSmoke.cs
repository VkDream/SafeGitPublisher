using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using SafeGitPublisher.Views;

namespace SafeGitPublisher.Tests;

/// <summary>
/// WPF 主窗口启动冒烟测试。
/// 由 GuiSmokeHost 在同一 STA 线程、同一 App 实例上调用（Application 为 AppDomain 单例）。
/// 捕获并判定失败：窗口构造异常、Dispatcher 未处理异常、DataBinding 运行时错误。
/// </summary>
public static class GuiStartupSmoke
{
    /// <summary>在已就绪的 App 上运行主窗口冒烟。</summary>
    public static void RunMainWindowSmoke(App app)
    {
        var bindingLines = new ConcurrentBag<string>();
        var unhandled = new ConcurrentBag<Exception>();
        var listener = new CollectingTraceListener(bindingLines);

        DispatcherUnhandledExceptionEventHandler unhandledHandler = (_, e) =>
        {
            unhandled.Add(e.Exception);
            e.Handled = true; // 阻止冒烟进程崩溃，改由断言判定失败
        };
        app.DispatcherUnhandledException += unhandledHandler;

        // 监听 WPF DataBinding 运行期错误（TraceSource 默认输出级别为 Error）
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var prevLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            var hasPageBg = app.Resources.Contains("PageBg");
            if (!hasPageBg)
            {
                throw new Exception($"App 资源字典未加载（Count={app.Resources.Count}，Contains(PageBg)={hasPageBg}）");
            }
            var window = new MainWindow();
            window.Show();
            // 至少完成一轮完整布局/渲染周期，并给启动期异步任务留出触发窗口
            for (var i = 0; i < 3; i++)
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Thread.Sleep(200);
            }
            if (!window.IsLoaded)
            {
                throw new Exception("主窗口未完成加载（IsLoaded == false）");
            }
            window.Close();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            app.DispatcherUnhandledException -= unhandledHandler;
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = prevLevel;
        }

        // ---- 断言 ----
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

    /// <summary>收集 WPF TraceSource 输出的监听器。</summary>
    internal sealed class CollectingTraceListener : TraceListener
    {
        private readonly ConcurrentBag<string> _lines;

        public CollectingTraceListener(ConcurrentBag<string> lines) => _lines = lines;

        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message)) _lines.Add(message);
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message)) _lines.Add(message);
        }
    }
}
