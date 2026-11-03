using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Translation;
using SMMS.Core.Versioning;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Network;
using SMMS.UseCases.Translation;
using SMMS.UseCases.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>供应商的一个凭证输入项。</summary>
public sealed partial class CredentialFieldViewModel : ViewModelBase
{
    public CredentialFieldViewModel(ProviderCredentialField field, string value)
    {
        Key = field.Key;
        Label = field.Label;
        IsSecret = field.IsSecret;
        Placeholder = field.Placeholder ?? string.Empty;
        Value = value;
    }

    public string Key { get; }

    public string Label { get; }

    public bool IsSecret { get; }

    public string Placeholder { get; }

    /// <summary>密钥字段用圆点遮住；普通字段传 \0 表示不遮。</summary>
    public char PasswordChar => IsSecret ? '●' : '\0';

    [ObservableProperty]
    public partial string Value { get; set; }
}

public partial class SettingsPageViewModel : PageViewModelBase
{
    private readonly GameInstallationService _games;
    private readonly NetworkProbeService _probe;
    private readonly SmapiReleaseService _smapiReleases;
    private readonly TranslationService _translation;
    private readonly ILogger<SettingsPageViewModel> _logger;
    private bool _suppressLanguageChange;

    public SettingsPageViewModel(
        GameInstallationService games,
        NetworkProbeService probe,
        SmapiReleaseService smapiReleases,
        TranslationService translation,
        ILocalizationService localization,
        ILogger<SettingsPageViewModel> logger)
        : base(localization)
    {
        _games = games;
        _probe = probe;
        _smapiReleases = smapiReleases;
        _translation = translation;
        _logger = logger;

        _suppressLanguageChange = true;
        foreach (var option in localization.AvailableLanguages)
        {
            Languages.Add(option);
        }

        SelectedLanguage = Languages.FirstOrDefault(option =>
                             string.Equals(option.Locale, localization.CurrentCulture.Name, StringComparison.OrdinalIgnoreCase))
                         ?? Languages.FirstOrDefault();
        _suppressLanguageChange = false;

        AppVersion = typeof(SettingsPageViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        LoadTranslationSettings();
        Refresh();
        UpdateHeader();
    }

    public ObservableCollection<LanguageOption> Languages { get; } = [];

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial string GamePathDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GameVersionDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SmapiVersionDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppVersion { get; set; } = string.Empty;

    // ---- SMAPI 版本 ----

    [ObservableProperty]
    public partial string SmapiStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSmapiUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingSmapi { get; set; }

    public string CheckSmapiButtonText => IsCheckingSmapi ? L("settings.smapiChecking") : L("settings.smapiCheck");

    private SmapiRelease? _latestSmapi;

    partial void OnIsCheckingSmapiChanged(bool value) => OnPropertyChanged(nameof(CheckSmapiButtonText));

    [RelayCommand]
    private async Task CheckSmapiAsync()
    {
        if (IsCheckingSmapi)
        {
            return;
        }

        IsCheckingSmapi = true;

        try
        {
            // 先重读本地安装再比对：玩家可能刚在外面升级了 SMAPI，
            // 不刷新的话本地版本仍是启动时的旧值，会一直误报「有新版本」。
            Refresh();

            _latestSmapi = await _smapiReleases.GetLatestAsync().ConfigureAwait(true);
            ApplySmapiStatus();
        }
        finally
        {
            IsCheckingSmapi = false;
        }
    }

    [RelayCommand]
    private void OpenSmapiDownload()
    {
        var url = _latestSmapi?.PageUrl ?? "https://github.com/Pathoschild/SMAPI/releases/latest";
        OpenExternal(url);
    }

    private void ApplySmapiStatus()
    {
        var currentText = _installation?.SmapiVersion;

        if (_latestSmapi is null)
        {
            SmapiStatusText = L("settings.smapiCheckFailed");
            HasSmapiUpdate = false;
            return;
        }

        // SmapiVersion 是展示用的字符串，比较前先解析成语义版本。
        var current = SemanticVersion.TryParse(currentText, out var parsed) ? parsed : null;

        if (current is null)
        {
            SmapiStatusText = string.Format(
                CultureInfo.CurrentCulture, L("settings.smapiNotInstalled"), _latestSmapi.Version);
            HasSmapiUpdate = true;
            return;
        }

        HasSmapiUpdate = _latestSmapi.Version.CompareTo(current) > 0;

        SmapiStatusText = HasSmapiUpdate
            ? string.Format(CultureInfo.CurrentCulture, L("settings.smapiOutdated"), current, _latestSmapi.Version)
            : string.Format(CultureInfo.CurrentCulture, L("settings.smapiUpToDate"), current);
    }

    private void OpenExternal(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
        }
    }

    // ---- 翻译服务 ----

    [ObservableProperty]
    public partial ITranslationProvider? SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string TranslationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsTestingTranslation { get; set; }

    [ObservableProperty]
    public partial bool NeedsForeignNetwork { get; set; }

    [ObservableProperty]
    public partial bool IsCustomProvider { get; set; }

    [ObservableProperty]
    public partial bool IsBaiduProvider { get; set; }

    public ObservableCollection<ITranslationProvider> Providers { get; } = [];

    /// <summary>当前供应商要求填写的凭证字段，随选择动态重建。</summary>
    public ObservableCollection<CredentialFieldViewModel> CredentialFields { get; } = [];

    partial void OnSelectedProviderChanged(ITranslationProvider? value) => RebuildCredentialFields();

    private void LoadTranslationSettings()
    {
        if (Providers.Count == 0)
        {
            foreach (var provider in _translation.Providers)
            {
                Providers.Add(provider);
            }
        }

        SelectedProvider = _translation.ActiveProvider;
        RebuildCredentialFields();
    }

    private void RebuildCredentialFields()
    {
        CredentialFields.Clear();

        var provider = SelectedProvider;
        if (provider is null)
        {
            NeedsForeignNetwork = false;
            IsCustomProvider = false;
            IsBaiduProvider = false;
            return;
        }

        NeedsForeignNetwork = provider.RequiresForeignNetwork;
        IsCustomProvider = string.Equals(provider.Id, "custom", StringComparison.OrdinalIgnoreCase);
        IsBaiduProvider = string.Equals(provider.Id, "baidu", StringComparison.OrdinalIgnoreCase);

        var stored = _games.Settings.TranslationCredentials.TryGetValue(provider.Id, out var values)
            ? values
            : [];

        foreach (var field in provider.Credentials)
        {
            CredentialFields.Add(new CredentialFieldViewModel(
                field,
                stored.TryGetValue(field.Key, out var value) ? value : string.Empty));
        }
    }

    [RelayCommand]
    private void SaveTranslation()
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }

        var settings = _games.Settings;
        settings.TranslationProvider = provider.Id;

        var values = new Dictionary<string, string>();

        foreach (var field in CredentialFields)
        {
            if (!string.IsNullOrWhiteSpace(field.Value))
            {
                values[field.Key] = field.Value.Trim();
            }
        }

        if (values.Count > 0)
        {
            settings.TranslationCredentials[provider.Id] = values;
        }
        else
        {
            settings.TranslationCredentials.Remove(provider.Id);
        }

        _games.SaveSettings();
        TranslationStatus = L("settings.translationSaved");
    }

    [RelayCommand]
    private async Task TestTranslationAsync()
    {
        if (IsTestingTranslation)
        {
            return;
        }

        IsTestingTranslation = true;

        try
        {
            // 先落盘，翻译服务读的是设置里的配置。
            SaveTranslation();

            var result = await _translation
                .TranslateToChineseAsync("The mod loader for Stardew Valley.")
                .ConfigureAwait(true);

            TranslationStatus = result.Success
                ? string.Format(CultureInfo.CurrentCulture, L("settings.translationTestOk"), result.Text)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    L("settings.translationTestFail"),
                    L(result.ErrorKey ?? "translate.error.failed"));
        }
        finally
        {
            IsTestingTranslation = false;
        }
    }

    [RelayCommand]
    private void OpenBaiduConsole() => OpenExternal("https://fanyi-api.baidu.com/manage/developer");

    // ---- 网络诊断 ----

    [ObservableProperty]
    public partial bool IsProbing { get; set; }

    [ObservableProperty]
    public partial bool HasProbeResult { get; set; }

    [ObservableProperty]
    public partial bool ProbeSucceeded { get; set; }

    [ObservableProperty]
    public partial bool ProbeFailed { get; set; }

    [ObservableProperty]
    public partial string ProbeDetail { get; set; } = string.Empty;

    public string ProbeButtonText => IsProbing ? L("settings.networkProbing") : L("settings.networkProbe");

    private NexusProbeResult? _lastProbe;

    partial void OnIsProbingChanged(bool value) => OnPropertyChanged(nameof(ProbeButtonText));

    [RelayCommand]
    private async Task ProbeNetworkAsync()
    {
        if (IsProbing)
        {
            return;
        }

        IsProbing = true;

        try
        {
            _lastProbe = await _probe.ProbeNexusAsync().ConfigureAwait(true);
            ApplyProbeResult();
        }
        finally
        {
            IsProbing = false;
        }
    }

    private void ApplyProbeResult()
    {
        if (_lastProbe is null)
        {
            return;
        }

        ProbeDetail = string.Join(
            Environment.NewLine,
            _lastProbe.Targets.Select(FormatTarget));

        ProbeSucceeded = _lastProbe.AnyReachable;
        ProbeFailed = !_lastProbe.AnyReachable;
        HasProbeResult = true;
    }

    private string FormatTarget(ProbeTargetResult target)
    {
        var name = L($"network.target.{target.TargetKey}");

        if (!target.Reachable)
        {
            return $"{name}    ✗  {L(target.ErrorKey ?? "network.error.unreachable")} · {target.ElapsedMs} ms";
        }

        var code = target.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—";
        return $"{name}    ✓  HTTP {code} · {target.ElapsedMs} ms";
    }

    public string PickFolderTitle => L("dialog.pickFolder");

    private GameInstallation? _installation;

    [RelayCommand]
    public void Refresh()
    {
        // 必须走 Resolve() 重新探测：Current 是软件启动时的快照，
        // 玩家用官方安装器升级 SMAPI 后，只有重读磁盘才能拿到新版本号。
        _installation = _games.Resolve();
        ApplyInstallation();
    }

    /// <summary>由视图在文件夹选择完成后调用。</summary>
    public bool TryAdoptPath(string path)
    {
        if (!_games.TryAdopt(path, out var installation) || installation is null)
        {
            _logger.LogWarning("用户选择的目录无效：{Path}", path);
            return false;
        }

        _installation = installation;
        ApplyInstallation();
        return true;
    }

    public override void RefreshTexts()
    {
        UpdateHeader();
        ApplyInstallation();
        ApplyProbeResult();
        LoadTranslationSettings();
        OnPropertyChanged(nameof(ProbeButtonText));
        OnPropertyChanged(nameof(CheckSmapiButtonText));
    }

    /// <summary>顶栏标题与副标题；语言切换后由 RefreshTexts 重新取词。</summary>
    private void UpdateHeader() => SetHeader("settings.title", "settings.subtitle");

    partial void OnSelectedLanguageChanged(LanguageOption? value)
    {
        if (_suppressLanguageChange || value is null)
        {
            return;
        }

        Localization.SetLanguage(value.Locale);
        _games.Settings.Locale = value.Locale;
        _games.SaveSettings();
    }

    private void ApplyInstallation()
    {
        GamePathDisplay = _installation?.RootPath ?? L("settings.gamePathEmpty");
        GameVersionDisplay = _installation?.GameVersion ?? L("settings.unknown");
        SmapiVersionDisplay = _installation?.SmapiVersion ?? L("settings.smapiMissing");
        StatusText = _installation is null ? L("settings.notDetected") : L("settings.detected");
    }
}
