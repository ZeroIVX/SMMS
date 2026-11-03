using Avalonia.Controls;
using Avalonia.Interactivity;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class TextPromptDialog : Window
{
    public TextPromptDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            var box = this.FindControl<TextBox>("InputBox");
            if (box is not null)
            {
                box.Focus();
                box.SelectAll();
            }
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
        => Close((DataContext as TextPromptViewModel)?.Value);
}
