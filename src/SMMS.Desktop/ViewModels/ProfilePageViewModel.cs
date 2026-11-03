using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Profiles;
using SMMS.Desktop.Localization;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;

namespace SMMS.Desktop.ViewModels;

/// <summary>隔离方式下拉项。语言切换时集合会被重建。</summary>
public sealed record IsolationOption(ProfileIsolation Isolation, string DisplayName);

/// <summary>
/// 配置档页的一张环境卡与环境对比表的一行共用的展示项。
/// 文案在 ViewModel 里算好，视图只负责摆位置。
/// </summary>
public sealed class ProfileCardViewModel
{
    public required ModProfile Profile { get; init; }

    public required string DisplayName { get; init; }

    public required bool IsActive { get; init; }

    public required bool IsBuiltIn { get; init; }

    /// <summary>手动排序：能否上移 / 下移。内置档固定位置，恒为 false。</summary>
    public bool CanMoveUp { get; init; }

    public bool CanMoveDown { get; init; }

    public required bool IsFullInstance { get; init; }

    public required int ModCount { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>卡片第二行：N 个模组 · 2.4 GB · SMAPI 4.5.1。</summary>
    public required string MetaText { get; init; }

    /// <summary>卡片底部：0 缺失依赖 · 2 风险。</summary>
    public required string StatusText { get; init; }

    /// <summary>有缺失依赖或风险时状态点与数字变琥珀色。</summary>
    public required bool HasIssues { get; init; }

    /// <summary>对比表「待处理」列。</summary>
    public required string PendingText { get; init; }

    /// <summary>对比表「占用」列。</summary>
    public required string SizeText { get; init; }

    /// <summary>对比表「上次启动」列。当前没有持久化启动时间，统一显示占位符。</summary>
    public required string LastLaunchText { get; init; }

    public required string PathText { get; init; }

    /// <summary>内置纯净档（原版）：启动时走原版，永远没有模组。</summary>
    public required bool IsVanilla { get; init; }

    /// <summary>卡片入口：「模组 (12)」。</summary>
    public required string ModsLinkText { get; init; }

    /// <summary>卡片入口：「美化包」。</summary>
    public required string VanityLinkText { get; init; }

    /// <summary>非当前档时提示会先切换环境；当前档为 null，不显示空 tooltip。</summary>
    public required string? SwitchTip { get; init; }

    /// <summary>刚复制出来的新档：整卡浅紫底高亮一次。</summary>
    public bool IsHighlighted { get; init; }

    // 行内按钮直接复用页面命令，避免模板里做祖先绑定。
    public ICommand? Launch { get; init; }

    public ICommand? Activate { get; init; }

    public ICommand? Rename { get; init; }

    public ICommand? Export { get; init; }

    public ICommand? Delete { get; init; }

    public ICommand? Copy { get; init; }

    public ICommand? OpenMods { get; init; }

    public ICommand? OpenVanity { get; init; }

    public string Id => Profile.Id;

    /// <summary>内置档（默认档 / 纯净档）不能改名删除；按钮仍占位，保证三张卡等高。</summary>
    public bool CanModify => !IsBuiltIn;
}

public partial class ProfilePageViewModel : PageViewModelBase
{
    private const string Placeholder = "—";

    private readonly ProfileService _profiles;
    private readonly ProfilePackService _packs;
    private readonly GameInstallationService _games;
    private readonly ModLibraryService _library;
    private readonly GameLaunchService _launcher;
    private readonly ILogger<ProfilePageViewModel> _logger;

    /// <summary>刚复制出来的档 Id，下一次 Reload 时高亮展示。</summary>
    private string? _highlightProfileId;

    /// <summary>防止 EnsureVanillaProfile 触发的 ProfilesChanged 在 Reload 内部重入。</summary>
    private bool _reloading;

    /// <summary>
    /// 卡片上的「模组 / 美化包」入口：先切到该配置档，再请外壳导航到对应页面。
    /// 参数为（配置档 Id, 页面段名 "mods" / "vanity"），由 MainWindowViewModel 订阅。
    /// </summary>
    public event EventHandler<(string ProfileId, string Section)>? NavigationRequested;

    public ProfilePageViewModel(
        ProfileService profiles,
        ProfilePackService packs,
        GameInstallationService games,
        ModLibraryService library,
        GameLaunchService launcher,
        ILocalizationService localization,
        ILogger<ProfilePageViewModel> logger)
        : base(localization)
    {
        _profiles = profiles;
        _packs = packs;
        _games = games;
        _library = library;
        _launcher = launcher;
        _logger = logger;

        _profiles.ProfilesChanged += OnProfilesChanged;
        Reload();
        RebuildIsolationOptions();

        _logger.LogInformation("配置档页初始化：条目={Count}，IsBusy={Busy}", Items.Count, IsBusy);
    }

    public ObservableCollection<IsolationOption> IsolationOptions { get; } = [];

    [ObservableProperty]
    public partial IsolationOption? SelectedIsolation { get; set; }

    /// <summary>选中完整实例时展示磁盘占用提示。</summary>
    public bool IsFullInstanceSelected => SelectedIsolation?.Isolation.IsolateGameInstance == true;

    public ObservableCollection<ProfileCardViewModel> Items { get; } = [];

    /// <summary>排序方式已取消：顺序由玩家手动调整并持久化。</summary>
    [RelayCommand]
    private void MoveProfileUp(ProfileCardViewModel? card)
    {
        if (card is not null && !card.IsBuiltIn)
        {
            _profiles.MoveProfile(card.Profile.Id, -1);
        }
    }

    [RelayCommand]
    private void MoveProfileDown(ProfileCardViewModel? card)
    {
        if (card is not null && !card.IsBuiltIn)
        {
            _profiles.MoveProfile(card.Profile.Id, 1);
        }
    }

    /// <summary>内置档的固定位次：原版第 1、默认第 2，其余内置档排后面。</summary>
    private static int BuiltInRank(ModProfile profile) => profile.Id switch    {
        ProfileService.VanillaProfileId => 0,
        ModProfile.DefaultId => 1,
        _ => 2
    };


    [ObservableProperty]
    public partial bool HasProfiles { get; set; }

    /// <summary>环境卡列数：3 张以内单排铺满，4 张起两列，避免最后一行只剩一张孤卡。</summary>
    public int CardColumns => Items.Count switch
    {
        <= 0 => 1,
        1 => 1,
        2 => 2,
        3 => 3,
        4 => 2,
        _ => 3
    };

    /// <summary>「新建配置档」卡展开后的表单面板。</summary>
    [ObservableProperty]
    public partial bool IsCreatePanelOpen { get; set; }

    [ObservableProperty]
    public partial string NewProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CopyFromActive { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>导出时是否一并打包模组文件。默认关闭：分发他人模组需授权。</summary>
    [ObservableProperty]
    public partial bool ExportIncludeMods { get; set; }

    [ObservableProperty]
    public partial bool IsPacking { get; set; }

    /// <summary>由视图注入：弹保存对话框，返回目标路径。</summary>
    public Func<string, string, Task<string?>>? SavePathRequested { get; set; }

    /// <summary>由视图注入：弹打开对话框，返回选中文件路径。</summary>
    public Func<Task<string?>>? OpenPathRequested { get; set; }

    /// <summary>由视图注入：弹文件夹选择对话框。</summary>
    public Func<Task<string?>>? PickFolderRequested { get; set; }

    public string CreateButtonText => IsBusy ? L("profiles.creating") : L("profiles.create");

    /// <summary>由视图注入：弹确认框。</summary>
    public Func<ConfirmDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    /// <summary>由视图注入：弹文本输入框，返回 null 表示取消。</summary>
    public Func<TextPromptViewModel, Task<string?>>? PromptRequested { get; set; }

    [RelayCommand]
    private void ToggleCreatePanel() => IsCreatePanelOpen = !IsCreatePanelOpen;

    // 名称留空时不禁用按钮：禁用态在浅色卡片上太不显眼，玩家会以为按钮不存在。
    // 改为允许点击，点击后给出明确提示。
    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        _logger.LogInformation("收到创建配置档请求：名称=「{Name}」，复制={Copy}", NewProfileName, CopyFromActive);

        if (IsBusy)
        {
            _logger.LogWarning("创建请求被忽略：已有任务在执行");
            return;
        }

        IsBusy = true;
        HasError = false;

        try
        {
            var result = await _profiles.CreateAsync(
                NewProfileName,
                CopyFromActive,
                SelectedIsolation?.Isolation ?? ProfileIsolation.ModsOnly).ConfigureAwait(true);

            if (result.Profile is null)
            {
                _logger.LogWarning("创建配置档失败：{ErrorKey}", result.ErrorKey);
                ErrorText = L(result.ErrorKey ?? "profiles.error.failed");
                HasError = true;
                return;
            }

            _logger.LogInformation("配置档创建成功：{Name} → {Path}", result.Profile.Name, result.Profile.ModsPath);
            NewProfileName = string.Empty;
            IsCreatePanelOpen = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建配置档时发生未处理异常");
            ErrorText = L("profiles.error.failed");
            HasError = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>切换当前使用的配置档（只改启动参数，不动文件）。</summary>
    [RelayCommand]
    private void Activate(ProfileCardViewModel? item)
    {
        if (item is null || item.IsActive)
        {
            return;
        }

        if (!_profiles.SetActive(item.Id))
        {
            _logger.LogWarning("切换配置档失败：{Id}", item.Id);
            return;
        }

        ShowStatus(string.Format(CultureInfo.CurrentCulture, L("profiles.switched"), item.DisplayName));
    }

    /// <summary>以该配置档启动游戏。账号/预检确认由主窗口启动按钮负责，这里只做快捷启动。</summary>
    [RelayCommand]
    private void Launch(ProfileCardViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsActive && _launcher.State != GameLaunchState.Idle)
        {
            ShowError("launch.error.alreadyRunning");
            return;
        }

        if (!_profiles.SetActive(item.Id))
        {
            ShowError("profiles.error.failed");
            return;
        }

        var game = _games.ResolveForProfile(item.Profile);
        if (game is null)
        {
            ShowError(item.Profile.HasOwnInstance ? "launch.error.instanceMissing" : "launch.error.noGame");
            return;
        }

        // 纯净档（原版）永远走原版；其它档没装 SMAPI 时退回原版，至少能让游戏跑起来。
        var mode = item.IsVanilla
            ? LaunchMode.Vanilla
            : game.IsSmapiInstalled ? LaunchMode.Smapi : LaunchMode.Vanilla;
        var result = _launcher.Launch(game, item.Profile, mode);

        if (!result.Success)
        {
            ShowError(result.ErrorKey ?? "launch.error.failed");
            return;
        }

        ShowStatus(string.Format(CultureInfo.CurrentCulture, L("profiles.launched"), item.DisplayName));
    }

    /// <summary>一键复制：真实复制模组目录与启用状态，名称默认「源名称 副本」。</summary>
    [RelayCommand]
    private async Task CopyAsync(ProfileCardViewModel? item)
    {
        if (item is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasError = false;

        try
        {
            var newName = string.Format(CultureInfo.CurrentCulture, L("profiles.copySuffix"), item.DisplayName);
            var result = await _profiles.CopyAsync(item.Id, newName).ConfigureAwait(true);

            if (result.Profile is null)
            {
                _logger.LogWarning("复制配置档失败：{ErrorKey}", result.ErrorKey);
                ShowError(result.ErrorKey ?? "profiles.error.copyFailed");
                return;
            }

            _highlightProfileId = result.Profile.Id;

            // 服务层已经触发 ProfilesChanged（会 Reload 一次），这里再刷一次以应用高亮。
            Reload();
            ShowStatus(string.Format(CultureInfo.CurrentCulture, L("profiles.copyDone"), result.Profile.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复制配置档时发生未处理异常");
            ShowError("profiles.error.copyFailed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>打开该配置档的模组列表：先切换环境，再请求外壳导航。</summary>
    [RelayCommand]
    private void OpenMods(ProfileCardViewModel? item) => RequestNavigation(item, "mods");

    /// <summary>打开该配置档的美化包列表：先切换环境，再请求外壳导航。</summary>
    [RelayCommand]
    private void OpenVanity(ProfileCardViewModel? item) => RequestNavigation(item, "vanity");

    private void RequestNavigation(ProfileCardViewModel? item, string section)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsActive && !_profiles.SetActive(item.Id))
        {
            _logger.LogWarning("跳转前切换配置档失败：{Id}", item.Id);
            ShowError("profiles.error.failed");
            return;
        }

        NavigationRequested?.Invoke(this, (item.Id, section));
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileCardViewModel? item)
    {
        if (item is null || item.IsBuiltIn || ConfirmRequested is null)
        {
            return;
        }

        var confirmed = await ConfirmRequested(new ConfirmDialogViewModel(
            L("profiles.deleteTitle"),
            string.Format(CultureInfo.CurrentCulture, L("profiles.deleteMessage"), item.DisplayName),
            L("profiles.deleteConfirm"),
            L("common.cancel"))).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        var result = _profiles.Delete(item.Id);
        ReportIfFailed(result);
    }

    [RelayCommand]
    private async Task RenameAsync(ProfileCardViewModel? item)
    {
        if (item is null || item.IsBuiltIn || PromptRequested is null)
        {
            return;
        }

        var input = await PromptRequested(new TextPromptViewModel(
            L("profiles.renameTitle"),
            L("profiles.renameMessage"),
            item.DisplayName,
            L("profiles.renameConfirm"),
            L("common.cancel"))).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        var result = _profiles.Rename(item.Id, input);
        ReportIfFailed(result);
    }

    /// <summary>纳入一个已存在的游戏目录：只建立引用，不复制文件。</summary>
    [RelayCommand]
    private async Task AdoptAsync()
    {
        if (IsPacking || PickFolderRequested is null || PromptRequested is null)
        {
            return;
        }

        var path = await PickFolderRequested().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var suggested = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        var name = await PromptRequested(new TextPromptViewModel(
            L("profiles.adoptTitle"),
            L("profiles.adoptMessage"),
            string.IsNullOrWhiteSpace(suggested) ? "Stardew Valley" : suggested,
            L("profiles.adoptConfirm"),
            L("common.cancel"))).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var result = _profiles.AdoptExternal(name, path);

        if (result.Profile is null)
        {
            ShowError(result.ErrorKey ?? "profiles.error.failed");
            return;
        }

        ShowStatus(string.Format(CultureInfo.CurrentCulture, L("profiles.adoptDone"), result.Profile.Name));
    }

    [RelayCommand]
    private async Task ExportAsync(ProfileCardViewModel? item)
    {
        if (item is null || IsPacking || SavePathRequested is null)
        {
            return;
        }

        var suggested = SanitizeFileName(item.DisplayName) + ".smms.zip";
        var target = await SavePathRequested(suggested, L("profiles.exportFilter")).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        IsPacking = true;

        try
        {
            var result = await _packs.ExportAsync(item.Profile, ExportIncludeMods, target).ConfigureAwait(true);

            if (!result.Success)
            {
                ShowError(result.ErrorKey ?? "profiles.error.exportFailed");
                return;
            }

            ShowStatus(string.Format(
                CultureInfo.CurrentCulture, L("profiles.exportDone"), result.ModCount));
        }
        finally
        {
            IsPacking = false;
        }
    }

    /// <summary>导出并把模组文件一并打包（分发前需确认模组授权）。</summary>
    [RelayCommand]
    private async Task ExportWithModsAsync(ProfileCardViewModel? item)
    {
        var previous = ExportIncludeMods;
        ExportIncludeMods = true;

        try
        {
            await ExportAsync(item).ConfigureAwait(true);
        }
        finally
        {
            ExportIncludeMods = previous;
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (IsPacking || OpenPathRequested is null)
        {
            return;
        }

        var path = await OpenPathRequested().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsPacking = true;

        try
        {
            var result = await _packs.ImportAsync(path).ConfigureAwait(true);

            if (!result.Success || result.Profile is null)
            {
                ShowError(result.ErrorKey ?? "profiles.error.invalidPack");
                return;
            }

            ShowStatus(result.MissingMods.Count == 0
                ? string.Format(CultureInfo.CurrentCulture, L("profiles.importDone"), result.Profile.Name, result.RestoredMods)
                : string.Format(CultureInfo.CurrentCulture, L("profiles.importMissing"), result.Profile.Name, result.RestoredMods, result.MissingMods.Count));

            _logger.LogInformation(
                "导入完成：还原 {Restored} 个，缺失 {Missing} 个",
                result.RestoredMods, result.MissingMods.Count);
        }
        finally
        {
            IsPacking = false;
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    private void ShowStatus(string text)
    {
        StatusText = text;
        HasStatus = true;
        HasError = false;
    }

    private void ShowError(string key)
    {
        ErrorText = L(key);
        HasError = true;
        HasStatus = false;
    }

    private void ReportIfFailed(ProfileActionResult result)
    {
        if (result.Success)
        {
            HasError = false;
            _logger.LogInformation("配置档操作成功");
            return;
        }

        _logger.LogWarning("配置档操作失败：{ErrorKey}", result.ErrorKey);
        ShowError(result.ErrorKey ?? "profiles.error.failed");
    }

    public override void RefreshTexts()
    {
        Reload();
        RebuildIsolationOptions();
        OnPropertyChanged(nameof(CreateButtonText));
    }

    partial void OnSelectedIsolationChanged(IsolationOption? value)
        => OnPropertyChanged(nameof(IsFullInstanceSelected));

    private void RebuildIsolationOptions()
    {
        var previous = SelectedIsolation?.Isolation ?? ProfileIsolation.ModsOnly;

        IsolationOptions.Clear();
        IsolationOptions.Add(new IsolationOption(ProfileIsolation.ModsOnly, L("profiles.isolation.modsOnly")));
        IsolationOptions.Add(new IsolationOption(ProfileIsolation.FullInstance, L("profiles.isolation.full")));

        SelectedIsolation = IsolationOptions.FirstOrDefault(option => option.Isolation == previous)
                            ?? IsolationOptions[0];
    }

    partial void OnNewProfileNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CreateButtonText));
        CreateCommand.NotifyCanExecuteChanged();
    }

    public void Reload()
    {
        if (_reloading)
        {
            return;
        }

        _reloading = true;

        try
        {
            // 内置纯净档按需落库；首次创建会触发 ProfilesChanged，重入已由 _reloading 挡住。
            _profiles.EnsureVanillaProfile();

            var active = _profiles.GetActive();

            Items.Clear();

            // 顺序完全由玩家手动决定（存 ProfileOrder）；内置档固定前两位。
            var all = _profiles.GetAll();
            var builtIn = all.Where(profile => profile.IsBuiltIn).OrderBy(BuiltInRank);
            var rest = all.Where(profile => !profile.IsBuiltIn).ToList();

            foreach (var profile in builtIn)
            {
                Items.Add(BuildCard(profile, ResolveDisplayName(profile), profile.Id == active?.Id));
            }

            for (var i = 0; i < rest.Count; i++)
            {
                var profile = rest[i];
                Items.Add(BuildCard(
                    profile,
                    ResolveDisplayName(profile),
                    profile.Id == active?.Id,
                    canMoveUp: i > 0,
                    canMoveDown: i < rest.Count - 1));
            }

            HasProfiles = Items.Count > 0;

            // 高亮只生效一次：卡片已按快照建好，后续刷新不再重复点亮。
            _highlightProfileId = null;

            OnPropertyChanged(nameof(CardColumns));
            UpdateHeader();
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>内置档名按 Id 本地化，自建档用持久化名称。</summary>
    private string ResolveDisplayName(ModProfile profile)
    {
        if (string.Equals(profile.Id, ProfileService.VanillaProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return L("profiles.vanillaName");
        }

        if (profile.IsBuiltIn)
        {
            return L("profiles.default");
        }

        return string.IsNullOrWhiteSpace(profile.Name) ? L("profiles.default") : profile.Name;
    }

    /// <summary>顶栏标题与副标题：环境数 + 总占用。</summary>
    private void UpdateHeader()
    {
        PageTitle = L("profiles.title");
        PageSubtitle = string.Format(
            CultureInfo.CurrentCulture,
            L("profiles.subtitle"),
            Items.Count,
            FormatSize(Items.Sum(item => item.SizeBytes)));
    }

    private ProfileCardViewModel BuildCard(ModProfile profile, string displayName, bool isActive,
        bool canMoveUp = false, bool canMoveDown = false)
    {
        if (string.Equals(profile.Id, ProfileService.VanillaProfileId, StringComparison.OrdinalIgnoreCase))
        {
            return BuildVanillaCard(profile, displayName, isActive);
        }

        var snapshot = _library.Load(profile.ModsPath, GetDisabledPath(profile));
        var mods = snapshot.Mods;

        var installed = mods
            .Select(mod => mod.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = mods
            .SelectMany(mod => mod.Dependencies)
            .Where(dependency => dependency.IsRequired && !installed.Contains(dependency.UniqueId))
            .Select(dependency => dependency.UniqueId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        // 风险 = 清单解析失败的目录数；完整实例丢失也算一项风险，否则玩家看到「0 风险」却启动不了。
        var risk = snapshot.Warnings.Count;

        var game = _games.ResolveForProfile(profile);
        if (profile.HasOwnInstance && game is null)
        {
            risk++;
        }

        var size = MeasureDirectory(profile.ModsPath);
        var sizeText = FormatSize(size);
        var applied = missing + risk;
        var smapiVersion = game?.SmapiVersion;

        var metaText = string.IsNullOrWhiteSpace(smapiVersion)
            ? string.Format(CultureInfo.CurrentCulture, L("profiles.cardMetaNoSmapi"), mods.Count, sizeText)
            : string.Format(CultureInfo.CurrentCulture, L("profiles.cardMeta"), mods.Count, sizeText, smapiVersion);

        return new ProfileCardViewModel
        {
            Profile = profile,
            DisplayName = displayName,
            IsActive = isActive,
            IsBuiltIn = profile.IsBuiltIn,
            CanMoveUp = canMoveUp,
            CanMoveDown = canMoveDown,
            IsFullInstance = profile.HasOwnInstance,
            ModCount = mods.Count,
            SizeBytes = size,
            MetaText = metaText,
            StatusText = string.Format(CultureInfo.CurrentCulture, L("profiles.issuesCount"), missing, risk),
            HasIssues = applied > 0,
            PendingText = applied == 0
                ? Placeholder
                : string.Format(CultureInfo.CurrentCulture, L("profiles.pendingCount"), applied),
            SizeText = sizeText,
            LastLaunchText = FormatLastLaunch(profile.Id),
            PathText = profile.ModsPath,
            IsVanilla = false,
            ModsLinkText = string.Format(CultureInfo.CurrentCulture, L("profiles.modsLink"), mods.Count),
            VanityLinkText = L("profiles.vanityLink"),
            SwitchTip = isActive ? null : L("profiles.switchTip"),
            IsHighlighted = profile.Id == _highlightProfileId,
            Launch = LaunchCommand,
            Activate = ActivateCommand,
            Rename = RenameCommand,
            Export = ExportCommand,
            Delete = DeleteCommand,
            Copy = CopyCommand,
            OpenMods = OpenModsCommand,
            OpenVanity = OpenVanityCommand
        };
    }

    /// <summary>「上次启动」列：只在最近一次启动确实用这个档时显示相对时间，否则占位。</summary>
    private string FormatLastLaunch(string profileId)
    {
        var settings = _games.Settings;
        if (settings.LastLaunchAt is not { } at ||
            !string.Equals(settings.LastLaunchProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            return Placeholder;
        }

        var span = DateTimeOffset.Now - at;
        return span switch
        {
            { TotalMinutes: < 1 } => L("launchpad.justNow"),
            { TotalHours: < 1 } => string.Format(
                CultureInfo.CurrentCulture, L("launchpad.minutesAgo"), (int)span.TotalMinutes),
            { TotalDays: < 1 } => string.Format(
                CultureInfo.CurrentCulture, L("launchpad.hoursAgo"), (int)span.TotalHours),
            _ => string.Format(CultureInfo.CurrentCulture, L("launchpad.daysAgo"), (int)span.TotalDays)
        };
    }

    /// <summary>纯净档（原版）卡片：没有模组，启动走原版，元信息固定为「原版启动」。</summary>
    private ProfileCardViewModel BuildVanillaCard(ModProfile profile, string displayName, bool isActive)
    {
        var sizeText = FormatSize(0);

        return new ProfileCardViewModel
        {
            Profile = profile,
            DisplayName = displayName,
            IsActive = isActive,
            IsBuiltIn = profile.IsBuiltIn,
            IsFullInstance = false,
            IsVanilla = true,
            ModCount = 0,
            SizeBytes = 0,
            MetaText = L("profiles.vanillaMeta"),
            StatusText = L("profiles.vanillaStatus"),
            HasIssues = false,
            PendingText = Placeholder,
            SizeText = sizeText,
            LastLaunchText = FormatLastLaunch(profile.Id),
            PathText = profile.ModsPath,
            ModsLinkText = string.Format(CultureInfo.CurrentCulture, L("profiles.modsLink"), 0),
            VanityLinkText = L("profiles.vanityLink"),
            SwitchTip = isActive ? null : L("profiles.switchTip"),
            IsHighlighted = profile.Id == _highlightProfileId,
            Launch = LaunchCommand,
            Activate = ActivateCommand,
            Rename = RenameCommand,
            Export = ExportCommand,
            Delete = DeleteCommand,
            Copy = CopyCommand,
            OpenMods = OpenModsCommand,
            OpenVanity = OpenVanityCommand
        };
    }

    /// <summary>禁用区路径，与 ProfileService/ModToggleService 一致：内置档共享工作区里的 disabled。</summary>
    private string? GetDisabledPath(ModProfile profile)
    {
        if (profile.IsBuiltIn)
        {
            var game = _games.Current ?? _games.Resolve();
            return game is null ? null : Path.Combine(game.WorkspacePath, "disabled");
        }

        var parent = Path.GetDirectoryName(profile.ModsPath);
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "_disabled");
    }

    private static long MeasureDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件读不到就跳过，不影响整体占用估算。
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string FormatSize(long bytes)
    {
        const long kilobyte = 1024;
        const long megabyte = kilobyte * 1024;
        const long gigabyte = megabyte * 1024;

        if (bytes >= gigabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.##} GB", bytes / (double)gigabyte);
        }

        if (bytes >= megabyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} MB", bytes / (double)megabyte);
        }

        if (bytes >= kilobyte)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.#} KB", bytes / (double)kilobyte);
        }

        return string.Format(CultureInfo.CurrentCulture, "{0} B", bytes);
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        // 事件可能来自任意线程，集合更新必须回到界面线程。
        if (Dispatcher.UIThread.CheckAccess())
        {
            Reload();
        }
        else
        {
            Dispatcher.UIThread.Post(Reload);
        }
    }

    public override void Dispose()
    {
        _profiles.ProfilesChanged -= OnProfilesChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
