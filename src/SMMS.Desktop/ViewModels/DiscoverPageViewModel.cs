using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Nexus;
using SMMS.Core.Ports;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Translation;

namespace SMMS.Desktop.ViewModels;

/// <summary>
/// 列表缩略图用的渐变配色。设计稿里每个模组/美化包左边都有一个彩色方块，
/// 颜色按 id 稳定取模，保证同一项每次进来颜色一致（不随排序或翻页跳色）。
/// 这 5 组颜色是纯装饰色，没有语义，因此不进 Tokens 令牌层。
/// </summary>
internal static class ThumbnailPalette
{
    private static readonly (string Start, string End)[] Pairs =
    [
        ("#6FA45C", "#3F6B45"), // 苔绿
        ("#6E7DF0", "#4650B8"), // 蓝紫
        ("#8F74DE", "#5A3D99"), // 紫
        ("#3FA79C", "#2A6B70"), // 青
        ("#C7954E", "#7E5A2E")  // 棕
    ];

    private static readonly IBrush[] Brushes = [.. Pairs.Select(pair => Create(pair.Start, pair.End))];

    /// <summary>按整数 id 取色（Nexus 模组号）。</summary>
    public static IBrush Pick(int seed) => Brushes[(int)((uint)seed % (uint)Brushes.Length)];

    /// <summary>按字符串 id 取色（美化包的 Guid）。</summary>
    public static IBrush Pick(string? identity) => Brushes[StableHash(identity) % Brushes.Length];

    /// <summary>缩略图里的单字：取第一个字母/数字/汉字，忽略空格与标点。</summary>
    public static string Glyph(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                return char.ToUpperInvariant(ch).ToString();
            }
        }

        return name.Trim()[..1].ToUpperInvariant();
    }

    private static int StableHash(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        unchecked
        {
            var hash = 17;

            foreach (var ch in text)
            {
                hash = (hash * 31) + ch;
            }

            return (int)((uint)hash % (uint)Brushes.Length);
        }
    }

    private static LinearGradientBrush Create(string start, string end) => new LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new GradientStop(Color.Parse(start), 0),
            new GradientStop(Color.Parse(end), 1)
        }
    };
}

/// <summary>左侧分类筛选项。<see cref="ApiName"/> 为 null 表示不筛选。</summary>
public sealed partial class NexusCategoryOption(string? apiName, string nameKey) : ViewModelBase
{
    public string? ApiName { get; } = apiName;

    public string NameKey { get; } = nameKey;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    /// <summary>当前结果的条数，只有「全部模组」显示。</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    public bool ShowCount => CountText.Length > 0;

    partial void OnCountTextChanged(string value) => OnPropertyChanged(nameof(ShowCount));
}

/// <summary>排序选项。</summary>
public sealed partial class NexusSortOption(NexusSortField field, string nameKey) : ViewModelBase
{
    public NexusSortField Field { get; } = field;

    public string NameKey { get; } = nameKey;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;
}

 /// <summary>结果列表里的一行。</summary>
public sealed partial class NexusItemViewModel : ViewModelBase
{
    public NexusItemViewModel(
        NexusModInfo info,
        string downloadsText,
        bool isInstalled,
        bool hasChinese,
        bool isRuntime = false,
        string? runtimeVersion = null)
    {
        IsRuntimeEnvironment = isRuntime;
        RuntimeVersion = runtimeVersion;
        Info = info;
        Name = info.Name;
        VersionText = $"v{info.Version}";
        AuthorText = string.IsNullOrWhiteSpace(info.Author) ? "—" : info.Author!;
        SummaryText = Truncate(info.Summary);
        DisplaySummary = SummaryText;
        CategoryText = string.IsNullOrWhiteSpace(info.Category) ? string.Empty : info.Category!;
        HasCategory = !string.IsNullOrWhiteSpace(info.Category);
        DownloadsText = downloadsText;
        PageUrl = info.PageUrl;
        IdText = $"#{info.ModId}";
        MetaText = $"{AuthorText} · {downloadsText} · {VersionText}";
        IsInstalled = isInstalled;
        HasChineseTranslation = hasChinese;
        ThumbBrush = ThumbnailPalette.Pick(info.ModId);
        ThumbGlyph = ThumbnailPalette.Glyph(Name);
    }

    public NexusModInfo Info { get; }

    public string Name { get; }

    public string VersionText { get; }

    public string AuthorText { get; }

    public string SummaryText { get; }

    public string CategoryText { get; }

    public bool HasCategory { get; }

    public string DownloadsText { get; }

    /// <summary>副行：作者 · 下载量 · 版本。数据源没有更新时间和体积，不伪造。</summary>
    public string MetaText { get; }

    public string PageUrl { get; }

    public string IdText { get; }

    /// <summary>缩略图渐变底色，按模组号稳定映射。</summary>
    public IBrush ThumbBrush { get; }

    /// <summary>缩略图中心那个字。</summary>
    public string ThumbGlyph { get; }

    public bool IsInstalled { get; }

    public bool HasChineseTranslation { get; }

    /// <summary>运行环境条目（SMAPI 等）：它是「跑模组的前提」，不是可安装的模组。</summary>
    public bool IsRuntimeEnvironment { get; }

    /// <summary>普通模组条目：显示「已安装」标记；运行环境显示安装状态文本而不是安装按钮。</summary>
    public bool IsRegularMod => !IsRuntimeEnvironment;

    /// <summary>本地 SMAPI 版本号，未安装时为 null。</summary>
    public string? RuntimeVersion { get; }

    /// <summary>运行环境的安装状态文案（「已安装 4.5.1」/「未安装」），随语言刷新。</summary>
    [ObservableProperty]
    public partial string RuntimeStatusText { get; set; } = string.Empty;

    /// <summary>当前展示的摘要。翻译后替换成中文。</summary>
    [ObservableProperty]
    public partial string DisplaySummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTranslated { get; set; }

    [ObservableProperty]
    public partial bool IsTranslating { get; set; }

    /// <summary>「含中文」绿胶囊：已翻译的摘要，或本地已装且带中文的版本。运行环境不显示。</summary>
    public bool ShowChinesePill => !IsRuntimeEnvironment && (IsTranslated || HasChineseTranslation);

    partial void OnIsTranslatedChanged(bool value) => OnPropertyChanged(nameof(ShowChinesePill));

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        // 在数据层截断而不是靠 TextBlock.MaxLines：后者在这个 Avalonia 版本里不生效，
        // 长摘要会把卡片撑到三四行，列表高度参差不齐。
        var clean = text.ReplaceLineEndings(" ").Trim();
        return clean.Length <= 140 ? clean : clean[..140] + "…";
    }
}

/// <summary>
/// 发现页。一个搜索框同时承担三种输入：关键词、N 网尾号、模组页链接；
/// 分类、排序、方向是同一次查询的不同部分，任何一项变化都合并成一个
/// <see cref="NexusQuery"/> 重新请求，避免互相覆盖。
/// </summary>
public partial class DiscoverPageViewModel : PageViewModelBase
{
    private const int FeedSize = 20;

    private static readonly (string ApiName, string NameKey)[] CategoryNames =
    [
        ("Gameplay Mechanics", "discover.category.gameplay"),
        ("User Interface", "discover.category.ui"),
        ("Items", "discover.category.items"),
        ("Maps", "discover.category.maps"),
        ("Characters", "discover.category.characters"),
        ("Visuals and Graphics", "discover.category.graphics"),
        ("Audio", "discover.category.audio"),
        ("Modding Tools", "discover.category.tools"),
        ("Buildings", "discover.category.buildings"),
        ("Expansions", "discover.category.expansions"),
        ("Miscellaneous", "discover.category.misc")
    ];

    /// <summary>已知的「运行环境」类 N 网条目：SMAPI 本体（2400），以及名称里带 SMAPI 的条目。</summary>
    private const int SmapiModId = 2400;

    private readonly INexusCatalog _catalog;
    private readonly TranslationService _translation;
    private readonly GameInstallationService _games;
    private readonly ProfileService _profiles;
    private readonly ModLibraryService _library;
    private readonly ModToggleService _toggle;
    private readonly ILogger<DiscoverPageViewModel> _logger;

    // 已安装模组的 N 网尾号，用来把按钮换成「已安装」。
    private readonly HashSet<string> _installedNexusIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chineseNexusIds = new(StringComparer.Ordinal);

    private int _requestId;
    private string? _errorKey;
    private bool _initialized;
    private bool _singleResult;

    /// <summary>Refresh 时解析一次的本地 SMAPI 版本，供运行环境条目复用。</summary>
    private string? _smapiVersion;

    public DiscoverPageViewModel(
        INexusCatalog catalog,
        TranslationService translation,
        GameInstallationService games,
        ProfileService profiles,
        ModLibraryService library,
        ModToggleService toggle,
        ILocalizationService localization,
        ILogger<DiscoverPageViewModel> logger)
        : base(localization)
    {
        _catalog = catalog;
        _translation = translation;
        _games = games;
        _profiles = profiles;
        _library = library;
        _toggle = toggle;
        _logger = logger;

        Categories.Add(new NexusCategoryOption(null, "discover.allMods"));

        foreach (var (apiName, nameKey) in CategoryNames)
        {
            Categories.Add(new NexusCategoryOption(apiName, nameKey));
        }

        // 只保留设计稿里的三项：下载量 / 最近更新 / 点赞数。
        SortOptions.Add(new NexusSortOption(NexusSortField.Downloads, "discover.sortDownloads"));
        SortOptions.Add(new NexusSortOption(NexusSortField.UpdatedAt, "discover.sortUpdated"));
        SortOptions.Add(new NexusSortOption(NexusSortField.Endorsements, "discover.sortEndorsements"));

        LocalizeOptions();

        // 顶栏标题/副标题：页面构造时先给一次，避免首帧顶栏空白（语言切换时 RefreshTexts 会再刷）。
        SetHeader("nav.discover", "discover.subtitle");

        _initialized = true;

        SelectedCategory = Categories[0];
        SelectedSort = SortOptions[0];

        Refresh();
    }

    public ObservableCollection<NexusItemViewModel> Items { get; } = [];

    public ObservableCollection<NexusCategoryOption> Categories { get; } = [];

    public ObservableCollection<NexusSortOption> SortOptions { get; } = [];

    /// <summary>顶部那个大搜索框的内容（关键词 / 尾号 / 链接都行）。</summary>
    [ObservableProperty]
    public partial string SearchInput { get; set; } = string.Empty;

    /// <summary>选择持久化 / 截图脚本用的兼容入口，等价于 <see cref="SearchInput"/>。</summary>
    public string SearchText
    {
        get => SearchInput;
        set => SearchInput = value;
    }

    [ObservableProperty]
    public partial NexusCategoryOption? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial NexusSortOption? SelectedSort { get; set; }

    [ObservableProperty]
    public partial bool Descending { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>结果卡头部标题，例如「搜索结果 · 20 个模组」。</summary>
    [ObservableProperty]
    public partial string ResultTitle { get; set; } = string.Empty;

    /// <summary>结果卡头部右侧的排序说明。</summary>
    [ObservableProperty]
    public partial string SortCaption { get; set; } = string.Empty;

    public string DirectionGlyph => Descending ? "↓" : "↑";

    partial void OnSearchInputChanged(string value) => OnPropertyChanged(nameof(SearchText));

    // 排序 / 分类改变立刻重查；关键词靠回车或按钮触发，避免每敲一个字都发请求。
    partial void OnSelectedSortChanged(NexusSortOption? value) => TriggerQuery();

    partial void OnSelectedCategoryChanged(NexusCategoryOption? value) => TriggerQuery();

    partial void OnDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(DirectionGlyph));
        TriggerQuery();
    }

    [RelayCommand]
    private void ToggleDirection() => Descending = !Descending;

    /// <summary>搜索按钮 / 回车：自动判别输入的是关键词、尾号还是链接。</summary>
    [RelayCommand]
    private async Task RunSearchAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var raw = (SearchInput ?? string.Empty).Trim();

        if (raw.Length == 0)
        {
            await ExecuteQueryAsync();
            return;
        }

        var looksLikeId = raw.All(char.IsDigit)
                          || raw.Contains("nexusmods", StringComparison.OrdinalIgnoreCase);

        if (!looksLikeId)
        {
            await ExecuteQueryAsync();
            return;
        }

        var digits = new string(raw.Where(char.IsDigit).ToArray());

        if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modId)
            || modId <= 0)
        {
            ShowError("discover.error.invalidId");
            return;
        }

        await SearchByIdAsync(modId);
    }

    /// <summary>兼容旧脚本 / 截图演示：按当前关键词与筛选条件重新查一次。</summary>
    [RelayCommand]
    private Task RunQueryAsync() => ExecuteQueryAsync();

    private void TriggerQuery()
    {
        if (_initialized)
        {
            _ = ExecuteQueryAsync();
        }
    }

    private async Task ExecuteQueryAsync()
    {
        var query = new NexusQuery
        {
            Keyword = string.IsNullOrWhiteSpace(SearchInput) ? null : SearchInput.Trim(),
            Category = SelectedCategory?.ApiName,
            Sort = SelectedSort?.Field ?? NexusSortField.Downloads,
            Descending = Descending,
            Count = FeedSize
        };

        var requestId = ++_requestId;
        IsBusy = true;

        try
        {
            var items = await _catalog.QueryAsync(query).ConfigureAwait(true);

            if (requestId != _requestId)
            {
                return;
            }

            Items.Clear();

            foreach (var info in items)
            {
                Items.Add(CreateItem(info));
            }

            // 目录接口失败和「没有结果」返回的都是空列表，这里无法区分，
            // 所以空结果只显示空态，不冒红错误条。
            _singleResult = false;
            IsEmpty = Items.Count == 0;
            HasError = false;
            _errorKey = null;
            ErrorText = string.Empty;
            Categories[0].CountText = Items.Count.ToString("N0", CultureInfo.CurrentCulture);
            ResultTitle = string.Format(CultureInfo.CurrentCulture, L("discover.resultCount"), Items.Count);
            SortCaption = BuildSortCaption();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "查询 Nexus 模组失败");
            ShowError("discover.error.feedFailed");
        }
        finally
        {
            if (requestId == _requestId)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>按尾号查询单个模组。</summary>
    private async Task SearchByIdAsync(int modId)
    {
        var requestId = ++_requestId;
        IsBusy = true;

        try
        {
            var info = await _catalog.GetModAsync(modId).ConfigureAwait(true);

            if (requestId != _requestId)
            {
                return;
            }

            if (info is null)
            {
                ShowError("discover.error.notFound");
                return;
            }

            _singleResult = true;
            HasError = false;
            _errorKey = null;
            ErrorText = string.Empty;
            Items.Clear();
            Items.Add(CreateItem(info));
            IsEmpty = false;
            Categories[0].CountText = string.Empty;
            ResultTitle = L("discover.result");
            SortCaption = string.Empty;
        }
        finally
        {
            if (requestId == _requestId)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>把摘要翻成中文。按条目点击触发，不做批量翻译以免消耗额度。</summary>
    [RelayCommand]
    private async Task TranslateAsync(NexusItemViewModel? item)
    {
        if (item is null || item.IsTranslating || item.IsTranslated)
        {
            return;
        }

        item.IsTranslating = true;

        try
        {
            var result = await _translation.TranslateToChineseAsync(item.SummaryText).ConfigureAwait(true);

            if (!result.Success || string.IsNullOrWhiteSpace(result.Text))
            {
                ShowError(result.ErrorKey ?? "translate.error.failed");
                return;
            }

            item.DisplaySummary = result.Text;
            item.IsTranslated = true;
            HasError = false;
        }
        finally
        {
            item.IsTranslating = false;
        }
    }

    /// <summary>「安装」：没有公开下载接口，打开模组页让用户下载后拖进来。</summary>
    [RelayCommand]
    private void Install(NexusItemViewModel? item) => OpenUrl(item?.PageUrl);

    /// <summary>用系统浏览器打开模组页。</summary>
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

    public override void RefreshTexts()
    {
        SetHeader("nav.discover", "discover.subtitle");

        LocalizeOptions();

        if (_errorKey is { } key)
        {
            ErrorText = L(key);
        }

        // 运行环境条目的「已安装 x.y.z / 未安装」是 VM 里拼的，语言切换时一起刷。
        foreach (var item in Items)
        {
            if (item.IsRuntimeEnvironment)
            {
                item.RuntimeStatusText = BuildRuntimeStatusText(item.RuntimeVersion);
            }
        }

        Categories[0].CountText = _singleResult || Items.Count == 0
            ? Categories[0].CountText
            : Items.Count.ToString("N0", CultureInfo.CurrentCulture);

        ResultTitle = _singleResult
            ? L("discover.result")
            : string.Format(CultureInfo.CurrentCulture, L("discover.resultCount"), Items.Count);

        SortCaption = _singleResult ? string.Empty : BuildSortCaption();
    }

    /// <summary>页面重新进入时调用：重新读已安装列表与本地 SMAPI 版本，再按当前条件查一次。</summary>
    public void Refresh()
    {
        LoadInstalledIds();
        _smapiVersion = ResolveSmapiVersion();
        _ = ExecuteQueryAsync();
    }

    private void LocalizeOptions()
    {
        foreach (var category in Categories)
        {
            category.DisplayName = L(category.NameKey);
        }

        foreach (var sort in SortOptions)
        {
            sort.DisplayName = L(sort.NameKey);
        }
    }

    private string BuildSortCaption() =>
        string.Format(
            CultureInfo.CurrentCulture,
            L("discover.feedTitle"),
            SelectedSort?.DisplayName ?? L("discover.sortDownloads"),
            DirectionGlyph);

    /// <summary>把当前配置档里已装模组的 N 网尾号读出来，用于「已安装 / 含中文」标记。</summary>
    private void LoadInstalledIds()
    {
        _installedNexusIds.Clear();
        _chineseNexusIds.Clear();

        try
        {
            var profile = _profiles.GetActive();

            if (profile is null || !Directory.Exists(profile.ModsPath))
            {
                return;
            }

            var snapshot = _library.Load(profile.ModsPath, _toggle.GetDisabledPath(profile));

            foreach (var mod in snapshot.Mods)
            {
                if (string.IsNullOrWhiteSpace(mod.NexusId))
                {
                    continue;
                }

                var id = mod.NexusId.Trim();
                _installedNexusIds.Add(id);

                if (mod.HasChineseTranslation)
                {
                    _chineseNexusIds.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取已安装模组列表失败，跳过「已安装」标记");
        }
    }

    private NexusItemViewModel CreateItem(NexusModInfo info)
    {
        var key = info.ModId.ToString(CultureInfo.InvariantCulture);
        var isRuntime = IsRuntimeEnvironment(info);

        var item = new NexusItemViewModel(
            info,
            FormatDownloads(info.Downloads),
            // 运行环境不算已装模组，否则 SMAPI 会被当成「已安装模组」。
            !isRuntime && _installedNexusIds.Contains(key),
            _chineseNexusIds.Contains(key),
            isRuntime,
            isRuntime ? _smapiVersion : null);

        if (isRuntime)
        {
            item.RuntimeStatusText = BuildRuntimeStatusText(item.RuntimeVersion);
        }

        return item;
    }

    /// <summary>SMAPI 系列是运行环境，不是可安装模组：按固定 id + 名称双保险识别。</summary>
    private static bool IsRuntimeEnvironment(NexusModInfo info) =>
        info.ModId == SmapiModId
        || (!string.IsNullOrWhiteSpace(info.Name)
            && info.Name.Contains("SMAPI", StringComparison.OrdinalIgnoreCase));

    /// <summary>本地 SMAPI 版本：优先当前配置档对应的安装，其次主安装。</summary>
    /// <remarks>
    /// 在 <see cref="Refresh"/> 里算一次存字段，而不是每条结果都去读盘——
    /// 一屏 20 条会触发 20 次 FileVersionInfo 读取。
    /// </remarks>
    private string? ResolveSmapiVersion()
    {
        try
        {
            var profile = _profiles.GetActive();
            var game = profile is null
                ? _games.Resolve()
                : _games.ResolveForProfile(profile) ?? _games.Resolve();

            return game?.SmapiVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取 SMAPI 版本失败，按未安装处理");
            return null;
        }
    }

    /// <summary>运行环境条目的状态文案：有版本号显示「已安装 x.y.z」，否则「未安装」。</summary>
    private string BuildRuntimeStatusText(string? version) =>
        string.IsNullOrWhiteSpace(version)
            ? L("discover.runtimeMissing")
            : string.Format(CultureInfo.CurrentCulture, L("discover.runtimeInstalled"), version);

    private string FormatDownloads(long downloads)
    {
        // 中文习惯统一用「万」，不区分千万/百万，避免同一列表里量纲跳来跳去。
        return downloads >= 10_000
            ? string.Format(CultureInfo.CurrentCulture, L("discover.downloadsTenK"), downloads / 10_000d)
            : string.Format(CultureInfo.CurrentCulture, L("discover.downloadsCount"), downloads);
    }

    private void ShowError(string key)
    {
        _errorKey = key;
        ErrorText = L(key);
        HasError = true;
    }
}
