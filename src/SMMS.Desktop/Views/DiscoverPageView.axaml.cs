using Avalonia.Controls;
using Avalonia.Interactivity;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class DiscoverPageView : UserControl
{
    public DiscoverPageView()
    {
        InitializeComponent();
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NexusItemViewModel item }
            && DataContext is DiscoverPageViewModel viewModel)
        {
            viewModel.OpenUrlCommand.Execute(item.PageUrl);
        }
    }

    private void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NexusItemViewModel item }
            && DataContext is DiscoverPageViewModel viewModel)
        {
            viewModel.TranslateCommand.Execute(item);
        }
    }
}
