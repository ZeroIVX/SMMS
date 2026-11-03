using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class LaunchpadPageView : UserControl
{
    /// <summary>快捷操作低于该宽度时折成两行（2×2），避免文案被裁切。</summary>
    private const double QuickActionsWrapWidth = 1100;

    public LaunchpadPageView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (QuickActions is null)
        {
            return;
        }

        var columns = e.NewSize.Width < QuickActionsWrapWidth ? 2 : 4;
        if (QuickActions.Columns != columns)
        {
            QuickActions.Columns = columns;
        }

        // 窄窗口时右栏收窄，把宽度让给主内容区。
        if (PageGrid?.ColumnDefinitions.Count > 1)
        {
            var sidebar = e.NewSize.Width < QuickActionsWrapWidth ? 300 : 360;
            if (Math.Abs(PageGrid.ColumnDefinitions[1].Width.Value - sidebar) > 0.5)
            {
                PageGrid.ColumnDefinitions[1].Width = new GridLength(sidebar);
            }
        }
    }

    /// <summary>快捷操作「导入压缩包」：选 zip 后走与拖放同一条导入链路。</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        // 页面自身 DataContext 是页面 VM，主窗口 VM 要从宿主窗口取。
        if (this.FindAncestorOfType<Window>() is not { DataContext: MainWindowViewModel main })
        {
            return;
        }

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = main.Localize("drop.pickTitle"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("zip") { Patterns = ["*.zip"] }
            ]
        });

        if (files.Count == 0)
        {
            return;
        }

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }

        if (paths.Count > 0)
        {
            await main.HandleDropAsync(paths);
        }
    }
}
