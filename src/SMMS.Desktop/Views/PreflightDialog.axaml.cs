using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SMMS.Desktop.Views;

public partial class PreflightDialog : Window
{
    public PreflightDialog() => InitializeComponent();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnContinueClick(object? sender, RoutedEventArgs e) => Close(true);
}
