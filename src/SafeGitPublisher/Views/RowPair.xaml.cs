using System.Windows;
using System.Windows.Controls;

namespace SafeGitPublisher.Views;

/// <summary>
/// 标签 + 值的简单行控件（确认页/报告页复用）。
/// </summary>
public partial class RowPair : UserControl
{
    public RowPair()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(RowPair), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(RowPair), new PropertyMetadata(string.Empty));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}