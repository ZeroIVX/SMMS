using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Profiles;
using SMMS.Core.Updates;
using SMMS.Core.Versioning;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>「可更新模组」表格里的一行。</summary>
public sealed class ModUpdateRowViewModel(
    string name,
    string versionChangeText,
    string kindText,
    bool isMajor,
    bool isIncompatible,
    string updateText,
    IRelayCommand updateCommand)
{
    public string Name { get; } = name;

    public string VersionChangeText { get; } = versionChangeText;

    public string KindText { get; } = kindText;

    public bool IsMajor { get; } = isMajor;

    public bool IsIncompatible { get; } = isIncompatible;

    public string UpdateText { get; } = updateText;

    public IRelayCommand UpdateCommand { get; } = updateCommand;
}

/// <summary>
/// 版本与更新页：SMAPI 版本对比、可更新模组清单、可回滚节点（复用 <see cref="BackupsPageViewModel"/>）。
/// 应用不代下文件、也没有写入式升级能力，所以这里不做任何「一键升级」的假动作：
/// 只提供真实的下载页跳转、真实的重新检查与真实的回滚。
/// </summary>
public sealed partial class UpdatesPageViewModel : PageViewModelBase
{
    /// <summary>SMAPI 官方下载页（安装器也在这一页）。</summary>
    public const string SmapiDownloadUrl = "https://smapi.io/";

    private readonly SmapiReleaseService _smapiReleases;
    private readonly GameInstallationService _games;
    private readonly ProfileService _profiles;
    private readonly UpdateCheckService _updateChecker;
    private readonly ModToggleService _toggle;
    private readonly ILogger<UpdatesPageViewModel> _logger;

    private GameInstallation? _game;
    private SmapiRelease? _release;
    private UpdateCheckResult _updateResult = UpdateCheckResult.Empty;
    private DateTimeOffset? _lastChecked;
    private string? _errorKey;
    private string? _statusKey;

    public UpdatesPageViewModel(
        SmapiReleaseService smapiReleases,
        GameInstallationService games,
        ProfileService profiles,
        UpdateCheckService updateChecker,
        ModToggleService toggle,
        BackupsPageViewModel snapshots,
        ILocalizationService localization,
        ILogger<UpdatesPageViewModel> logger)
        : base(localization)
    {
        _smapiReleases = smapiReleases;
        _games = games;
        _profiles = profiles;
        _updateChecker = updateChecker;
        _toggle = toggle;
        _logger = logger;
        Snapshots = snapshots;

        _profiles.ProfilesChanged += OnSourceChanged;
        _games.InstallationChanged += OnSourceChanged;

        Refresh();
        _ = RefreshAsync();
    }

    /// <summary>可回滚节点：直接复用备份页的 ViewModel（同一份数据与回滚命令）。</summary>
    public BackupsPageViewModel Snapshots { get; }

    public ObservableCollection<ModUpdateRowViewModel> Updates { get; } = [];

    /// <summary>供主窗口把确认对话框接到本页（转发给备份页 ViewModel）。</summary>
    public Func<ConfirmDialogViewModel, Task<bool>>? ConfirmRequested
    {
        get => Snapshots.ConfirmRequested;
        set => Snapshots.ConfirmRequested = value;
    }

    [ObservableProperty]
    public partial int UpdateCount { get; set; }

    [ObservableProperty]
    public partial bool HasUpdates { get; set; }

    [ObservableProperty]
    public partial bool HasSnapshots { get; set; }

    [ObservableProperty]
    public partial bool HasSmapiUpdate { get; set; }

    [ObservableProperty]
    public partial string SmapiTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SmapiNoteText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModsTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>按当前数据重建表格与统计（不发起网络请求）。</summary>
    public void Refresh()
    {
        Updates.Clear();
        // SMAPI 是运行环境，由上方卡片单独覆盖，不进模组表格。
        foreach (var update in _updateResult.Mods.Where(update => update.HasUpdate && !IsSmapiEntry(update)))
        {
            Updates.Add(CreateRow(update));
        }

        UpdateCount = Updates.Count;
        HasUpdates = UpdateCount > 0;
        HasSnapshots = Snapshots.Items.Count > 0;
        ModsTitleText = Format("updates.mods.title", [UpdateCount.ToString(CultureInfo.CurrentCulture)]);

        UpdateSmapiTexts();
        UpdateSubtitle();
        RecheckText = IsBusy ? L("updates.checking") : L("updates.recheck");
    }

    [ObservableProperty]
    public partial string RecheckText { get; set; } = string.Empty;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        HasError = false;
        HasStatus = false;

        try
        {
            var profile = _profiles.GetActive();
            _game = profile is null ? _games.Resolve() : _games.ResolveForProfile(profile);

            _release = await _smapiReleases.GetLatestAsync().ConfigureAwait(true);

            if (profile is null)
            {
                _updateResult = UpdateCheckResult.Empty;
            }
            else
            {
                var disabledPath = _toggle.GetDisabledPath(profile);
                _updateResult = await _updateChecker.CheckAsync(profile, disabledPath).ConfigureAwait(true);
            }

            if (_updateResult is { Succeeded: false, Errors.Count: > 0 })
            {
                ShowError("updates.error.check");
            }

            _lastChecked = _updateResult.Succeeded ? _updateResult.CheckedAt : DateTimeOffset.Now;
            Snapshots.Refresh();
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "版本与更新页检查失败");
            ShowError("updates.error.check");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>「打开 SMAPI 下载页」：SMMS 不代下文件，只把用户送到官方下载页。</summary>
    [RelayCommand]
    public void OpenSmapiDownload() => OpenUrl(SmapiDownloadUrl);

    [RelayCommand]
    public void OpenReleaseNotes()
    {
        if (string.IsNullOrWhiteSpace(_release?.PageUrl))
        {
            ShowStatus("updates.smapi.noLink");
            return;
        }

        OpenUrl(_release.PageUrl);
    }

    public override void RefreshTexts()
    {
        Refresh();
        Snapshots.RefreshTexts();

        if (_errorKey is { } errorKey)
        {
            ErrorText = L(errorKey);
        }

        if (_statusKey is { } statusKey)
        {
            StatusText = L(statusKey);
        }
    }

    private ModUpdateRowViewModel CreateRow(ModUpdateInfo update)
    {
        var currentText = string.IsNullOrWhiteSpace(update.CurrentVersion) ? L("settings.unknown") : update.CurrentVersion;
        var suggestedText = update.SuggestedVersion ?? "—";
        var isMajor = IsMajorUpgrade(update.CurrentVersion, update.SuggestedVersion);

        return new ModUpdateRowViewModel(
            string.IsNullOrWhiteSpace(update.Name) ? update.UniqueId : update.Name,
            Format("updates.mods.versionChange", [currentText, suggestedText]),
            isMajor ? L("updates.mods.kind.major") : L("updates.mods.kind.normal"),
            isMajor,
            update.IsIncompatible,
            L("updates.mods.openPage"),
            new RelayCommand(() => OpenUpdatePage(update)));
    }

    private void UpdateSmapiTexts()
    {
        var installed = _game?.IsSmapiInstalled == true && _game.SmapiVersion is not null;
        var currentText = _game?.SmapiVersion?.ToString() ?? L("settings.unknown");
        var latest = _release?.Version;

        if (latest is null)
        {
            HasSmapiUpdate = false;
            SmapiTitleText = installed
                ? Format("updates.smapi.currentOnly", [currentText])
                : L("updates.smapi.unknown");
            SmapiNoteText = L("settings.smapiCheckFailed");
            return;
        }

        var latestText = latest.ToString();

        if (!installed)
        {
            HasSmapiUpdate = true;
            SmapiTitleText = Format("updates.smapi.notInstalled", [latestText]);
        }
        else if (SemanticVersion.TryParseLenient(_game!.SmapiVersion, out var installedVersion) &&
                 latest.IsNewerThan(installedVersion))
        {
            HasSmapiUpdate = true;
            SmapiTitleText = Format("updates.smapi.title", [currentText, latestText]);
        }
        else
        {
            HasSmapiUpdate = false;
            SmapiTitleText = Format("updates.smapi.upToDate", [currentText]);
        }

        SmapiNoteText = HasSmapiUpdate
            ? L("updates.smapi.guide")
            : L("updates.smapi.noteUpToDate");
    }

    /// <summary>SMAPI 自身不是普通模组：表格里不列它（上方卡片已单独覆盖）。</summary>
    private static bool IsSmapiEntry(ModUpdateInfo update) =>
        update.UniqueId.Contains("smapi", StringComparison.OrdinalIgnoreCase) ||
        (update.Name?.Contains("SMAPI", StringComparison.OrdinalIgnoreCase) ?? false);

    private void UpdateSubtitle()
    {
        var smapiPart = _release is null
            ? L("updates.subtitle.smapiUnknown")
            : HasSmapiUpdate
                ? L("updates.subtitle.smapiNew")
                : L("updates.subtitle.smapiOk");

        var modsPart = Format("updates.subtitle.mods", [UpdateCount.ToString(CultureInfo.CurrentCulture)]);
        var checkedAt = _lastChecked?.ToString("HH:mm", CultureInfo.CurrentCulture) ?? "—";

        SetHeaderText(L("nav.updates"), Format("updates.subtitle", [smapiPart, modsPart, checkedAt]));
    }

    private static bool IsMajorUpgrade(string? current, string? suggested)
    {
        if (!SemanticVersion.TryParseLenient(current, out var currentVersion) ||
            !SemanticVersion.TryParseLenient(suggested, out var suggestedVersion))
        {
            return false;
        }

        return suggestedVersion.Major != currentVersion.Major || suggestedVersion.Minor != currentVersion.Minor;
    }

    private void OpenUpdatePage(ModUpdateInfo update)
    {
        if (string.IsNullOrWhiteSpace(update.UpdateUrl))
        {
            ShowStatus("updates.mods.noLink");
            return;
        }

        OpenUrl(update.UpdateUrl);
    }

    private void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowStatus("updates.openFailed");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
            ShowStatus("updates.openFailed");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
            ShowStatus("updates.openFailed");
        }
    }

    private string Format(string key, IReadOnlyList<string> arguments)
    {
        var template = L(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, [.. arguments]);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private void ShowError(string key)
    {
        _errorKey = key;
        HasError = true;
        ErrorText = L(key);
    }

    private void ShowStatus(string key)
    {
        _statusKey = key;
        HasStatus = true;
        StatusText = L(key);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Refresh();

    public override void Dispose()
    {
        _profiles.ProfilesChanged -= OnSourceChanged;
        _games.InstallationChanged -= OnSourceChanged;
        base.Dispose();
    }
}
