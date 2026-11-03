using Avalonia.Controls;
using Avalonia.Interactivity;
using SMMS.Desktop.ViewModels;

namespace SMMS.Desktop.Views;

public partial class UpdatesPageView : UserControl
{
    public UpdatesPageView() => InitializeComponent();

    /// <summary>
    /// 快照列表行内的「恢复」按钮。
    /// </summary>
    /// <remarks>
    /// 原先用 <c>{Binding ((vm:UpdatesPageViewModel)$parent[ItemsControl].DataContext).Snapshots.RestoreCommand}</c>
    /// 取值，Avalonia 的绑定表达式解析不了这种「把整个路径包进类型转换」的写法，
    /// 抛 ExpressionParseException。模板只在列表有数据时才构建，所以平时不崩，
    /// 一旦检查出更新就整窗崩溃。改用事件转交，避开复杂绑定。
    /// </remarks>
    private void OnRestoreSnapshotClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: var item } && DataContext is UpdatesPageViewModel viewModel)
        {
            viewModel.Snapshots.RestoreCommand.Execute(item);
        }
    }
}
