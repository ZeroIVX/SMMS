using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SMMS.Core.Game;
using SMMS.Core.Profiles;
using SMMS.Core.Snapshots;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Importing;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;

namespace SMMS.Desktop.ViewModels;

/// <summary>应用外壳：负责导航、顶栏状态、启动入口与全局拖放。</summary>
public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly GameInstallationService _games;
    private readonly GameLaunchService _launchService;
    private readonly ProfileService _profileService;
    private readonly LaunchPreflightService _preflight;
    private readonly DropImportService _dropImport;
    private readonly SnapshotService _snapshots;
    private GameInstallation? _installation;
    private GameInstallation? _activeInstallation;
    private string? _errorKey;
    private bool _suppressProfileChange;

    public MainWindowViewModel(
        LaunchpadPageViewModel launchpadPage,
        ModsPageViewModel modsPage,
        DiscoverPageViewModel discoverPage,
        VanityPageViewModel vanityPage,
        DiagnosticsPageViewModel diagnosticsPage,
        UpdatesPageViewModel updatesPage,
        ProfilePageViewModel profilesPage,
        SettingsPageViewModel settingsPage,
        BackupsPageViewModel backupsPage,
        GameInstallationService games,
        GameLaunchService launchService,
        ProfileService profileService,
        LaunchPreflightService preflight,
        DropImportService dropImport,
        SnapshotService snapshots,
        ILocalizationService localization)
    {
        _localization = localization;
        _games = games;
        _launchService = launchService;
        _profileService = profileService;
        _preflight = preflight;
        _dropImport = dropImport;
        _snapshots = snapshots;

        LaunchpadPage = launchpadPage;
        ModsPage = modsPage;
        DiscoverPage = discoverPage;
        VanityPage = vanityPage;
        ProfilesPage = profilesPage;
        SettingsPage = settingsPage;
        BackupsPage = backupsPage;
        DiagnosticsPage = diagnosticsPage;
        UpdatesPage = updatesPage;

        CurrentPage = LaunchpadPage;
        IsLaunchpadActive = true;

        _installation = _games.Current ?? _games.Resolve();

        // 原版启动统一由内置「纯净档」承载，启动时确保它存在（幂等）。
        _profileService.EnsureVanillaProfile(_installation);

        RebuildLaunchModes();
        RebuildProfiles();
        RefreshActiveInstallation();
        UpdateLaunchState();
        UpdateSummary();

        _localization.LanguageChanged += OnLanguageChanged;
        _launchService.StateChanged += OnLaunchStateChanged;
        _games.InstallationChanged += OnInstallationChanged;
        _profileService.ProfilesChanged += OnProfilesChanged;
        BackupsPage.LibraryChanged += OnLibraryChanged;
        VanityPage.LibraryChanged += OnVanityLibraryChanged;

        LaunchpadPage.ScanRequested += OnScanRequested;
        LaunchpadPage.NavigateRequested += OnNavigateRequested;
        LaunchpadPage.PropertyChanged += OnLaunchpadPropertyChanged;
        DiagnosticsPage.NavigationRequested += OnNavigateRequested;
        ProfilesPage.NavigationRequested += OnProfileNavigationRequested;
    }

    public LaunchpadPageViewModel LaunchpadPage { get; }

    public ModsPageViewModel ModsPage { get; }

    public DiscoverPageViewModel DiscoverPage { get; }

    public VanityPageViewModel VanityPage { get; }

    public ProfilePageViewModel ProfilesPage { get; }

    public SettingsPageViewModel SettingsPage { get; }

    /// <summary>备份不再是独立页面，能力并入「版本与更新」的可回滚节点。</summary>
    public BackupsPageViewModel BackupsPage { get; }

    public DiagnosticsPageViewModel DiagnosticsPage { get; }

    public UpdatesPageViewModel UpdatesPage { get; }
    public ObservableCollection<LaunchModeOption> LaunchModes { get; } = [];

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty]
    public partial PageViewModelBase? CurrentPage { get; set; }

    [ObservableProperty]
    public partial bool IsLaunchpadActive { get; set; }

    [ObservableProperty]
    public partial bool IsModsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDiscoverActive { get; set; }

    [ObservableProperty]
    public partial bool IsVanityActive { get; set; }

    [ObservableProperty]
    public partial bool IsDiagnosticsActive { get; set; }

    [ObservableProperty]
    public partial bool IsUpdatesActive { get; set; }

    [ObservableProperty]
    public partial bool IsProfilesActive { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsActive { get; set; }

    [ObservableProperty]
    public partial LaunchModeOption? SelectedLaunchMode { get; set; }

    [ObservableProperty]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>原版启动不读模组，配置档选择此时无意义。</summary>
    public bool IsProfileSelectable => SelectedLaunchMode?.Mode != LaunchMode.Vanilla;

    /// <summary>当前配置档实际使用的安装：完整实例子类用副本，其余共用主安装。</summary>
    private GameInstallation? ActiveInstallation => _activeInstallation ?? _installation;

    public string LaunchButtonText => _launchService.State switch
    {
        GameLaunchState.Launching => L("launch.launching"),
        GameLaunchState.Running => L("launch.running"),
        _ => ActiveInstallation is null ? L("launch.noGame") : L("launch.start")
    };

    /// <summary>顶栏摘要：启动前让玩家一眼看到自己在启动什么。</summary>
    public string InstanceSummary { get; private set; } = string.Empty;

    /// <summary>顶栏健康胶囊：任何一页都能看到「能不能开」。</summary>
    public string PendingSummary => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        L("topbar.pendingHint"),
        LaunchpadPage.PendingCount);

    public bool HasPendingIssues => LaunchpadPage.HasPending;

    public bool IsHealthy => LaunchpadPage.IsStatusOk;

    public bool CanLaunch
    {
        get
        {
            var installation = ActiveInstallation;
            if (_launchService.State != GameLaunchState.Idle || installation is null)
            {
                return false;
            }

            var mode = SelectedLaunchMode?.Mode ?? LaunchMode.Smapi;
            return mode == LaunchMode.Vanilla || installation.IsSmapiInstalled;
        }
    }

    public string LaunchTooltip
    {
        get
        {
            var installation = ActiveInstallation;

            if (installation is null)
            {
                return SelectedProfile?.Profile?.HasOwnInstance == true
                    ? L("launch.error.instanceMissing")
                    : L("launch.error.noGame");
            }

            var mode = SelectedLaunchMode?.Mode ?? LaunchMode.Smapi;

            if (mode == LaunchMode.Smapi && !installation.IsSmapiInstalled)
            {
                return L("launch.error.noSmapi");
            }

            if (mode == LaunchMode.Vanilla)
            {
                return L("launch.tooltip.vanilla");
            }

            var profileName = SelectedProfile?.DisplayName ?? L("profiles.default");
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("launch.tooltip.smapi"),
                profileName);
        }
    }

    [RelayCommand]
    private void Navigate(string? section)
    {
        CurrentPage = section switch
        {
            "launchpad" => LaunchpadPage,
            "discover" => DiscoverPage,
            "vanity" => VanityPage,
            "diagnostics" => DiagnosticsPage,
            "updates" => UpdatesPage,
            "profiles" => ProfilesPage,
            "settings" => SettingsPage,
            _ => ModsPage
        };

        IsLaunchpadActive = ReferenceEquals(CurrentPage, LaunchpadPage);
        IsModsActive = ReferenceEquals(CurrentPage, ModsPage);
        IsDiscoverActive = ReferenceEquals(CurrentPage, DiscoverPage);
        IsVanityActive = ReferenceEquals(CurrentPage, VanityPage);
        IsDiagnosticsActive = ReferenceEquals(CurrentPage, DiagnosticsPage);
        IsUpdatesActive = ReferenceEquals(CurrentPage, UpdatesPage);
        IsProfilesActive = ReferenceEquals(CurrentPage, ProfilesPage);
        IsSettingsActive = ReferenceEquals(CurrentPage, SettingsPage);

        // 概览页的统计随操作变化，每次进来重新取一次。
        if (ReferenceEquals(CurrentPage, LaunchpadPage))
        {
            LaunchpadPage.Refresh();
        }
    }

    /// <summary>顶栏搜索图标：直接去市场搜东西。</summary>
    [RelayCommand]
    private void OpenSearch() => Navigate("discover");

    /// <summary>顶栏待处理胶囊：去诊断中心看全部问题。</summary>
    [RelayCommand]
    private void OpenDiagnostics() => Navigate("diagnostics");

    /// <summary>由视图注入：展示预检结果并返回玩家是否仍要继续。</summary>
    public Func<PreflightResult, Task<bool>>? PreflightConfirmation { get; set; }

    /// <summary>由视图注入：降级安装前征求确认，参数是「模组 旧版 → 新版」列表。</summary>
    public Func<IReadOnlyList<string>, Task<bool>>? DowngradeConfirmation { get; set; }

    /// <summary>
    /// 处理拖入的压缩包。逐个按类型分流，导入后刷新受影响的页面。
    /// </summary>
    public async Task<string> HandleDropAsync(IReadOnlyList<string> paths)
    {
        var succeeded = 0;
        var failed = 0;
        string? lastError = null;

        foreach (var path in paths)
        {
            var outcome = await _dropImport.ImportAsync(path).ConfigureAwait(true);

            // 降级时服务层什么都没做，确认后带着 allowDowngrade 重跑一次。
            if (outcome.NeedsDowngradeConfirmation && DowngradeConfirmation is not null)
            {
                var proceed = await DowngradeConfirmation(outcome.Downgrades!).ConfigureAwait(true);

                if (!proceed)
                {
                    failed++;
                    lastError = L("drop.cancelled");
                    continue;
                }

                outcome = await _dropImport.ImportAsync(path, allowDowngrade: true).ConfigureAwait(true);
            }

            if (outcome.Success)
            {
                succeeded++;
            }
            else
            {
                failed++;
                lastError = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L(outcome.MessageKey),
                    outcome.Arguments.Cast<object>().ToArray());
            }

            ApplyDropEffects(outcome.Effects);
        }

        return failed == 0
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, L("drop.done"), succeeded)
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, L("drop.doneWithErrors"), succeeded, failed, lastError);
    }

    private void ApplyDropEffects(DropEffects effects)
    {
        if (effects.HasFlag(DropEffects.Mods))
        {
            ModsPage.Refresh();
            LaunchpadPage.Refresh();
            UpdateSummary();
        }

        if (effects.HasFlag(DropEffects.Vanity))
        {
            VanityPage.Refresh();
        }

        if (effects.HasFlag(DropEffects.Profiles))
        {
            RebuildProfiles();
            RefreshActiveInstallation();
            LaunchpadPage.Refresh();
        }
    }

    public PreflightDialogViewModel CreatePreflightViewModel(PreflightResult result) => new(result, L);

    /// <summary>供视图取本地化文案（对话框标题等）。</summary>
    public string Localize(string key) => L(key);

    [RelayCommand]
    private async Task LaunchAsync() => await LaunchWithModeAsync(SelectedLaunchMode?.Mode ?? LaunchMode.Smapi);

    /// <summary>
    /// Hero 的副链接：原版启动统一走内置「纯净档」配置档——先切到该档，再按原版模式启动。
    /// </summary>
    [RelayCommand]
    private async Task LaunchVanillaAsync()
    {
        var vanilla = _profileService.GetVanillaProfile() ?? _profileService.EnsureVanillaProfile(_installation);

        if (vanilla is null)
        {
            ShowError("preflight.noGame");
            return;
        }

        _profileService.SetActive(vanilla.Id);
        RebuildProfiles();

        if (SelectedProfile is { } selected && selected.Id == vanilla.Id)
        {
            SelectedProfile = selected;
        }

        await LaunchWithModeAsync(LaunchMode.Vanilla);
    }

    private async Task LaunchWithModeAsync(LaunchMode mode)
    {
        var profile = SelectedProfile?.Profile ?? _profileService.GetActive();
        if (profile is null)
        {
            ShowError("preflight.noProfile");
            return;
        }

        // 纯净档固定按原版启动：它没有模组，也不需要 SMAPI。
        if (string.Equals(profile.Id, ProfileService.VanillaProfileId, StringComparison.OrdinalIgnoreCase))
        {
            mode = LaunchMode.Vanilla;
        }

        // 完整实例子类的启动目标是副本，其余用主安装。
        var installation = _games.ResolveForProfile(profile);
        if (installation is null)
        {
            ShowError(profile.HasOwnInstance ? "launch.error.instanceMissing" : "launch.error.noGame");
            return;
        }

        // 先预检再启动：让玩家在点下去之前就知道会遇到什么，而不是等游戏闪退后翻日志。
        var preflight = _preflight.Check(
            installation,
            profile,
            mode,
            _launchService.IsGameProcessRunning());

        if (preflight.NeedsConfirmation && PreflightConfirmation is not null)
        {
            var proceed = await PreflightConfirmation(preflight);
            if (!proceed)
            {
                return;
            }
        }

        // 启动前留一份快照，代表「这一刻的模组状态」，出问题能整体退回。
        // 快照失败不阻止启动——保护措施不该挡住正常使用；原版启动没有模组，跳过。
        if (mode != LaunchMode.Vanilla)
        {
            _snapshots.Create(profile, L("snapshots.preLaunchLabel"), SnapshotKind.PreLaunch);
            _snapshots.Prune(profile.Id);
        }

        var result = _launchService.Launch(installation, profile, mode);

        if (!result.Success && result.ErrorKey is { } key)
        {
            ShowError(key);
        }
        else
        {
            ClearError();
        }

        UpdateLaunchState();
        LaunchpadPage.Refresh();
    }

    [RelayCommand]
    private void DismissError() => ClearError();

    /// <summary>设置页改动目录后由主窗口同步，保证启动按钮立即反映新路径。</summary>
    public void RefreshInstallation()
    {
        _installation = _games.Current ?? _games.Resolve();
        RebuildLaunchModes();
        RebuildProfiles();
        RefreshActiveInstallation();
        UpdateLaunchState();
        UpdateSummary();
        LaunchpadPage.Refresh();
    }

    partial void OnSelectedLaunchModeChanged(LaunchModeOption? value)
    {
        // 原版启动不读模组，配置档选择此时无意义；切回 SMAPI 时保留上次选择。
        UpdateLaunchState();
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        if (_suppressProfileChange || value is null)
        {
            return;
        }

        _profileService.SetActive(value.Id);
        RefreshActiveInstallation();
        UpdateLaunchState();
        UpdateSummary();
        LaunchpadPage.Refresh();
    }

    private void RebuildLaunchModes()
    {
        var previous = SelectedLaunchMode?.Mode;

        _suppressProfileChange = true;
        LaunchModes.Clear();
        LaunchModes.Add(new LaunchModeOption(LaunchMode.Smapi, L("launch.mode.smapi")));
        LaunchModes.Add(new LaunchModeOption(LaunchMode.Vanilla, L("launch.mode.vanilla")));

        // 默认优先 SMAPI；未安装时自动落到原版，避免上来就是个不可用的按钮。
        var preferred = previous
                        ?? (_installation?.IsSmapiInstalled == true ? LaunchMode.Smapi : LaunchMode.Vanilla);

        SelectedLaunchMode = LaunchModes.FirstOrDefault(option => option.Mode == preferred) ?? LaunchModes[0];
        _suppressProfileChange = false;
    }

    private void RebuildProfiles()
    {
        var active = _profileService.GetActive();

        _suppressProfileChange = true;
        Profiles.Clear();

        // 顺序由 ProfileService.GetAll() 决定（内置档固定前两位 + 玩家手动排序），这里不再重排。
        var ordered = _profileService.GetAll();

        foreach (var profile in ordered)
        {
            var name = string.IsNullOrWhiteSpace(profile.Name) ? L("profiles.default") : profile.Name;

            if (string.Equals(profile.Id, ProfileService.VanillaProfileId, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(profile.Name))
            {
                name = L("profiles.vanillaName");
            }

            Profiles.Add(new ProfileItemViewModel(profile, name, profile.Id == active?.Id));
        }

        SelectedProfile = Profiles.FirstOrDefault(item => item.IsActive) ?? Profiles.FirstOrDefault();
        _suppressProfileChange = false;
    }

    private void UpdateLaunchState()
    {
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchTooltip));
        OnPropertyChanged(nameof(IsProfileSelectable));
    }

    private void UpdateSummary()
    {
        var installation = ActiveInstallation;

        if (installation is null)
        {
            InstanceSummary = L("launch.summaryNoGame");
        }
        else
        {
            var gameVersion = installation.GameVersion ?? L("settings.unknown");
            var smapiVersion = installation.SmapiVersion ?? L("settings.smapiMissing");
            InstanceSummary = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                L("launch.summary"),
                gameVersion,
                smapiVersion,
                ModsPage.TotalCount);
        }

        OnPropertyChanged(nameof(InstanceSummary));
    }

    /// <summary>切换配置档后重新确定启动目标，完整实例会解析到自己的副本。</summary>
    private void RefreshActiveInstallation()
    {
        var profile = SelectedProfile?.Profile;
        _activeInstallation = profile is null ? _installation : _games.ResolveForProfile(profile);
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

    private string L(string key) => _localization[key];

    private void OnLaunchpadPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchpadPageViewModel.PendingCount)
            or nameof(LaunchpadPageViewModel.HasPending)
            or nameof(LaunchpadPageViewModel.IsStatusOk))
        {
            OnPropertyChanged(nameof(PendingSummary));
            OnPropertyChanged(nameof(HasPendingIssues));
            OnPropertyChanged(nameof(IsHealthy));
        }
    }

    private void OnScanRequested(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            ModsPage.Refresh();
            LaunchpadPage.Refresh();
            UpdateSummary();
        });

    private void OnNavigateRequested(object? sender, string section)
        => Dispatcher.UIThread.Post(() => Navigate(section));

    /// <summary>配置档卡片上的「模组 / 美化包」入口：切档后跳到对应页面。</summary>
    private void OnProfileNavigationRequested(object? sender, (string ProfileId, string Section) request)
        => Dispatcher.UIThread.Post(() =>
        {
            _profileService.SetActive(request.ProfileId);
            RebuildProfiles();
            RefreshActiveInstallation();
            Navigate(request.Section);

            if (ReferenceEquals(CurrentPage, ModsPage))
            {
                ModsPage.Refresh();
            }
            else if (ReferenceEquals(CurrentPage, VanityPage))
            {
                VanityPage.Refresh();
            }

            LaunchpadPage.Refresh();
        });

    private void OnLaunchStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdateLaunchState);

    /// <summary>美化页切换内容包后，模组页需要重新扫描磁盘。</summary>
    private void OnVanityLibraryChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            ModsPage.Refresh();
            LaunchpadPage.Refresh();
            UpdateSummary();
        });

    private void OnInstallationChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(RefreshInstallation);

    /// <summary>备份恢复或删除后，模组页需要重新扫描磁盘。</summary>
    private void OnLibraryChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            ModsPage.Refresh();
            LaunchpadPage.Refresh();
            UpdateSummary();
        });

    private void OnProfilesChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() =>
        {
            RebuildProfiles();
            RefreshActiveInstallation();
            ModsPage.Refresh();
            LaunchpadPage.Refresh();
            UpdateLaunchState();
            UpdateSummary();
        });

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ModsPage.RefreshTexts();
        SettingsPage.RefreshTexts();
        ProfilesPage.RefreshTexts();
        BackupsPage.RefreshTexts();
        LaunchpadPage.RefreshTexts();
        DiagnosticsPage.RefreshTexts();
        UpdatesPage.RefreshTexts();

        RebuildLaunchModes();
        RebuildProfiles();
        UpdateLaunchState();
        UpdateSummary();
        OnPropertyChanged(nameof(PendingSummary));

        if (_errorKey is { } key)
        {
            ErrorText = L(key);
        }
    }

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _launchService.StateChanged -= OnLaunchStateChanged;
        _games.InstallationChanged -= OnInstallationChanged;
        _profileService.ProfilesChanged -= OnProfilesChanged;
        BackupsPage.LibraryChanged -= OnLibraryChanged;
        VanityPage.LibraryChanged -= OnVanityLibraryChanged;
        LaunchpadPage.ScanRequested -= OnScanRequested;
        LaunchpadPage.NavigateRequested -= OnNavigateRequested;
        LaunchpadPage.PropertyChanged -= OnLaunchpadPropertyChanged;
        ProfilesPage.NavigationRequested -= OnProfileNavigationRequested;

        LaunchpadPage.Dispose();
        ModsPage.Dispose();
        SettingsPage.Dispose();
        ProfilesPage.Dispose();
        BackupsPage.Dispose();

        GC.SuppressFinalize(this);
    }
}
