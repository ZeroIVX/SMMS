using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>诊断清单里的一条问题。文案在构建时已经本地化，动作命令由页面提供。</summary>
public sealed class DiagnosticIssueViewModel(
    string title,
    string description,
    string actionText,
    IRelayCommand? actionCommand)
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public string ActionText { get; } = actionText;

    public IRelayCommand? ActionCommand { get; } = actionCommand;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasAction => ActionCommand is not null && !string.IsNullOrEmpty(ActionText);
}

/// <summary>
/// 诊断页：把启动前预检（<see cref="LaunchPreflightService"/>）的结果按严重度重新分组，
/// 再补上「必需依赖缺失」与「依赖环」两项自检，给出统一的健康报告。
/// </summary>
public sealed partial class DiagnosticsPageViewModel : PageViewModelBase
{
    /// <summary>页面实际执行的全局检查项数量（与 <see cref="Refresh"/> 里的检查一一对应）。</summary>
    private const int RuleCount = 8;

    private readonly LaunchPreflightService _preflight;
    private readonly GameInstallationService _games;
    private readonly ProfileService _profiles;
    private readonly UpdateCheckService _updateChecker;
    private readonly ModLibraryService _library;
    private readonly ModToggleService _toggle;
    private readonly GameLaunchService _launch;
    private readonly ILogger<DiagnosticsPageViewModel> _logger;

    private IReadOnlyList<PreflightIssue> _issues = [];
    private IReadOnlyList<InstalledMod> _mods = [];
    private int _updateCount;
    private DateTimeOffset? _lastChecked;
    private int _cycleCount;
    private bool _manifestBroken;
    private string? _errorKey;
    private string? _statusKey;

    public DiagnosticsPageViewModel(
        LaunchPreflightService preflight,
        GameInstallationService games,
        ProfileService profiles,
        UpdateCheckService updateChecker,
        ModLibraryService library,
        ModToggleService toggle,
        GameLaunchService launch,
        ILocalizationService localization,
        ILogger<DiagnosticsPageViewModel> logger)
        : base(localization)
    {
        _preflight = preflight;
        _games = games;
        _profiles = profiles;
        _updateChecker = updateChecker;
        _library = library;
        _toggle = toggle;
        _launch = launch;
        _logger = logger;

        _profiles.ProfilesChanged += OnSourceChanged;
        _games.InstallationChanged += OnSourceChanged;

        Refresh();
        _ = RecheckAsync();
    }

    /// <summary>页面请求跳转到别的分区（由 Lead 在主窗口里接到导航命令）。</summary>
    public event EventHandler<string>? NavigationRequested;

    public ObservableCollection<DiagnosticIssueViewModel> Blockers { get; } = [];

    public ObservableCollection<DiagnosticIssueViewModel> Risks { get; } = [];

    public ObservableCollection<DiagnosticIssueViewModel> Suggestions { get; } = [];

    [ObservableProperty]
    public partial int BlockerCount { get; set; }

    [ObservableProperty]
    public partial int RiskCount { get; set; }

    [ObservableProperty]
    public partial int SuggestionCount { get; set; }

    [ObservableProperty]
    public partial int PassedCount { get; set; }

    [ObservableProperty]
    public partial bool HasBlockers { get; set; }

    [ObservableProperty]
    public partial bool HasRisks { get; set; }

    [ObservableProperty]
    public partial bool HasSuggestions { get; set; }

    [ObservableProperty]
    public partial bool AllClear { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string RecheckText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckedAtText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PassedLabelText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FooterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>重新读取模组库与预检结果，并重建三个分组清单。</summary>
    public void Refresh()
    {
        var profile = _profiles.GetActive();
        var game = profile is null ? _games.Current : _games.ResolveForProfile(profile);
        var disabledPath = profile is null ? null : _toggle.GetDisabledPath(profile);
        var snapshot = _library.Load(profile?.ModsPath, disabledPath);
        _mods = snapshot.Mods;

        try
        {
            _issues = _preflight.Check(game, profile, LaunchMode.Smapi, _launch.IsGameProcessRunning()).Issues;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "诊断页预检失败");
            ShowError("diagnostics.error.check");
            _issues = [];
        }

        BuildIssueLists();
        UpdateTexts();
    }

    [RelayCommand]
    public async Task RecheckAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasError = false;
        HasStatus = false;

        try
        {
            var profile = _profiles.GetActive();
            if (profile is null)
            {
                _updateCount = 0;
            }
            else
            {
                var disabledPath = _toggle.GetDisabledPath(profile);
                var result = await _updateChecker.CheckAsync(profile, disabledPath).ConfigureAwait(true);
                _updateCount = result.Succeeded ? result.UpdateCount : 0;
            }

            _lastChecked = DateTimeOffset.Now;
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "诊断页更新检查失败");
            ShowError("diagnostics.error.check");
        }
        finally
        {
            IsBusy = false;
            RecheckText = L("diagnostics.recheck");
        }
    }

    public override void RefreshTexts()
    {
        Refresh();

        if (_errorKey is { } errorKey)
        {
            ErrorText = L(errorKey);
        }

        if (_statusKey is { } statusKey)
        {
            StatusText = L(statusKey);
        }
    }

    private void BuildIssueLists()
    {
        Blockers.Clear();
        Risks.Clear();
        Suggestions.Clear();

        var enabled = _mods.Where(mod => mod.IsEnabled).ToList();
        var installedIds = new HashSet<string>(_mods.Select(mod => mod.UniqueId), StringComparer.OrdinalIgnoreCase);

        var all = new List<PreflightIssue>(_issues);

        // 自检 1：必需依赖缺失（PreflightService 只把缺依赖定为 Warning，诊断页把它升级为阻断项）。
        foreach (var (dependencyId, dependents) in FindMissingDependencies(enabled, installedIds))
        {
            all.Add(new PreflightIssue
            {
                Severity = PreflightSeverity.Error,
                Code = "diagnostics.missingDependency",
                Arguments = [dependencyId, string.Join("、", dependents)]
            });
        }

        // 自检 2：已装模组之间的依赖环。
        var cycle = FindDependencyCycle(enabled);
        _cycleCount = cycle.Count;
        if (cycle.Count > 0)
        {
            all.Add(new PreflightIssue
            {
                Severity = PreflightSeverity.Warning,
                Code = "diagnostics.dependencyCycle",
                Arguments = [cycle.Count.ToString(CultureInfo.CurrentCulture), string.Join(" → ", cycle)]
            });
        }

        // 自检 3：可更新的模组（数据来自 UpdateCheckService）。
        if (_updateCount > 0)
        {
            all.Add(new PreflightIssue
            {
                Severity = PreflightSeverity.Info,
                Code = "diagnostics.updatesAvailable",
                Arguments = [_updateCount.ToString(CultureInfo.CurrentCulture)]
            });
        }

        _manifestBroken = all.Any(issue => issue.Code == "preflight.manifestBroken");

        foreach (var issue in all.OrderByDescending(issue => issue.Severity))
        {
            var item = CreateItem(issue);
            switch (issue.Severity)
            {
                case PreflightSeverity.Error:
                    Blockers.Add(item);
                    break;
                case PreflightSeverity.Warning:
                    Risks.Add(item);
                    break;
                default:
                    Suggestions.Add(item);
                    break;
            }
        }

        BlockerCount = Blockers.Count;
        RiskCount = Risks.Count;
        SuggestionCount = Suggestions.Count;
        HasBlockers = BlockerCount > 0;
        HasRisks = RiskCount > 0;
        HasSuggestions = SuggestionCount > 0;
        AllClear = Blockers.Count + Risks.Count + Suggestions.Count == 0;

        var checksRun = RuleCount + (enabled.Count * 2);
        var failed = Blockers.Count + Risks.Count + Suggestions.Count;
        PassedCount = Math.Max(0, checksRun - failed);
    }

    private DiagnosticIssueViewModel CreateItem(PreflightIssue issue)
    {
        var title = Localize(issue.Code, issue.Arguments);
        var description = HintFor(issue);
        var (actionText, section, statusKey) = ActionFor(issue.Code);

        IRelayCommand? command = null;
        if (section is not null)
        {
            var target = section;
            command = new RelayCommand(() => NavigationRequested?.Invoke(this, target));
        }
        else if (statusKey is not null)
        {
            var key = statusKey;
            command = new RelayCommand(() => ShowStatus(key));
        }
        else if (string.Equals(issue.Code, "preflight.smapiTooOld", StringComparison.Ordinal))
        {
            command = new RelayCommand(OpenSmapiDownload);
        }

        return new DiagnosticIssueViewModel(title, description, actionText, command);
    }

    private string HintFor(PreflightIssue issue) => issue.Code switch
    {
        "diagnostics.missingDependency" => Format("diagnostics.missingDependencyHint", issue.Arguments),
        "diagnostics.dependencyCycle" => Format("diagnostics.dependencyCycleHint", issue.Arguments),
        "diagnostics.updatesAvailable" => Format("diagnostics.updatesAvailableHint", issue.Arguments),
        "preflight.smapiTooOld" => L("diagnostics.hint.smapiTooOld"),
        _ => string.Empty
    };

    private (string ActionText, string? Section, string? StatusKey) ActionFor(string code) => code switch
    {
        // SMAPI 由用户手动安装：这里只把用户送到官方下载页，不做假的「一键升级」。
        "preflight.smapiTooOld" => (L("diagnostics.action.openSmapi"), null, null),
        "preflight.manifestBroken" => (L("diagnostics.action.loadOrder"), "mods", null),
        "preflight.missingDependencies" or "diagnostics.missingDependency" =>
            (L("diagnostics.action.openMods"), "mods", null),
        "diagnostics.updatesAvailable" => (L("diagnostics.action.goUpdates"), "updates", null),
        _ => (string.Empty, null, null)
    };

    private void UpdateTexts()
    {
        RecheckText = IsBusy ? L("diagnostics.checking") : L("diagnostics.recheck");
        PassedLabelText = Format("diagnostics.stat.passed", [RuleCount.ToString(CultureInfo.CurrentCulture)]);

        var timeText = _lastChecked?.ToString("HH:mm", CultureInfo.CurrentCulture) ?? "—";
        CheckedAtText = Format(
            "diagnostics.lastCheck",
            [
                timeText,
                _mods.Count.ToString(CultureInfo.CurrentCulture),
                RuleCount.ToString(CultureInfo.CurrentCulture)
            ]);

        var footer = new List<string> { Format("diagnostics.footer.passed", [PassedCount.ToString(CultureInfo.CurrentCulture)]) };
        if (_cycleCount == 0)
        {
            footer.Add(L("diagnostics.footer.noCycle"));
        }

        if (!_manifestBroken)
        {
            footer.Add(L("diagnostics.footer.manifestsOk"));
        }

        FooterText = string.Join(" · ", footer);

        var total = BlockerCount + RiskCount + SuggestionCount;
        var subtitle = total == 0
            ? L("diagnostics.subtitleClean")
            : Format(
                "diagnostics.subtitle",
                [
                    BlockerCount.ToString(CultureInfo.CurrentCulture),
                    RiskCount.ToString(CultureInfo.CurrentCulture),
                    SuggestionCount.ToString(CultureInfo.CurrentCulture)
                ]);

        SetHeaderText(L("nav.diagnostics"), subtitle);
    }

    private static Dictionary<string, List<string>> FindMissingDependencies(
        IReadOnlyList<InstalledMod> enabled,
        HashSet<string> installedIds)
    {
        var missing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in enabled)
        {
            foreach (var dependency in mod.Dependencies)
            {
                if (!dependency.IsRequired || installedIds.Contains(dependency.UniqueId))
                {
                    continue;
                }

                if (!missing.TryGetValue(dependency.UniqueId, out var dependents))
                {
                    dependents = [];
                    missing[dependency.UniqueId] = dependents;
                }

                if (!dependents.Contains(mod.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    dependents.Add(mod.Name);
                }
            }
        }

        return missing;
    }

    /// <summary>在已启用模组的依赖图里找一条环；返回环上的 UniqueId（没有环时为空）。</summary>
    private static List<string> FindDependencyCycle(IReadOnlyList<InstalledMod> enabled)
    {
        var byId = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in enabled)
        {
            byId.TryAdd(mod.UniqueId, mod);
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();
        var cycle = new List<string>();

        bool Visit(string id)
        {
            if (done.Contains(id))
            {
                return false;
            }

            if (!visiting.Add(id))
            {
                var start = path.FindIndex(entry => string.Equals(entry, id, StringComparison.OrdinalIgnoreCase));
                cycle.AddRange(start >= 0 ? path.Skip(start) : path);
                return true;
            }

            path.Add(id);
            if (byId.TryGetValue(id, out var mod))
            {
                foreach (var dependency in mod.Dependencies)
                {
                    if (Visit(dependency.UniqueId))
                    {
                        return true;
                    }
                }
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(id);
            done.Add(id);
            return false;
        }

        foreach (var id in byId.Keys)
        {
            if (Visit(id))
            {
                break;
            }
        }

        return cycle;
    }

    private string Localize(string code, IReadOnlyList<string> arguments) =>
        arguments.Count == 0 ? L(code) : Format(code, arguments);

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

    /// <summary>打开 SMAPI 官方下载页（安装器也在这一页）。</summary>
    private void OpenSmapiDownload() => OpenUrl(UpdatesPageViewModel.SmapiDownloadUrl);

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
            ShowStatus("diagnostics.openFailed");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "打开链接失败：{Url}", url);
            ShowStatus("diagnostics.openFailed");
        }
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
