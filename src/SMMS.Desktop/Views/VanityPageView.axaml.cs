using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class VanityPageView : UserControl
{
    public VanityPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is VanityPageViewModel viewModel)
        {
            viewModel.PickArchiveRequested = ShowPickArchiveAsync;
        }
    }

    private void OnVanityToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: VanityItemViewModel item }
            && DataContext is VanityPageViewModel viewModel)
        {
            viewModel.ToggleVanity(item);
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VanityItemViewModel item }
            && DataContext is VanityPageViewModel viewModel)
        {
            viewModel.DeleteCommand.Execute(item);
        }
    }

    private void OnContentPackToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: ContentPackItemViewModel item }
            && DataContext is VanityPageViewModel viewModel)
        {
            viewModel.ToggleContentPack(item);
        }
    }

    private async Task<string?> ShowPickArchiveAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("美化包") { Patterns = ["*.zip", "*.7z", "*.rar"] }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
