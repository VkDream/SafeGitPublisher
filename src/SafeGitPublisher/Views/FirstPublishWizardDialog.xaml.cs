using System.Windows;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 首次发布向导对话框。收起后执行步骤由 MainViewModel 按顺序完成。
/// </summary>
public partial class FirstPublishWizardDialog : Window
{
    private readonly WizardData _data;

    public FirstPublishWizardDialog(WizardData data)
    {
        InitializeComponent();
        _data = data;
        DataContext = data;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_data.CommitMessage))
        {
            MessageBox.Show("请填写 Commit Message。", "SafeGitPublisher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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