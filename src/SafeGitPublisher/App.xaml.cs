using System.Windows;

namespace SafeGitPublisher;

/// <summary>
/// 应用入口。全局异常捕获避免静默崩溃。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"发生未处理异常：{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "SafeGitPublisher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}