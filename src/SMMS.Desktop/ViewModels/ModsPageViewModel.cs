using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Core.Updates;
using SMMS.Core.Versioning;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Translation;
using SMMS.UseCases.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>列表筛选模式，对应工具栏上的四个胶囊。</summary>
public enum ModListFilter
{
    All,
    Risk,
    Update,
    Disabled
}

/// <summary>列表排序模式，对应工具栏右侧的下拉。</summary>
public enum ModListSort
{
    LoadOrder,
    Name,
    Version
}

public partial class ModsPageViewModel : PageViewModelBase
{
    /// <summary>表格行高。紧凑布局下压到设计稿 07 规范的 52。</summary>
    private const double NormalRowHeight = 64;

    private const double CompactRowHeight = 52;

    // 设计稿断点按窗口宽度定义：≥1280 三栏、<1280 详情收起为抽屉、<1024 紧凑表格。
    // 这里是页面自身的宽度（窗口减去左侧导航条），所以两边同减 62 的导航宽度。
    private const double WideLayoutThreshold = 1280 - 62;

    private const double CompactLayoutThreshold = 1024 - 62;

    private readonly ModLibraryService _library;
    private readonly GameInstallationService _games;
    private readonly ProfileService _profiles;
    private readonly ModToggleService _toggle;
    private readonly ModNotesService _notes;
    private readonly TranslationService _translation;
    private readonly UpdateCheckService _updateChecker;
    private readonly ILogger<ModsPageViewModel> _logger;
    private readonly List<ModListItemViewModel> _all = [];

    private GameInstallation? _installation;
    private ModProfile? _activeProfile;
    private UpdateCheckResult _updates = UpdateCheckResult.Empty;
    private IReadOnlyList<InstalledMod> _installed = [];
    private string? _errorKey;
    private string? _noticeKey;
    private int _totalCount;
    private int _codeCount;
    private int _contentCount;
    private int _warningCount;
    private bool _suppressSortChange;

    public ModsPageViewModel(
        ModLibraryService library,
        GameInstallationService games,
        ProfileService profiles,
        ModToggleService toggle,
        ModNotesService notes,
        TranslationService translation,
        UpdateCheckService updateChecker,
        ILocalizationService localization,
        ILogger<ModsPageViewModel> logger)
        : base(localization)
    {
        _library = library;
        _games = games;
        _profiles = profiles;
        _toggle = toggle;
        _notes = notes;
        _translation = translation;
        _updateChecker = updateChecker;
        _logger = logger;

        RebuildSortOptions();
        Refresh();
    }

    public ObservableCollection<ModListItemViewModel> Mods { get; } = [];

    /// <summary>当前配置档识别到的模组总数，供顶栏摘要读取。</summary>
    public int TotalCount => _totalCount;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WarningText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasWarnings { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>琥珀色的中性提示条，用于说明「当前版本做不到」之类的事，不当作错误。</summary>
    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial ModListItemViewModel? SelectedMod { get; set; }

    /// <summary>详情面板里正在编辑的备注。</summary>
    [ObservableProperty]
    public partial string CurrentNote { get; set; } = string.Empty;

    /// <summary>列表第二行是否优先显示备注。关掉则显示 UniqueID 与模组类型。</summary>
    [ObservableProperty]
    public partial bool ShowNotes { get; set; }

    [ObservableProperty]
    public partial bool IsTranslatingNote { get; set; }

    // ---- 工具栏：筛选 ----

    [ObservableProperty]
    public partial ModListFilter FilterMode { get; set; } = ModListFilter.All;

    [ObservableProperty]
    public partial string FilterAllText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterRiskText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterUpdateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterDisabledText { get; set; } = string.Empty;

    public bool IsAllFilterActive => FilterMode == ModListFilter.All;

    public bool IsRiskFilterActive => FilterMode == ModListFilter.Risk;

    public bool IsUpdateFilterActive => FilterMode == ModListFilter.Update;

    public bool IsDisabledFilterActive => FilterMode == ModListFilter.Disabled;

    // ---- 工具栏：排序 ----

    public ObservableCollection<string> SortOptions { get; } = [];

    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    public ModListSort SortMode => (ModListSort)SelectedSortIndex;

    // ---- 响应式布局 ----

    /// <summary>≥1280 逻辑宽度时详情面板固定在右侧；否则收进抽屉，由工具栏按钮唤出。</summary>
    [ObservableProperty]
    public partial bool IsWideLayout { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDetailDrawerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsCompact { get; set; }

    /// <summary>行高随断点变化，视图直接绑定。</summary>
    public double RowHeight => IsCompact ? CompactRowHeight : NormalRowHeight;

    /// <summary>紧凑布局按设计稿隐藏「依赖」列。</summary>
    public bool IsDependencyColumnVisible => !IsCompact;

    /// <summary>紧凑档（&lt;1024）只显示「全部 / 有风险」两个筛选芯片。</summary>
    public bool ShowSecondaryFilters => !IsCompact;

    /// <summary>当前正在管理的配置档名，用于避免在多个路径之间误操作。</summary>
    [ObservableProperty]
    public partial string ActiveProfileName { get; set; } = string.Empty;

    /// <summary>当前扫描的模组目录完整路径。</summary>
    [ObservableProperty]
    public partial string ModsPathText { get; set; } = string.Empty;

    public bool HasActivePath => !string.IsNullOrEmpty(ModsPathText);

    /// <summary>有选中项时右侧详情面板才显示内容。</summary>
    public bool HasSelection => SelectedMod is not null;

    public string CheckButtonText => IsChecking ? L("mods.checking") : L("mods.checkUpdates");

    public string FooterText { get; private set; } = string.Empty;

    partial void OnSelectedModChanged(ModListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));

        // 切换选中项时重新加载备注，避免把上一个模组的文字带过来。
        CurrentNote = value is null ? string.Empty : _notes.Get(value.Mod.UniqueId);

        // 窄窗口下面板是抽屉：选中一行就顺手展开，省一次点击。
        if (!IsWideLayout && value is not null)
        {
            IsDetailDrawerOpen = true;
        }
    }

    partial void OnFilterModeChanged(ModListFilter value)
    {
        OnPropertyChanged(nameof(IsAllFilterActive));
        OnPropertyChanged(nameof(IsRiskFilterActive));
        OnPropertyChanged(nameof(IsUpdateFilterActive));
        OnPropertyChanged(nameof(IsDisabledFilterActive));

        ApplyFilter();
        UpdateTexts();
    }

    partial void OnSelectedSortIndexChanged(int value)
    {
        if (_suppressSortChange || value < 0)
        {
            return;
        }

        ApplyFilter();
        UpdateTexts();
    }

    partial void OnIsCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(RowHeight));
        OnPropertyChanged(nameof(IsDependencyColumnVisible));
        OnPropertyChanged(nameof(ShowSecondaryFilters));
    }

    partial void OnIsWideLayoutChanged(bool value)
    {
        if (value)
        {
            IsDetailDrawerOpen = false;
        }
    }

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CheckButtonText));

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        UpdateTexts();
    }

    partial void OnShowNotesChanged(bool value) => Refresh();

    partial void OnModsPathTextChanged(string value)
        => OnPropertyChanged(nameof(HasActivePath));

    /// <summary>视图尺寸变化时由 code-behind 转发，换算成断点布尔量。</summary>
    public void UpdateViewWidth(double width)
    {
        if (double.IsNaN(width) || width <= 0)
        {
            return;
        }

        IsWideLayout = width >= WideLayoutThreshold;
        IsCompact = width < CompactLayoutThreshold;

        // 紧凑档只显示「全部 / 有风险」两个芯片：如果当前筛的是被隐藏的芯片，
        // 自动回落到「全部」，避免出现「列表被筛过但看不到任何选中芯片」。
        if (IsCompact && FilterMode is ModListFilter.Update or ModListFilter.Disabled)
        {
            FilterMode = ModListFilter.All;
        }
    }

    /// <summary>筛选胶囊。参数为 <see cref="ModListFilter"/> 的名称。</summary>
    [RelayCommand]
    private void SetFilter(string? mode)
    {
        if (Enum.TryParse<ModListFilter>(mode, ignoreCase: true, out var filter))
        {
            FilterMode = filter;
        }
    }

    /// <summary>窄窗口下的「详情」抽屉开关。</summary>
    [RelayCommand]
    private void ToggleDetailDrawer() => IsDetailDrawerOpen = !IsDetailDrawerOpen;

    /// <summary>详情面板底部主操作：优先更新页，其次 Nexus，最后打开模组目录。</summary>
    [RelayCommand]
    private void PrimaryAction()
    {
        if (SelectedMod is null)
        {
            return;
        }

        if (SelectedMod.Update?.UpdateUrl is { Length: > 0 } updateUrl)
        {
            OpenUrl(updateUrl);
            return;
        }

        if (SelectedMod.NexusUrl is { Length: > 0 } nexusUrl)
        {
            OpenUrl(nexusUrl);
            return;
        }

        OpenModsFolder();
    }

    /// <summary>加载顺序写回需要 SMAPI 侧支持，先明确告知而不是把按钮装成能用。</summary>
    [RelayCommand]
    private void MoveUp() => ShowNotice("mods.loadOrder.unsupported");

    [RelayCommand]
    private void MoveDown() => ShowNotice("mods.loadOrder.unsupported");

    [RelayCommand]
    private void OpenDependencyGraph() => ShowNotice("mods.graph.unsupported");

    [RelayCommand]
    private void DismissNotice()
    {
        _noticeKey = null;
        HasNotice = false;
    }

    /// <summary>把作者描述翻成中文并写入备注；用户仍可在备注框里改措辞。</summary>
    [RelayCommand]
    private async Task TranslateDescriptionAsync()
    {
        if (SelectedMod is null || IsTranslatingNote)
        {
            return;
        }

        var description = SelectedMod.Mod.Description;

        if (string.IsNullOrWhiteSpace(description))
        {
            ShowError("translate.error.empty");
            return;
        }

        IsTranslatingNote = true;

        try
        {
            var result = await _translation.TranslateToChineseAsync(description).ConfigureAwait(true);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                ShowError(result.ErrorKey ?? "translate.error.failed");
                return;
            }

            CurrentNote = result.Text;
            _notes.Set(SelectedMod.Mod.UniqueId, result.Text);
            SelectedMod.UpdateNote(result.Text);
            HasError = false;
        }
        finally
        {
            IsTranslatingNote = false;
        }
    }

    /// <summary>备注输入框失焦/点保存时写入服务，并原地更新列表行，避免列表重建导致滚动跳动。</summary>
    [RelayCommand]
    private void SaveNote()
    {
        if (SelectedMod is null)
        {
            return;
        }

        _notes.Set(SelectedMod.Mod.UniqueId, CurrentNote);
        SelectedMod.UpdateNote(CurrentNote);
    }

    /// <summary>清空备注（写空字符串），让翻译/手写的备注可以撤销。</summary>
    [RelayCommand]
    private void ClearNote()
    {
        if (SelectedMod is null)
        {
            return;
        }

        _notes.Set(SelectedMod.Mod.UniqueId, string.Empty);
        CurrentNote = string.Empty;
        SelectedMod.UpdateNote(string.Empty);
    }

    /// <summary>在资源管理器中打开当前模组目录，便于核对改的是哪一份。</summary>
    [RelayCommand]
    private void OpenModsFolder()
    {
        var path = _activeProfile?.ModsPath;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowError("mods.toggle.missing");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "打开模组目录失败：{Path}", path);
        }
    }

    /// <summary>用系统默认浏览器打开模组页面。</summary>
    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        var previousSelection = SelectedMod?.Mod.UniqueId;

        _installation = _games.Current ?? _games.Resolve();
        _activeProfile = _profiles.GetActive();

        // 扫描当前配置档的模组目录与禁用区；切换配置档后列表随之变化。
        var disabledPath = _activeProfile is null ? null : _toggle.GetDisabledPath(_activeProfile);
        var snapshot = _library.Load(_activeProfile?.ModsPath, disabledPath);

        _installed = snapshot.Mods;
        _all.Clear();

        foreach (var mod in snapshot.Mods)
        {
            var item = new ModListItemViewModel(
                mod,
                _updates.Find(mod.UniqueId),
                _notes.Get(mod.UniqueId),
                ShowNotes,
                key => L(key));

            // 开关动作交给页面，列表项本身不依赖业务服务。
            item.ToggleCommand = new RelayCommand(() => ToggleMod(item));
            _all.Add(item);
        }

        foreach (var item in _all)
        {
            item.AttachRelations(_installed);
        }

        _totalCount = snapshot.Mods.Count;
        _codeCount = snapshot.CodeModCount;
        _contentCount = snapshot.ContentPackCount;
        _warningCount = snapshot.Warnings.Count;
        HasWarnings = _warningCount > 0;

        // 明确标注正在管理哪一份模组，切换配置档后立即更新。
        ActiveProfileName = _activeProfile is null || string.IsNullOrWhiteSpace(_activeProfile.Name)
            ? L("profiles.default")
            : _activeProfile.Name;
        ModsPathText = _activeProfile?.ModsPath ?? string.Empty;

        ApplyFilter();

        // 刷新会重建列表项，按 UniqueID 找回原来的选中项，避免详情面板莫名清空。
        SelectedMod = previousSelection is null
            ? null
            : Mods.FirstOrDefault(item =>
                string.Equals(item.Mod.UniqueId, previousSelection, StringComparison.OrdinalIgnoreCase));

        // 首屏/刷新后没有选中项时默认选第一条：设计稿是「列表 + 详情同屏有内容」的状态，
        // 只设置 SelectedMod，不额外触发表格滚动或定位。
        if (SelectedMod is null && Mods.Count > 0)
        {
            SelectedMod = Mods[0];
        }

        UpdateTexts();

        _logger.LogInformation(
            "模组页刷新完成：{Total} 个模组，{Code} 个代码模组，{Content} 个内容包",
            _totalCount, _codeCount, _contentCount);
    }

    /// <summary>向 smapi.io 批量查询更新。只提交启用中的模组。</summary>
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsChecking)
        {
            return;
        }

        var profile = _activeProfile ?? _profiles.GetActive();
        if (profile is null)
        {
            ShowError("mods.toggle.noGame");
            return;
        }

        IsChecking = true;

        try
        {
            var disabledPath = _toggle.GetDisabledPath(profile);
            var result = await _updateChecker.CheckAsync(profile, disabledPath).ConfigureAwait(true);

            if (!result.Succeeded)
            {
                ShowError("mods.updateFailed");
                return;
            }

            ClearError();

            // 更新结果只在内存中保留，刷新磁盘扫描后仍可复用到对应行。
            _updates = result;
            Refresh();
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>切换某个模组的启用状态。失败时回滚界面开关，避免界面与磁盘状态不一致。</summary>
    public void ToggleMod(ModListItemViewModel item)
    {
        if (_activeProfile is null)
        {
            item.IsEnabled = !item.IsEnabled;
            ShowError("mods.toggle.noGame");
            return;
        }

        var result = _toggle.SetEnabled(_activeProfile, item.Mod, item.IsEnabled);

        if (!result.Success)
        {
            item.IsEnabled = !item.IsEnabled;
            ShowError(result.ErrorKey ?? "mods.toggle.failed");
            return;
        }

        ClearError();
        _profiles.SyncManifest(_activeProfile);
        Refresh();
    }

    public override void RefreshTexts()
    {
        RebuildSortOptions();

        foreach (var item in _all)
        {
            item.RefreshTexts(_installed);
        }

        UpdateTexts();
        OnPropertyChanged(nameof(CheckButtonText));

        if (_errorKey is { } key)
        {
            ErrorText = L(key);
        }

        if (_noticeKey is { } noticeKey)
        {
            NoticeText = L(noticeKey);
        }
    }

    private void ApplyFilter()
    {
        var keyword = SearchText?.Trim() ?? string.Empty;

        IEnumerable<ModListItemViewModel> query = _all;

        query = FilterMode switch
        {
            ModListFilter.Risk => query.Where(item => item.HasRisk),
            ModListFilter.Update => query.Where(item => item.HasUpdate),
            ModListFilter.Disabled => query.Where(item => !item.IsEnabled),
            _ => query
        };

        if (keyword.Length > 0)
        {
            query = query.Where(item => item.Matches(keyword));
        }

        var ordered = SortMode switch
        {
            ModListSort.Name => query
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            ModListSort.Version => query
                .OrderByDescending(item => item.Mod.Version, VersionComparer.Instance)
                .ToList(),
            _ => query.ToList()
        };

        Mods.Clear();

        var order = 1;
        foreach (var item in ordered)
        {
            item.SetOrder(order++);
            Mods.Add(item);
        }

        // 筛选后选中项可能被隐藏，详情面板要跟着收起。
        if (SelectedMod is not null && !Mods.Contains(SelectedMod))
        {
            SelectedMod = null;
        }

        IsEmpty = Mods.Count == 0;
    }

    private void RebuildSortOptions()
    {
        _suppressSortChange = true;

        try
        {
            // 语言切换会重建选项文本，先记住当前排序再恢复，避免 ComboBox 被重置成未选中。
            var mode = SelectedSortIndex is >= 0 and <= 2 ? (ModListSort)SelectedSortIndex : ModListSort.LoadOrder;

            SelectedSortIndex = -1;
            SortOptions.Clear();
            SortOptions.Add(L("mods.sort.loadOrder"));
            SortOptions.Add(L("mods.sort.name"));
            SortOptions.Add(L("mods.sort.version"));
            SelectedSortIndex = (int)mode;
        }
        finally
        {
            _suppressSortChange = false;
        }
    }

    private void UpdateTexts()
    {
        FilterAllText = string.Format(CultureInfo.CurrentCulture, L("mods.filter.all"), _all.Count);
        FilterRiskText = string.Format(CultureInfo.CurrentCulture, L("mods.filter.risk"), _all.Count(item => item.HasRisk));
        FilterUpdateText = string.Format(CultureInfo.CurrentCulture, L("mods.filter.update"), _all.Count(item => item.HasUpdate));
        FilterDisabledText = string.Format(CultureInfo.CurrentCulture, L("mods.filter.disabled"), _all.Count(item => !item.IsEnabled));

        FooterText = string.Format(CultureInfo.CurrentCulture, L("mods.footer"), _all.Count, Mods.Count);
        OnPropertyChanged(nameof(FooterText));

        var enabledCount = _all.Count(item => item.IsEnabled);
        var sortLabel = SelectedSortIndex >= 0 && SelectedSortIndex < SortOptions.Count
            ? SortOptions[SelectedSortIndex]
            : string.Empty;
        SetHeaderText(
            L("mods.title"),
            string.Format(CultureInfo.CurrentCulture, L("mods.subtitle"), _all.Count, enabledCount, sortLabel));

        SummaryText = _updates.UpdateCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Localization["mods.summaryUpdates"], _totalCount, _codeCount, _contentCount, _updates.UpdateCount)
            : string.Format(CultureInfo.CurrentCulture, Localization["mods.summary"], _totalCount, _codeCount, _contentCount);

        WarningText = string.Format(CultureInfo.CurrentCulture, Localization["mods.skipped"], _warningCount);

        // 没有游戏目录和「有目录但没有模组」是两种不同的空状态，提示要给对。
        if (_installation is null)
        {
            EmptyTitle = L("mods.noGame");
            EmptyHint = L("mods.noGameHint");
        }
        else
        {
            EmptyTitle = L("mods.empty");
            EmptyHint = L("mods.emptyHint");
        }
    }

    private void ShowError(string key)
    {
        _errorKey = key;
        ErrorText = L(key);
        HasError = true;
    }

    private void ClearError()
    {
        _errorKey = null;
        HasError = false;
    }

    private void ShowNotice(string key)
    {
        _noticeKey = key;
        NoticeText = L(key);
        HasNotice = true;
    }

    /// <summary>版本号排序：解析失败的模组排在最后，避免整个列表崩掉。</summary>
    private sealed class VersionComparer : IComparer<SemanticVersion?>
    {
        public static VersionComparer Instance { get; } = new();

        public int Compare(SemanticVersion? left, SemanticVersion? right) => (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            _ => left.CompareTo(right)
        };
    }
}
