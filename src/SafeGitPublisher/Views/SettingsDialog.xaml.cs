using System.Diagnostics;
using System.Globalization;
using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 设置对话框。DataContext 为 SettingsData。
/// 大文件阈值验证规则：0 &lt; 警告 &lt; 高危 &lt; 阻断 ≤ 100 MB，非法时保存按钮禁用。
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly SettingsData _data;

    public SettingsDialog(SettingsData data)
    {
        InitializeComponent();
        _data = data;
        DataContext = data;
        SettingsPathText.Text = data.SettingsPath;
        ValidateThresholds();
    }

    private void OnThresholdChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ValidateThresholds();

    private void ValidateThresholds()
    {
        var ok = TryParse(WarnBox) is double w && w > 0
                 && TryParse(HighBox) is double h && h > w
                 && TryParse(BlockBox) is double b && b > h && b <= 100;
        var repoOk = TryParse(RepoWarnBox) is double rw && rw > 0
                     && TryParse(RepoBlockBox) is double rb && rb > rw;
        SaveButton.IsEnabled = ok && repoOk;
        ThresholdErrorText.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        RepoThresholdErrorText.Visibility = repoOk ? Visibility.Collapsed : Visibility.Visible;
    }

    private static double? TryParse(System.Windows.Controls.TextBox box)
        => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_data.SettingsPath)) return;
        try
        {
            Clipboard.SetText(_data.SettingsPath);
        }
        catch
        {
            // 剪贴板异常忽略
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.GetDirectoryName(_data.SettingsPath);
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch
        {
            // 打开失败忽略
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled) return;
        _data.Saved = true;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
