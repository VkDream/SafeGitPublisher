using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SafeGitPublisher.Models;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 主窗口。负责对话框联动与日志自动滚动；业务逻辑全部在 MainViewModel。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel();
        DataContext = _vm;

        // ---- ViewModel 事件 → UI 对话框 ----
        _vm.BrowseFolderRequested += OnBrowseFolderRequested;
        _vm.ConfirmPublishRequested += OnConfirmPublishRequested;
        _vm.SetOriginRequested += OnSetOriginRequested;
        _vm.GitignorePreviewRequested += OnGitignorePreviewRequested;
        _vm.WizardRequested += OnWizardRequested;
        _vm.ShowReportRequested += OnShowReportRequested;
        _vm.SettingsRequested += OnSettingsRequested;
        _vm.ShowMessageRequested += OnShowMessageRequested;

        _vm.Logs.CollectionChanged += LogsOnCollectionChanged;
    }

    // ---------- 对话框联动 ----------
    private async Task<string?> OnBrowseFolderRequested()
    {
        // WPF .NET 8+ 原生文件夹选择对话框
        var dialog = new OpenFolderDialog
        {
            Title = "选择项目目录",
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private Task<bool> OnConfirmPublishRequested(ConfirmPublishData data)
    {
        var dlg = new ConfirmPublishDialog(data) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.DialogResult == true);
    }

    private Task<SetOriginData?> OnSetOriginRequested(SetOriginData data)
    {
        var dlg = new SetOriginDialog(data) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.DialogResult == true ? data : null);
    }

    private Task<bool> OnGitignorePreviewRequested(GitignorePreviewData data)
    {
        var dlg = new GitignorePreviewDialog(data) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.DialogResult == true && data.Confirmed);
    }

    private Task<WizardData?> OnWizardRequested(WizardData data)
    {
        var dlg = new FirstPublishWizardDialog(data) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.DialogResult == true ? data : null);
    }

    private void OnShowReportRequested(ReportData data)
    {
        var dlg = new ReportDialog(data) { Owner = this };
        dlg.ShowDialog();
    }

    private Task<bool> OnSettingsRequested(SettingsData data)
    {
        var dlg = new SettingsDialog(data) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.DialogResult == true && data.Saved);
    }

    private void OnShowMessageRequested(string message, bool isError)
    {
        MessageBox.Show(this, message, "SafeGitPublisher",
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    // ---------- 关于 ----------
    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    // ---------- 日志：复制 / 清空 ----------
    private void OnCopyLogs(object sender, RoutedEventArgs e)
    {
        var text = _vm.BuildLogText();
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被占用等场景忽略
        }
    }

    private void OnClearLogs(object sender, RoutedEventArgs e)
    {
        _vm.ClearLogs();
    }

    // ---------- 变更列表右键菜单 ----------
    private static GitFileChange? MenuItemChange(object sender)
    {
        return (sender as FrameworkElement)?.DataContext as GitFileChange;
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var change = MenuItemChange(sender);
        if (change == null) return;
        try
        {
            Clipboard.SetText(change.Path);
        }
        catch
        {
            // 剪贴板异常忽略
        }
    }

    private void OnRevealInExplorer(object sender, RoutedEventArgs e)
    {
        var change = MenuItemChange(sender);
        var root = _vm.LastContext?.RepositoryRoot;
        if (change == null || root == null) return;
        var full = System.IO.Path.Combine(root, change.Path);
        if (!System.IO.File.Exists(full) && !System.IO.Directory.Exists(full)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开资源管理器失败忽略
        }
    }

    // ---------- 日志自动滚动（仅在用户位于底部时跟随） ----------
    private void LogsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (LogList.Items.Count == 0) return;
            if (IsLogAtBottom())
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        });
    }

    private bool IsLogAtBottom()
    {
        var scroll = FindVisualChild<ScrollViewer>(LogList);
        if (scroll == null) return true;
        return scroll.ScrollableHeight == 0 || scroll.VerticalOffset >= scroll.ScrollableHeight - 24;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
