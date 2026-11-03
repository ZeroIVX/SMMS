using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using SMMS.Core.Game;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // 只接受文件，避免拖入文本时出现可放置提示。
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path)
                           && path!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(path => path!)
            .ToList();

        if (paths is null || paths.Count == 0)
        {
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var message = await viewModel.HandleDropAsync(paths);

        var dialog = new ConfirmDialog
        {
            DataContext = new ConfirmDialogViewModel(
                viewModel.Localize("drop.title"),
                message,
                viewModel.Localize("common.ok"),
                viewModel.Localize("common.cancel"),
                showCancel: false)
        };

        await dialog.ShowDialog<bool>(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PreflightConfirmation = ShowPreflightAsync;
            viewModel.DowngradeConfirmation = ShowDowngradeAsync;
        }
    }

    private async Task<bool> ShowDowngradeAsync(IReadOnlyList<string> items)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        var list = string.Join(Environment.NewLine, items.Select(item => "   " + item));

        var dialog = new ConfirmDialog
        {
            DataContext = new ConfirmDialogViewModel(
                viewModel.Localize("drop.confirm.downgradeTitle"),
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    viewModel.Localize("drop.confirm.downgradeMessage"),
                    list),
                viewModel.Localize("drop.confirm.downgradeConfirm"),
                viewModel.Localize("common.cancel"))
        };

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ShowPreflightAsync(PreflightResult result)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        var dialog = new PreflightDialog
        {
            DataContext = viewModel.CreatePreflightViewModel(result)
        };

        return await dialog.ShowDialog<bool>(this);
    }
}
