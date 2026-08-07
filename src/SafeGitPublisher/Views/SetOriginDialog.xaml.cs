using System.Windows;
using SafeGitPublisher.Services;
using SafeGitPublisher.ViewModels;

namespace SafeGitPublisher.Views;

/// <summary>
/// 设置 origin 对话框。校验 URL，提示畸形值。
/// </summary>
public partial class SetOriginDialog : Window
{
    private readonly SetOriginData _data;

    public SetOriginDialog(SetOriginData data)
    {
        InitializeComponent();
        _data = data;
        Title = $"设置 {data.RemoteName}";
        UrlBox.Text = data.CurrentUrl ?? data.SuggestedUrl;

        if (data.CurrentUrl != null)
        {
            ReplaceCheck.Visibility = Visibility.Visible;
            UpdateHint("origin 已存在，修改将覆盖当前值。");
        }
        else
        {
            UpdateHint($"建议：{data.SuggestedUrl}");
        }
    }

    private void UpdateHint(string text)
    {
        HintText.Text = text;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim() ?? string.Empty;
        if (url.Length == 0)
        {
            MessageBox.Show("URL 不能为空。", "SafeGitPublisher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 校验：畸形 URL 强制提示
        var (_, _, malformed, reason, suggested) = GitRemoteService.ParseUrl(url);
        if (malformed)
        {
            var fix = suggested == null ? string.Empty : $"\n\n建议改为：{suggested}";
            var go = MessageBox.Show($"URL 疑似异常：\n{url}\n{reason}{fix}\n\n仍要保存吗？",
                "SafeGitPublisher", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (go != MessageBoxResult.Yes) return;
        }

        if (_data.CurrentUrl != null && ReplaceCheck.IsChecked != true)
        {
            MessageBox.Show($"origin 已存在，请勾选“我确认将替换为上述 URL”。",
                "SafeGitPublisher", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _data.ResultUrl = url;
        _data.ConfirmReplace = _data.CurrentUrl != null;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _data.ResultUrl = null;
        DialogResult = false;
        Close();
    }
}