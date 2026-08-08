using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// .gitignore 内容预览对话框。
/// </summary>
public partial class GitignorePreviewDialog : Window
{
    private readonly GitignorePreviewData _data;

    public GitignorePreviewDialog(GitignorePreviewData data)
    {
        InitializeComponent();
        _data = data;
        // SGP-UI-001 修复（根因）：原实现 `Content = data.NewContent` 会把 Window.Content
        // （XAML 根 Grid：标题/说明/输入区/按钮条）整体替换为纯文本，
        // 导致"取消/应用"按钮在真实 GUI 中不可见。改为写入内容区 TextBox。
        ContentBox.Text = data.NewContent;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        _data.Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _data.Confirmed = false;
        DialogResult = false;
        Close();
    }
}