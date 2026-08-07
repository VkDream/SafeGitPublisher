using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 详细报告对话框。显示全部检查项、Secret 发现、变更列表与已安全忽略项。
/// </summary>
public partial class ReportDialog : Window
{
    public ReportDialog(ReportData data)
    {
        InitializeComponent();
        DataContext = data;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}