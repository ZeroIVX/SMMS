using CommunityToolkit.Mvvm.ComponentModel;

namespace SMMS.Desktop.ViewModels;

public sealed class ConfirmDialogViewModel(
    string title,
    string message,
    string confirmText,
    string cancelText,
    bool showCancel = true)
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public string CancelText { get; } = cancelText;

    /// <summary>纯提示型对话框可以只要一个按钮。</summary>
    public bool ShowCancel { get; } = showCancel;
}

public sealed partial class TextPromptViewModel(string title, string message, string value, string confirmText, string cancelText)
    : ObservableObject
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public string CancelText { get; } = cancelText;

    [ObservableProperty]
    public partial string Value { get; set; } = value;
}
