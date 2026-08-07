using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 提交前最终确认对话框（应用内，非 MessageBox）。
/// </summary>
public partial class ConfirmPublishDialog : Window
{
    private readonly ConfirmPublishData _data;

    public ConfirmPublishDialog(ConfirmPublishData data)
    {
        InitializeComponent();
        _data = data;
        DataContext = data;
        ConfirmButton.Content = data.CommitOnly ? "确认提交" : "确认提交并 Push";
        // Zero Change 兜底：0 变更时确认按钮必须禁用（正常流程已被上层拦截）
        ConfirmButton.IsEnabled = data.ChangeCount > 0;
        ConfirmButton.ToolTip = data.ChangeCount > 0 ? null : "当前没有可提交的变更";
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}