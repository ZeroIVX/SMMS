using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class ProfilePageView : UserControl
{
    public ProfilePageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ProfilePageViewModel viewModel)
        {
            return;
        }

        viewModel.ConfirmRequested = ShowConfirmAsync;
        viewModel.PromptRequested = ShowPromptAsync;
        viewModel.SavePathRequested = ShowSaveAsync;
        viewModel.OpenPathRequested = ShowOpenAsync;
        viewModel.PickFolderRequested = ShowPickFolderAsync;
    }

    private void OnActivateClick(object? sender, RoutedEventArgs e) => Invoke(viewModel => viewModel.ActivateCommand, sender);

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Invoke(viewModel => viewModel.DeleteCommand, sender);

    private void OnRenameClick(object? sender, RoutedEventArgs e) => Invoke(viewModel => viewModel.RenameCommand, sender);

    private void OnExportClick(object? sender, RoutedEventArgs e) => Invoke(viewModel => viewModel.ExportCommand, sender);

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilePageViewModel viewModel)
        {
            viewModel.ImportCommand.Execute(null);
        }
    }

    private void Invoke(
        Func<ProfilePageViewModel, System.Windows.Input.ICommand> selector,
        object? sender)
    {
        if (sender is Button { Tag: ProfileItemViewModel item }
            && DataContext is ProfilePageViewModel viewModel)
        {
            var command = selector(viewModel);
            if (command.CanExecute(item))
            {
                command.Execute(item);
            }
        }
    }

    private async Task<bool> ShowConfirmAsync(ConfirmDialogViewModel viewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var dialog = new ConfirmDialog { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(owner);
    }

    private async Task<string?> ShowPromptAsync(TextPromptViewModel viewModel)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        var dialog = new TextPromptDialog { DataContext = viewModel };
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task<string?> ShowSaveAsync(string suggestedName, string filterName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType(filterName) { Patterns = ["*.zip"] }
            ]
        });

        return file?.TryGetLocalPath();
    }

    private async Task<string?> ShowOpenAsync()
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
                new FilePickerFileType("SMMS pack") { Patterns = ["*.zip", "*.smms.zip"] }
            ]
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> ShowPickFolderAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
