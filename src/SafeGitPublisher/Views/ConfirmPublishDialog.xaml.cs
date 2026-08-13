using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 提交前最终确认对话框（应用内，非 MessageBox）。
/// </summary>
public partial class ConfirmPublishDialog : Window
{
    private readonly ConfirmPublishData _data;
    private bool _initialized;

    public ConfirmPublishDialog(ConfirmPublishData data)
    {
        InitializeComponent();
        _data = data;
        DataContext = data;
        ConfirmButton.Content = data.CommitOnly ? "确认提交" : "确认提交并 Push";
        _initialized = true;
        UpdateConfirmState();
    }

    /// <summary>图片脱敏勾选变化时，同步最终确认按钮的可用状态与原因。</summary>
    private void OnImageConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized) UpdateConfirmState();
    }

    private void UpdateConfirmState()
    {
        var hasChanges = _data.ChangeCount > 0;
        var imageReady = !_data.RequiresImageConfirmation || _data.ImageConfirmed;
        ConfirmButton.IsEnabled = hasChanges && imageReady;
        ConfirmButton.ToolTip = !hasChanges
            ? "当前没有可提交的变更"
            : !imageReady ? "请先确认本次图片已完成脱敏检查" : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_data.RequiresImageConfirmation && !_data.ImageConfirmed) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
