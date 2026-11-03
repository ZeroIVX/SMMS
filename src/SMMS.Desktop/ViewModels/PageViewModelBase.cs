using CommunityToolkit.Mvvm.ComponentModel;
using SMMS.Desktop.Localization;

namespace SMMS.Desktop.ViewModels;

/// <summary>
/// 页面 ViewModel 基类。统一持有本地化服务，并在语言切换时由主视图模型驱动刷新文案。
/// 顶栏的标题与副标题也由页面自己决定，页面切换时主窗口只负责显示。
/// </summary>
public abstract partial class PageViewModelBase : ViewModelBase, IDisposable
{
    protected PageViewModelBase(ILocalizationService localization) => Localization = localization;

    protected ILocalizationService Localization { get; }

    /// <summary>顶栏标题，例如「模块库」。</summary>
    [ObservableProperty]
    public partial string PageTitle { get; protected set; } = string.Empty;

    /// <summary>顶栏副标题，例如「12 个模组 · 10 已启用 · 按加载顺序」。</summary>
    [ObservableProperty]
    public partial string PageSubtitle { get; protected set; } = string.Empty;

    protected string L(string key) => Localization[key];

    /// <summary>用本地化键设置顶栏标题与副标题。</summary>
    protected void SetHeader(string titleKey, string subtitleKey)
    {
        PageTitle = L(titleKey);
        PageSubtitle = L(subtitleKey);
    }

    /// <summary>直接用算好的文案设置顶栏（副标题通常含实时数据）。</summary>
    protected void SetHeaderText(string title, string subtitle)
    {
        PageTitle = title;
        PageSubtitle = subtitle;
    }

    public abstract void RefreshTexts();

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
