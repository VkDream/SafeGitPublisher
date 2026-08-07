using System.Windows;
using SafeGitPublisher.Services;

namespace SafeGitPublisher.Views;

/// <summary>
/// 关于窗口：版本来自程序集元数据（AppVersionService），不硬编码。
/// </summary>
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppVersionService.ProductVersion}";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
