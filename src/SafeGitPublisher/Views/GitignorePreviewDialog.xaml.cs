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
        Content = data.NewContent;
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