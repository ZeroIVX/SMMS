using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Core.Snapshots;
using SMMS.Core.Updates;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;
using SMMS.UseCases.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>启动台的一条待处理问题。</summary>
public sealed record PendingIssueItem(
    string SeverityKind,
    bool IsBad,
    bool IsWarn,
    bool IsInfo,
    string Title,
    string Detail,
    string ActionText,
    bool HasDetail,
    string? TargetSection);

/// <summary>启动台「自上次启动以来」的一条记录。</summary>
public sealed record LaunchpadHistoryItem(string Text, string TimeText, string DotKind);

/// <summary>启动台右栏的一条最近备份。</summary>
public sealed record LaunchpadSnapshotItem(string Id, string TimeText, string LabelText, string MetaText);

/// <summary>启动台右栏的一个环境（配置档）。</summary>
public sealed record LaunchpadEnvironmentItem(string Id, string Name, string CountText, bool IsActive);

/// <summary>
/// 启动台：先用一句话回答「现在能不能开」，再给出待处理动作与最近变更。
/// </summary>
public partial class LaunchpadPageViewModel : PageViewModelBase
{
    private readonly ModLibraryService _library;
    private readonly GameInstallationService _games;
    private readonly ProfileService _profiles;
    private readonly ModToggleService _toggle;
    private readonly LaunchPreflightService _preflight;
    private readonly SnapshotService _snapshots;
    private readonly GameLaunchService _launchService;
    private readonly UpdateCheckService _updateChecker;
    private readonly ILogger<LaunchpadPageViewModel> _logger;

    private GameInstallation? _installation;
    private ModProfile? _activeProfile;
    private int _modCount;
    private int _enabledCount;
    private int _missingDependencyCount;
    private DateTime? _lastScanAt;
    private DateTimeOffset? _lastLaunchAt;
    private bool _isCheckingUpdates;

    public LaunchpadPageViewModel(
        ModLibraryService library,
        GameInstallationService games,
        ProfileService profiles,
        ModToggleService toggle,
        LaunchPreflightService preflight,
        SnapshotService snapshots,
        GameLaunchService launchService,
        UpdateCheckService updateChecker,
        ILocalizationService localization,
        ILogger<LaunchpadPageViewModel> logger)
        : base(localization)
    {
        _library = library;
        _games = games;
        _profiles = profiles;
        _toggle = toggle;
        _preflight = preflight;
        _snapshots = snapshots;
        _launchService = launchService;
        _updateChecker = updateChecker;
        _logger = logger;

        _launchService.StateChanged += OnLaunchStateChanged;
        _profiles.ProfilesChanged += OnExternalStateChanged;
        _games.InstallationChanged += OnExternalStateChanged;

        // 上次启动时间持久化在设置里，重启后仍能看到真实值。
        _lastLaunchAt = _games.Settings.LastLaunchAt;

        Refresh();
    }

    /// <summary>需要主窗口顺手刷新模组页（重新扫描磁盘时）。</summary>
    public event EventHandler? ScanRequested;

    /// <summary>请求主窗口切换页面，参数是导航 section。</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>请求主窗口弹出文件选择并导入压缩包。</summary>
    public event EventHandler? ImportRequested;

    public ObservableCollection<PendingIssueItem> PendingIssues { get; } = [];

    public ObservableCollection<LaunchpadHistoryItem> History { get; } = [];

    public ObservableCollection<LaunchpadSnapshotItem> RecentSnapshots { get; } = [];

    public ObservableCollection<LaunchpadEnvironmentItem> Environments { get; } = [];

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>ok / warn / bad，决定 Hero 徽章配色。</summary>
    [ObservableProperty]
    public partial string StatusKind { get; set; } = "ok";

    [ObservableProperty]
    public partial bool IsStatusOk { get; set; } = true;

    [ObservableProperty]
    public partial bool IsStatusWarn { get; set; }

    [ObservableProperty]
    public partial bool IsStatusBad { get; set; }

    [ObservableProperty]
    public partial string HeroMetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int MissingDependencyCount { get; set; }

    [ObservableProperty]
    public partial int ConflictCount { get; set; }

    [ObservableProperty]
    public partial string UpdateCountText { get; set; } = "—";

    [ObservableProperty]
    public partial int PendingCount { get; set; }

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    [ObservableProperty]
    public partial string SessionProcessText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastLaunchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastScanText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasRecentSnapshots { get; set; }

    [ObservableProperty]
    public partial bool HasHistory { get; set; }

    [ObservableProperty]
    public partial string ModsPathText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>重新扫描模组目录、重跑预检、刷新右栏。</summary>
    public void Refresh()
    {
        _installation = _games.Resolve();
        _activeProfile = _profiles.GetActive();

        var disabledPath = _activeProfile is null ? null : _toggle.GetDisabledPath(_activeProfile);
        var scan = _library.Load(_activeProfile?.ModsPath, disabledPath);

        _modCount = scan.Mods.Count;
        _enabledCount = scan.Mods.Count(mod => mod.IsEnabled);
        _missingDependencyCount = CountMissingDependencies(scan.Mods);
        _lastScanAt = DateTime.Now;

        ProfileName = string.IsNullOrWhiteSpace(_activeProfile?.Name) ? L("profiles.default") : _activeProfile.Name;
        ModsPathText = _activeProfile?.ModsPath ?? string.Empty;

        RebuildPreflight();
        RebuildHistory();
        RebuildSnapshots();
        RebuildEnvironments();
        UpdateTexts();

        HasError = false;
        ErrorText = string.Empty;

        _ = RefreshUpdateCountAsync();

        _logger.LogInformation(
            "启动台刷新完成：{Mods} 个模组，{Pending} 项待处理",
            _modCount,
            PendingCount);
    }

    public override void RefreshTexts()
    {
        UpdateTexts();
        SetHeaderText(L("nav.launchpad"), SubtitleText());
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(CanLaunch));
    }

    /// <summary>主窗口启动按钮的文案由启动服务状态决定。</summary>
    public string LaunchButtonText => _launchService.State switch
    {
        GameLaunchState.Launching => L("launch.launching"),
        GameLaunchState.Running => L("launch.running"),
        _ => _installation is null ? L("launch.noGame") : L("launch.start")
    };

    public bool CanLaunch => _launchService.State == GameLaunchState.Idle && _installation is not null;

    [RelayCommand]
    private void RefreshScan()
    {
        Refresh();
        ScanRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        var path = _activeProfile?.ModsPath ?? _installation?.ModsPath;

        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowError("launchpad.error.modsFolder");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void Import() => ImportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void BackupNow()
    {
        if (_activeProfile is null)
        {
            ShowError("preflight.noProfile");
            return;
        }

        var result = _snapshots.Create(_activeProfile, L("launchpad.backupManualLabel"));

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "snapshots.error.create");
            return;
        }

        RebuildSnapshots();
        RebuildHistory();
        _logger.LogInformation("启动台手动备份完成");
    }

    [RelayCommand]
    private void ManageProfiles() => NavigateRequested?.Invoke(this, "profiles");

    [RelayCommand]
    private void ViewAllIssues() => NavigateRequested?.Invoke(this, "diagnostics");

    [RelayCommand]
    private void OpenIssue(PendingIssueItem? item)
    {
        if (item?.TargetSection is { Length: > 0 } section)
        {
            NavigateRequested?.Invoke(this, section);
        }
    }

    [RelayCommand]
    private void RestoreSnapshot(LaunchpadSnapshotItem? item)
    {
        // 恢复属于破坏性操作，统一到「版本与更新」页的可回滚节点里做，那里有确认与进度提示。
        NavigateRequested?.Invoke(this, "updates");
    }

    [RelayCommand]
    private void SelectEnvironment(LaunchpadEnvironmentItem? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        _profiles.SetActive(item.Id);
        Refresh();
    }

    private void RebuildPreflight()
    {
        PendingIssues.Clear();

        var activeProfile = _activeProfile;

        if (activeProfile is null)
        {
            AddIssue(PreflightSeverity.Error, "preflight.noProfile", null, "launchpad.action.fix", "settings");
        }
        else
        {
            var result = _preflight.Check(
                _installation,
                activeProfile,
                IsVanillaProfile(activeProfile) ? LaunchMode.Vanilla : LaunchMode.Smapi,
                _launchService.State == GameLaunchState.Running);

            foreach (var issue in result.Issues)
            {
                var (actionKey, section) = ActionFor(issue.Code);
                AddIssue(issue.Severity, issue.Code, issue.Arguments, actionKey, section);
            }
        }

        PendingCount = PendingIssues.Count;
        HasPending = PendingCount > 0;

        var hasBlocker = PendingIssues.Any(item => item.SeverityKind == "bad");
        var hasRisk = PendingIssues.Any(item => item.SeverityKind == "warn");

        StatusKind = hasBlocker ? "bad" : hasRisk ? "warn" : "ok";
        IsStatusBad = hasBlocker;
        IsStatusWarn = !hasBlocker && hasRisk;
        IsStatusOk = !hasBlocker && !hasRisk;
        StatusText = L(hasBlocker
            ? "launchpad.status.blocked"
            : hasRisk
                ? "launchpad.status.risky"
                : "launchpad.status.ready");
    }

    private static bool IsVanillaProfile(ModProfile? profile)
        => profile is not null
           && string.Equals(profile.Id, ProfileService.VanillaProfileId, StringComparison.OrdinalIgnoreCase);

    private void AddIssue(PreflightSeverity severity, string code, IReadOnlyList<object>? arguments, string actionKey, string? section)
    {
        var title = string.Format(
            CultureInfo.CurrentCulture,
            L(code),
            arguments?.ToArray() ?? []);

        var hintKey = code + ".hint";
        var hint = L(hintKey);
        var detail = string.Equals(hint, hintKey, StringComparison.Ordinal) ? string.Empty : hint;

        PendingIssues.Add(new PendingIssueItem(
            severity switch
            {
                PreflightSeverity.Error => "bad",
                PreflightSeverity.Warning => "warn",
                _ => "info"
            },
            severity == PreflightSeverity.Error,
            severity == PreflightSeverity.Warning,
            severity == PreflightSeverity.Info,
            title,
            detail,
            L(actionKey),
            !string.IsNullOrEmpty(detail),
            section));
    }

    private static (string ActionKey, string? Section) ActionFor(string code) => code switch
    {
        "preflight.missingDependencies" => ("launchpad.action.install", "diagnostics"),
        "preflight.smapiTooOld" => ("launchpad.action.upgradeSmapi", "updates"),
        "preflight.smapiMissing" => ("launchpad.action.installSmapi", "updates"),
        "preflight.gameRunning" => ("launchpad.action.focusGame", null),
        "preflight.manifestBroken" => ("launchpad.action.review", "diagnostics"),
        "preflight.modsMissing" => ("launchpad.action.openSettings", "settings"),
        "preflight.noGame" => ("launchpad.action.openSettings", "settings"),
        _ => ("launchpad.action.fix", "diagnostics")
    };

    private void RebuildHistory()
    {
        History.Clear();

        if (_activeProfile is null)
        {
            HasHistory = false;
            return;
        }

        foreach (var snapshot in _snapshots.List(_activeProfile.Id).Take(4))
        {
            var label = string.IsNullOrWhiteSpace(snapshot.Label) ? L("snapshots.unlabeled") : snapshot.Label;
            History.Add(new LaunchpadHistoryItem(
                string.Format(
                    CultureInfo.CurrentCulture,
                    L("launchpad.historyItem"),
                    label,
                    snapshot.ModCount),
                snapshot.CreatedAt.ToLocalTime().ToString("MM\\/dd HH:mm", CultureInfo.CurrentCulture),
                snapshot.IsAutomatic ? "muted" : "primary"));
        }

        HasHistory = History.Count > 0;
    }

    private void RebuildSnapshots()
    {
        RecentSnapshots.Clear();

        if (_activeProfile is null)
        {
            HasRecentSnapshots = false;
            return;
        }

        foreach (var snapshot in _snapshots.List(_activeProfile.Id).Take(3))
        {
            RecentSnapshots.Add(new LaunchpadSnapshotItem(
                snapshot.Id,
                snapshot.CreatedAt.ToLocalTime().ToString("MM\\/dd HH:mm", CultureInfo.CurrentCulture),
                string.IsNullOrWhiteSpace(snapshot.Label) ? L("snapshots.unlabeled") : snapshot.Label,
                string.Format(
                    CultureInfo.CurrentCulture,
                    L("launchpad.snapshotMeta"),
                    snapshot.ModCount,
                    FormatSize(snapshot.SizeBytes))));
        }

        HasRecentSnapshots = RecentSnapshots.Count > 0;
    }

    private void RebuildEnvironments()
    {
        Environments.Clear();

        var active = _profiles.GetActive();

        foreach (var profile in _profiles.GetAll())
        {
            var disabledPath = _toggle.GetDisabledPath(profile);
            var mods = profile.IsExternal ? [] : _library.Load(profile.ModsPath, disabledPath).Mods;
            var enabled = mods.Count(mod => mod.IsEnabled);

            Environments.Add(new LaunchpadEnvironmentItem(
                profile.Id,
                DisplayProfileName(profile),
                string.Format(CultureInfo.CurrentCulture, L("launchpad.envCount"), mods.Count, enabled),
                profile.Id == active?.Id));
        }
    }

    /// <summary>纯净档的 Name 可能为空，统一按 Id 本地化；其余档用用户给的名字。</summary>
    private string DisplayProfileName(ModProfile profile)
    {
        if (IsVanillaProfile(profile))
        {
            return L("profiles.vanillaName");
        }

        return string.IsNullOrWhiteSpace(profile.Name) ? L("profiles.default") : profile.Name;
    }

    private async Task RefreshUpdateCountAsync()
    {
        if (_isCheckingUpdates || _activeProfile is null)
        {
            return;
        }

        _isCheckingUpdates = true;

        try
        {
            UpdateCountText = L("launchpad.checking");

            var disabledPath = _toggle.GetDisabledPath(_activeProfile);
            var result = await _updateChecker.CheckAsync(_activeProfile, disabledPath).ConfigureAwait(true);

            UpdateCountText = result.Succeeded
                ? result.UpdateCount.ToString(CultureInfo.CurrentCulture)
                : L("launchpad.unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动台更新检查失败");
            UpdateCountText = L("launchpad.unknown");
        }
        finally
        {
            _isCheckingUpdates = false;
        }
    }

    private void UpdateTexts()
    {
        SetHeaderText(L("nav.launchpad"), SubtitleText());

        HeroMetaText = string.Format(
            CultureInfo.CurrentCulture,
            L("launchpad.heroMeta"),
            _enabledCount,
            _modCount,
            _installation?.SmapiVersion ?? L("settings.smapiMissing"),
            _installation?.GameVersion ?? L("settings.unknown"),
            FormatLastLaunch());

        SessionProcessText = _launchService.State switch
        {
            GameLaunchState.Running => L("launchpad.processRunning"),
            GameLaunchState.Launching => L("launchpad.processLaunching"),
            _ => L("launchpad.processIdle")
        };

        LastLaunchText = _lastLaunchAt is null
            ? L("launchpad.never")
            : _lastLaunchAt.Value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);

        LastScanText = _lastScanAt is null
            ? L("launchpad.never")
            : string.Format(
                CultureInfo.CurrentCulture,
                L("launchpad.scanMeta"),
                _lastScanAt.Value.ToString("HH:mm", CultureInfo.CurrentCulture),
                _modCount);

        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(CanLaunch));
    }

    private string SubtitleText()
    {
        if (_activeProfile is null && _installation is null)
        {
            return L("launchpad.subtitleNoGame");
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            L("launchpad.subtitle"),
            ProfileName,
            _enabledCount,
            _modCount,
            _installation?.SmapiVersion ?? L("settings.smapiMissing"));
    }

    private string FormatLastLaunch()
        => _lastLaunchAt is null
            ? L("launchpad.never")
            : FormatAgo(DateTimeOffset.Now - _lastLaunchAt.Value);

    private string FormatAgo(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => L("launchpad.justNow"),
        { TotalHours: < 1 } => string.Format(
            CultureInfo.CurrentCulture, L("launchpad.minutesAgo"), (int)span.TotalMinutes),
        { TotalDays: < 1 } => string.Format(
            CultureInfo.CurrentCulture, L("launchpad.hoursAgo"), (int)span.TotalHours),
        _ => string.Format(CultureInfo.CurrentCulture, L("launchpad.daysAgo"), (int)span.TotalDays)
    };

    private static int CountMissingDependencies(IReadOnlyList<InstalledMod> mods)
    {
        var installed = mods.Select(mod => mod.UniqueId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return mods
            .SelectMany(mod => mod.Dependencies)
            .Count(dependency => dependency.IsRequired && !installed.Contains(dependency.UniqueId));
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => string.Format(
            CultureInfo.CurrentCulture, "{0:0.0} GB", bytes / (1024d * 1024 * 1024)),
        >= 1024L * 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} MB", bytes / (1024d * 1024)),
        >= 1024 => string.Format(CultureInfo.CurrentCulture, "{0:0} KB", bytes / 1024d),
        _ => string.Format(CultureInfo.CurrentCulture, "{0} B", bytes)
    };

    private void ShowError(string key)
    {
        HasError = true;
        ErrorText = L(key);
    }

    private void OnLaunchStateChanged(object? sender, EventArgs e)
    {
        if (_launchService.State == GameLaunchState.Running)
        {
            _lastLaunchAt = DateTimeOffset.Now;
            _games.Settings.LastLaunchAt = _lastLaunchAt;
                _games.Settings.LastLaunchProfileId = _profiles.GetActive()?.Id;
            _games.SaveSettings();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateTexts();
            RebuildPreflight();
            PendingCount = PendingIssues.Count;
            HasPending = PendingCount > 0;
            OnPropertyChanged(nameof(LaunchButtonText));
            OnPropertyChanged(nameof(CanLaunch));
        });
    }

    /// <summary>配置档/游戏目录在别处变化后，启动台的模组数与预检要跟着刷新。</summary>
    private void OnExternalStateChanged(object? sender, EventArgs e)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    public override void Dispose()
    {
        GC.SuppressFinalize(this);
        _launchService.StateChanged -= OnLaunchStateChanged;
        _profiles.ProfilesChanged -= OnExternalStateChanged;
        _games.InstallationChanged -= OnExternalStateChanged;
        base.Dispose();
    }
}
