using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Vanity;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Importing;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Vanity;

namespace SMMS.Desktop.ViewModels;

/// <summary>左栏「资源替换」列表里的一套替换包。</summary>
public sealed partial class VanityItemViewModel : ViewModelBase
{
    public VanityItemViewModel(VanityPackage package, string metaText, bool isActive, string noteText)
    {
        PackageId = package.Id;
        Name = package.Name;
        MetaText = metaText;
        NoteText = noteText;
        ThumbBrush = ThumbnailPalette.Pick(package.Id);
        ThumbGlyph = ThumbnailPalette.Glyph(package.Name);
        IsActive = isActive;
    }

    public string PackageId { get; }

    public string Name { get; }

    public string MetaText { get; }

    /// <summary>包里自带的说明（导入时作者写的更新日志之类），压成一行放在副行下面。</summary>
    public string NoteText { get; }

    public bool HasNote => NoteText.Length > 0;

    /// <summary>64×64 圆角缩略图底色，按包 id 稳定取色。</summary>
    public IBrush ThumbBrush { get; }

    public string ThumbGlyph { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public bool IsInactive => !IsActive;

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(IsInactive));
}

/// <summary>右栏「额外资源包」列表里的一行，对应一个 Content Patcher 内容包模组。</summary>
public sealed partial class ContentPackItemViewModel : ViewModelBase
{
    public ContentPackItemViewModel(InstalledMod mod, string metaText, string descriptionText)
    {
        Mod = mod;
        Name = mod.Name;
        MetaText = metaText;
        DescriptionText = descriptionText;
        ThumbBrush = ThumbnailPalette.Pick(mod.UniqueId);
        ThumbGlyph = ThumbnailPalette.Glyph(mod.Name);
        IsEnabled = mod.IsEnabled;
    }

    public InstalledMod Mod { get; }

    public string Name { get; }

    /// <summary>副行：版本 · 依赖框架（可多个共存，所以不写素材数）。</summary>
    public string MetaText { get; }

    public string DescriptionText { get; }

    public bool HasDescription => DescriptionText.Length > 0;

    public IBrush ThumbBrush { get; }

    public string ThumbGlyph { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>关闭的行整体压暗（设计稿里禁用行是灰的）。</summary>
    public bool IsDisabled => !IsEnabled;

    public double RowOpacity => IsEnabled ? 1d : 0.55d;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(RowOpacity));
    }
}

/// <summary>
/// 美化包页。左栏是「资源替换」——直接覆盖游戏 Content 目录的 XNB，
/// 同一时间只允许一套生效；右栏是「额外资源包」——Content Patcher 内容包，
/// 本质是 Mods 目录里的模组，可以多个共存，开关复用模组库那套。
/// </summary>
public partial class VanityPageViewModel : PageViewModelBase
{
    private readonly VanityService _vanity;
    private readonly ProfileService _profiles;
    private readonly GameInstallationService _games;
    private readonly GameLaunchService _launch;
    private readonly ModLibraryService _library;
    private readonly ModToggleService _toggle;
    private readonly DropImportService _dropImport;
    private readonly ILogger<VanityPageViewModel> _logger;

    private string? _errorKey;

    public VanityPageViewModel(
        VanityService vanity,
        ProfileService profiles,
        GameInstallationService games,
        GameLaunchService launch,
        ModLibraryService library,
        ModToggleService toggle,
        DropImportService dropImport,
        ILocalizationService localization,
        ILogger<VanityPageViewModel> logger)
        : base(localization)
    {
        _vanity = vanity;
        _profiles = profiles;
        _games = games;
        _launch = launch;
        _library = library;
        _toggle = toggle;
        _dropImport = dropImport;
        _logger = logger;

        _profiles.ProfilesChanged += OnProfilesChanged;
        _games.InstallationChanged += OnInstallationChanged;

        Refresh();
    }

    public ObservableCollection<VanityItemViewModel> Packages { get; } = [];

    public ObservableCollection<ContentPackItemViewModel> ContentPacks { get; } = [];

    /// <summary>由 code-behind 注入的文件选择器（过滤 zip/7z/rar）。</summary>
    public Func<Task<string?>>? PickArchiveRequested { get; set; }

    /// <summary>内容包开关变化后通知模组库刷新。</summary>
    public event EventHandler? LibraryChanged;

    [ObservableProperty]
    public partial bool HasPackages { get; set; }

    [ObservableProperty]
    public partial bool HasContentPacks { get; set; }

    [ObservableProperty]
    public partial bool HasGame { get; set; }

    /// <summary>当前是否有替换包生效（决定状态条是绿色「生效中」还是中性「原版」）。</summary>
    [ObservableProperty]
    public partial bool IsActiveState { get; set; }

    [ObservableProperty]
    public partial string ActiveBannerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>操作成功后的绿条（导入结果之类），与错误条互斥。</summary>
    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string NoticeText { get; set; } = string.Empty;

    /// <summary>重新读取包列表 / 生效状态 / 额外资源包，并刷新顶栏副标题。</summary>
    [RelayCommand]
    public void Refresh()
    {
        var game = ResolveInstallation();
        HasGame = game is not null;

        Packages.Clear();
        ContentPacks.Clear();

        if (game is null)
        {
            IsActiveState = false;
            ActiveBannerText = L("vanity.error.noGame");
            HasPackages = false;
            LoadContentPacks();
            UpdateHeader();
            return;
        }

        VanityState state;

        try
        {
            state = _vanity.GetState(game);

            foreach (var package in _vanity.List(game))
            {
                Packages.Add(CreatePackageItem(package, state.ActivePackageId));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取美化包列表失败");
            ShowError("vanity.error.notFound");
            HasPackages = false;
            LoadContentPacks();
            UpdateHeader();
            return;
        }

        HasPackages = Packages.Count > 0;
        IsActiveState = state.IsActive;
        ActiveBannerText = state.IsActive
            ? string.Format(
                CultureInfo.CurrentCulture,
                L("vanity.activeBanner"),
                state.ActivePackageName ?? "—",
                state.AppliedFiles.Count)
            : L("vanity.activeBannerOriginal");

        if (state.IsActive && _vanity.IsAppliedButOverwritten(game))
        {
            ShowError("vanity.error.overwritten");
        }

        LoadContentPacks();
        UpdateHeader();
    }

    public override void RefreshTexts()
    {
        if (_errorKey is { } key)
        {
            ErrorText = L(key);
        }

        Refresh();
    }

    /// <summary>「应用」：把某套替换包覆盖到游戏里（会先备份原版）。</summary>
    [RelayCommand]
    private void ApplyPackage(VanityItemViewModel? item)
    {
        if (item?.PackageId is null || IsBusy)
        {
            return;
        }

        var game = ResolveInstallation();

        if (game is null)
        {
            ShowError("vanity.error.noGame");
            return;
        }

        if (_launch.IsGameProcessRunning())
        {
            ShowError("vanity.error.gameRunning");
            return;
        }

        var result = _vanity.Apply(game, item.PackageId);

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "vanity.error.applyFailed");
            Refresh();
            return;
        }

        ClearMessages();
        Refresh();
    }

    /// <summary>
    /// 资源替换包的开关：打开即应用（自动停用其他包），关闭即停用。
    /// 与右侧额外资源包的开关保持同一交互。
    /// </summary>
    [RelayCommand]
    private void TogglePackage(VanityItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        if (item.IsActive)
        {
            DeactivatePackage(item);
        }
        else
        {
            ApplyPackage(item);
        }
    }

    /// <summary>「停用」：当前生效的那套切回原版；未生效的直接忽略。</summary>
    [RelayCommand]
    private void DeactivatePackage(VanityItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsActive)
        {
            Revert();
        }
    }

    /// <summary>状态条右侧「切回原版」。</summary>
    [RelayCommand]
    private void Revert()
    {
        if (IsBusy)
        {
            return;
        }

        var game = ResolveInstallation();

        if (game is null)
        {
            ShowError("vanity.error.noGame");
            return;
        }

        if (_launch.IsGameProcessRunning())
        {
            ShowError("vanity.error.gameRunning");
            return;
        }

        var result = _vanity.Revert(game);

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "vanity.error.revertFailed");
            Refresh();
            return;
        }

        ClearMessages();
        Refresh();
    }

    /// <summary>删除一套替换包。命令名 DeleteCommand 被旧视图 code-behind 引用，方法名不能带 Async 后缀。</summary>
    [RelayCommand]
    private void Delete(VanityItemViewModel? item)
    {
        if (item?.PackageId is null)
        {
            return;
        }

        var game = ResolveInstallation();

        if (game is null)
        {
            ShowError("vanity.error.noGame");
            return;
        }

        var result = _vanity.Delete(game, item.PackageId);

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "vanity.error.deleteFailed");
            return;
        }

        ClearMessages();
        Refresh();
    }

    /// <summary>左栏「导入 XNB 包」：只走资源替换这条通道。</summary>
    [RelayCommand]
    private async Task ImportPackageAsync()
    {
        if (IsBusy || PickArchiveRequested is null)
        {
            return;
        }

        var path = await PickArchiveRequested().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var game = ResolveInstallation();

        if (game is null)
        {
            ShowError("vanity.error.noGame");
            return;
        }

        IsBusy = true;

        try
        {
            var result = _vanity.Import(game, path);

            if (!result.Success)
            {
                ShowError(result.ErrorKey ?? "vanity.error.invalidArchive");
                return;
            }

            ClearMessages();
            ShowNotice(string.Format(
                CultureInfo.CurrentCulture,
                L("vanity.imported"),
                result.Package?.Name ?? "—",
                result.FileCount));
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 右栏「导入内容包」：走统一的拖入导入通道，按包结构自动分流
    /// （含 manifest.json 的会进 Mods 目录成为内容包）。
    /// </summary>
    [RelayCommand]
    private async Task ImportContentPackAsync()
    {
        if (IsBusy || PickArchiveRequested is null)
        {
            return;
        }

        var path = await PickArchiveRequested().ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;

        try
        {
            var outcome = await _dropImport.ImportAsync(path).ConfigureAwait(true);

            if (!outcome.Success)
            {
                ShowErrorText(LocalizeOutcome(outcome));
                return;
            }

            ClearMessages();
            ShowNotice(LocalizeOutcome(outcome));
            Refresh();
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>内容包开关（右栏每一行的绿色 switch）。</summary>
    [RelayCommand]
    private void ToggleContentPackCommand(ContentPackItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // switch 是 OneWay 绑定，模型才是唯一真相：先翻转模型，再由绑定把 UI 拨过去。
        item.IsEnabled = !item.IsEnabled;
        ToggleContentPack(item);
    }

    /// <summary>旧视图用 ToggleSwitch 已经翻过一次状态，这里直接按 item.IsEnabled 落盘。</summary>
    public void ToggleContentPack(ContentPackItemViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        var profile = _profiles.GetActive();

        if (profile is null)
        {
            item.IsEnabled = !item.IsEnabled;
            ShowError("vanity.error.noGame");
            return;
        }

        var desired = item.IsEnabled;
        var result = _toggle.SetEnabled(profile, item.Mod, desired);

        if (!result.Success)
        {
            item.IsEnabled = !desired;
            ShowError(result.ErrorKey ?? "vanity.error.applyFailed");
            return;
        }

        ClearMessages();
        _profiles.SyncManifest(profile);
        LoadContentPacks();
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>旧视图 code-behind 的入口，保留编译兼容（新视图不再使用）。</summary>
    public void ToggleVanity(VanityItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsActive)
        {
            Revert();
        }
        else
        {
            ApplyPackage(item);
        }
    }

    private void LoadContentPacks()
    {
        ContentPacks.Clear();

        var profile = _profiles.GetActive();

        if (profile is null)
        {
            HasContentPacks = false;
            return;
        }

        ModLibrarySnapshot snapshot;

        try
        {
            snapshot = _library.Load(profile.ModsPath, _toggle.GetDisabledPath(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取内容包列表失败");
            HasContentPacks = false;
            return;
        }

        foreach (var mod in snapshot.Mods
                     .Where(mod => mod.IsContentPack)
                     .OrderBy(mod => mod.Name, StringComparer.CurrentCulture))
        {
            ContentPacks.Add(CreateContentPackItem(mod));
        }

        HasContentPacks = ContentPacks.Count > 0;
    }

    private VanityItemViewModel CreatePackageItem(VanityPackage package, string? activePackageId)
    {
        var meta = string.Format(
            CultureInfo.CurrentCulture,
            L("vanity.packageMetaRich"),
            package.FileCount,
            FormatSize(package.TotalBytes));

        meta += " · " + string.Format(
            CultureInfo.CurrentCulture,
            L("vanity.importedAtShort"),
            package.ImportedAt.ToLocalTime().ToString("MM/dd", CultureInfo.CurrentCulture));

        // 说明文字可能好几十行（作者写的更新日志），压成一行再显示，否则卡片会被撑高。
        var note = string.IsNullOrWhiteSpace(package.Note)
            ? string.Empty
            : package.Note.ReplaceLineEndings(" ").Trim();

        if (note.Length > 96)
        {
            note = note[..96] + "…";
        }

        return new VanityItemViewModel(
            package,
            meta,
            string.Equals(package.Id, activePackageId, StringComparison.Ordinal),
            note);
    }

    private ContentPackItemViewModel CreateContentPackItem(InstalledMod mod)
    {
        var version = mod.Version?.ToString() ?? "—";
        var framework = string.IsNullOrWhiteSpace(mod.ContentPackFor) ? mod.UniqueId : mod.ContentPackFor!;
        var meta = string.Format(CultureInfo.CurrentCulture, L("vanity.contentPackMeta"), version, Shorten(framework));

        return new ContentPackItemViewModel(mod, meta, FormatDescription(mod.Description));
    }

    /// <summary>"Pathoschild.ContentPatcher" 太长，列表副行只留最后一段。</summary>
    private static string Shorten(string value)
    {
        var index = value.LastIndexOf('.');

        return index >= 0 && index < value.Length - 1 ? value[(index + 1)..] : value;
    }

    private static string FormatDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var clean = description.ReplaceLineEndings(" ").Trim();

        return clean.Length <= 90 ? clean : clean[..90] + "…";
    }

    private string LocalizeOutcome(DropOutcome outcome)
    {
        var template = L(outcome.MessageKey);

        if (outcome.Arguments.Count == 0)
        {
            return template;
        }

        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, outcome.Arguments.ToArray());
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private void UpdateHeader()
    {
        PageTitle = L("vanity.title");
        PageSubtitle = string.Format(
            CultureInfo.CurrentCulture,
            L("vanity.subtitle"),
            Packages.Count,
            ContentPacks.Count);
    }

    private void ClearMessages()
    {
        HasError = false;
        ErrorText = string.Empty;
        _errorKey = null;
        HasNotice = false;
        NoticeText = string.Empty;
    }

    private void ShowError(string key)
    {
        _errorKey = key;
        ErrorText = L(key);
        HasError = true;
        HasNotice = false;
        NoticeText = string.Empty;
    }

    private void ShowErrorText(string text)
    {
        _errorKey = null;
        ErrorText = text;
        HasError = true;
        HasNotice = false;
        NoticeText = string.Empty;
    }

    private void ShowNotice(string text)
    {
        NoticeText = text;
        HasNotice = true;
        HasError = false;
        ErrorText = string.Empty;
        _errorKey = null;
    }

    private GameInstallation? ResolveInstallation()
    {
        var profile = _profiles.GetActive();

        return profile is null ? _games.Current : _games.ResolveForProfile(profile);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }

    private void OnProfilesChanged(object? sender, EventArgs e) => Refresh();

    private void OnInstallationChanged(object? sender, EventArgs e) => Refresh();

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
        _profiles.ProfilesChanged -= OnProfilesChanged;
        _games.InstallationChanged -= OnInstallationChanged;
        base.Dispose();
    }
}
