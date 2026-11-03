using Avalonia.Controls;
using Avalonia.Interactivity;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class ModsPageView : UserControl
{
    public ModsPageView()
    {
        InitializeComponent();

        // 断点由页面宽度驱动：≥1280 三栏、<1280 详情抽屉、<1024 紧凑表格。
        SizeChanged += OnSizeChanged;
        DataContextChanged += (_, _) => UpdateBreakpoints();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateBreakpoints();

    /// <summary>把当前宽度转交给 VM。设计器 / 早期加载时 DataContext 可能还不是目标类型。</summary>
    private void UpdateBreakpoints()
    {
        if (DataContext is ModsPageViewModel viewModel)
        {
            viewModel.UpdateViewWidth(Bounds.Width);
        }
    }

    /// <summary>备注在失焦时保存，避免每敲一个字都写盘。</summary>
    private void OnNoteLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ModsPageViewModel viewModel)
        {
            viewModel.SaveNoteCommand.Execute(null);
        }
    }
}
