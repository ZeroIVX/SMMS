using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SMMS.Core.Mods;
using SMMS.Core.Updates;

namespace SMMS.Desktop.ViewModels;

/// <summary>依赖 / 被依赖清单里的一行。名称与状态文案已本地化，视图只负责排版。</summary>
public sealed record ModRelationRow(string Name, string StatusText, bool IsMissing);

/// <summary>模组列表中的一行。只承载展示所需的派生字段，不持有任何 UI 元素。</summary>
public sealed partial class ModListItemViewModel : ViewModelBase
{
    private readonly Func<string, string> _localize;

    private List<ModRelationRow> _dependencyRows = [];
    private List<string> _dependentNames = [];
    private int _missingDependencyCount;
    private int _missingRequiredDependencyCount;

    public ModListItemViewModel(
        InstalledMod mod,
        ModUpdateInfo? update,
        string? note,
        bool preferNote,
        Func<string, string> localize)
    {
        Mod = mod;
        Update = update;
        Note = note?.Trim() ?? string.Empty;
        PreferNote = preferNote;
        IsEnabled = mod.IsEnabled;
        _localize = localize;
    }

    public InstalledMod Mod { get; }

    public ModUpdateInfo? Update { get; }

    /// <summary>玩家自己写的备注（也可能是翻译结果）。非空时优先于作者描述显示。</summary>
    public string Note { get; private set; }

    public bool HasNote => Note.Length > 0;

    /// <summary>列表行里的单行备注摘要：换行/连续空白折叠成空格，交给 TextTrimming 收尾。</summary>
    public string NotePreview
    {
        get
        {
            if (Note.Length == 0)
            {
                return string.Empty;
            }

            var text = Note.ReplaceLineEndings(" ").Trim();

            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            return text;
        }
    }

    /// <summary>备注保存/翻译/清空后原地更新，不必重建整个列表。</summary>
    public void UpdateNote(string? note)
    {
        var next = note?.Trim() ?? string.Empty;

        if (string.Equals(Note, next, StringComparison.Ordinal))
        {
            return;
        }

        Note = next;

        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(NotePreview));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>列表是否优先展示备注。由页面的开关控制。</summary>
    public bool PreferNote { get; }

    /// <summary>界面开关直接绑定它；切换失败时由页面回滚。</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>切换启用状态的命令。由页面在构造列表项时注入，避免列表项反向依赖业务服务。</summary>
    public IRelayCommand? ToggleCommand { get; set; }

    public string Name => Mod.Name;

    /// <summary>第二行：开关打开时优先显示备注，否则「UniqueID · 类型 · 含中文」。</summary>
    public string SubtitleText
    {
        get
        {
            if (PreferNote && HasNote)
            {
                return Note;
            }

            var kind = KindTagText;
            var text = $"{Mod.UniqueId} · {kind}";

            if (HasChinese)
            {
                text += $" · {_localize("mods.detail.chinese")}";
            }

            return text;
        }
    }

    /// <summary>旧的第二行格式，保留给潜在复用方。</summary>
    public string DetailText
    {
        get
        {
            if (PreferNote && HasNote)
            {
                return Note;
            }

            var author = string.IsNullOrWhiteSpace(Mod.Author) ? Mod.UniqueId : Mod.Author;
            var summary = Summarize(Mod.Description);

            return string.IsNullOrEmpty(summary) ? author : $"{author} · {summary}";
        }
    }

    /// <summary>摘要控制在半行内，太长会挤掉右侧的版本与开关。</summary>
    private static string Summarize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var text = description.ReplaceLineEndings(" ").Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Length <= 46 ? text : text[..46] + "…";
    }

    public bool HasChinese => Mod.HasChineseTranslation;

    public bool IsEnglishOnly => !Mod.HasChineseTranslation;

    public string VersionText => Mod.Version?.ToString() ?? "—";

    public string SuggestedVersionText => Update?.SuggestedVersion ?? string.Empty;

    public bool HasUpdate => Update?.HasUpdate == true;

    /// <summary>形如「→ 2.6.1」。箭头与版本号都是语言无关内容。</summary>
    public string UpdateText => Update?.SuggestedVersion is { } version ? $"→ {version}" : string.Empty;

    public bool IsIncompatible => Update?.IsIncompatible == true;

    public string UpdateTooltip => Update?.UpdateUrl ?? string.Empty;

    /// <summary>没有图标资源时用名称首字作为占位。</summary>
    public string Initial
    {
        get
        {
            var name = Mod.Name.Trim();
            return name.Length == 0 ? "?" : name[..1].ToUpperInvariant();
        }
    }

    public bool IsContentPack => Mod.IsContentPack;

    public bool IsCodeMod => Mod.IsCodeMod;

    public bool IsDisabled => !Mod.IsEnabled;

    public string Tooltip => string.IsNullOrWhiteSpace(Mod.Description) ? SubtitleText : Mod.Description;

    public bool Matches(string keyword) =>
        Mod.Name.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
        || Mod.Author.Contains(keyword, StringComparison.CurrentCultureIgnoreCase)
        || Mod.UniqueId.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    // ---- 列表行 ----

    /// <summary>1 起的加载顺序。由页面在排序后写入。</summary>
    public int Order { get; private set; } = 1;

    /// <summary>两位序号，例如 <c>01</c>。</summary>
    public string OrderText => Order.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>没有依赖时显示 0，与设计稿的「依赖」列一致。</summary>
    public string DependencyCountText => DependencyCount.ToString(CultureInfo.CurrentCulture);

    public int DependencyCount => _dependencyRows.Count;

    public void SetOrder(int order)
    {
        if (Order == order)
        {
            return;
        }

        Order = order;
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(OrderText));
        OnPropertyChanged(nameof(LoadOrderText));
    }

    // ---- 状态 ----

    /// <summary>有风险：缺必需依赖，或更新检查判定与当前 SMAPI / 游戏版本不兼容。</summary>
    public bool HasRisk => _missingRequiredDependencyCount > 0 || IsIncompatible;

    public string RiskPillText => _localize("mods.detail.risk");

    /// <summary>提示内容把各类风险拼在一起，悬停状态圆点时显示。</summary>
    public string RiskTip
    {
        get
        {
            var parts = new List<string>(3);

            if (_missingRequiredDependencyCount > 0)
            {
                parts.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    _localize("mods.detail.riskMissing"),
                    _missingRequiredDependencyCount));
            }

            if (IsIncompatible)
            {
                parts.Add(_localize("mods.detail.riskIncompatible"));
            }

            if (HasUpdate)
            {
                parts.Add(_localize("mods.detail.riskUpdate"));
            }

            return string.Join(" · ", parts);
        }
    }

    // ---- 详情面板 ----

    public string AuthorText => string.IsNullOrWhiteSpace(Mod.Author) ? "—" : Mod.Author;

    public string UniqueIdText => Mod.UniqueId;

    /// <summary>详情标题下方那行：<c>FlashShifter.SVE · v1.15.11</c>。</summary>
    public string MetaText => $"{Mod.UniqueId} · v{VersionText}";

    public string DescriptionText => string.IsNullOrWhiteSpace(Mod.Description) ? "—" : Mod.Description;

    public string MinimumApiText => Mod.MinimumApiVersion?.ToString() ?? "—";

    public string FolderPathText => Mod.DirectoryPath;

    public string KindTagText => _localize(IsContentPack ? "mods.kind.content" : "mods.kind.code");

    public string MinimumApiValueText => MinimumApiText;

    /// <summary>保留旧标记，供潜在复用方区分代码模组与内容包。</summary>
    public string KindText => Mod.IsContentPack ? "content" : "code";

    public IReadOnlyList<ModDependencyRef> Dependencies => Mod.Dependencies;

    public bool HasDependencies => DependencyCount > 0;

    public IReadOnlyList<ModRelationRow> DependencyRows => _dependencyRows;

    public string DependenciesHeaderText => string.Format(
        CultureInfo.CurrentCulture,
        _localize("mods.detail.dependenciesHeader"),
        DependencyCount);

    public bool AllDependenciesMet => _missingDependencyCount == 0;

    public bool HasMissingDependencies => _missingDependencyCount > 0;

    public string DependenciesSummaryText => _missingDependencyCount == 0
        ? _localize("mods.detail.allMet")
        : string.Format(CultureInfo.CurrentCulture, _localize("mods.detail.missingCount"), _missingDependencyCount);

    /// <summary>被依赖：反查全部已安装模组里谁声明了本模组（含内容包声明的 ContentPackFor）。</summary>
    public bool HasDependents => _dependentNames.Count > 0;

    public string DependentsInlineText => string.Join(" · ", _dependentNames);

    public string DependentsHeaderText => string.Format(
        CultureInfo.CurrentCulture,
        _localize("mods.detail.dependentsHeader"),
        _dependentNames.Count);

    /// <summary>加载顺序当前位置，例如「第 03 位」。</summary>
    public string LoadOrderText => string.Format(
        CultureInfo.CurrentCulture,
        _localize("mods.detail.loadOrderPos"),
        OrderText);

    /// <summary>有更新时底部主按钮文案「更新到 1.16.0」，否则退化为打开 Nexus / 目录。</summary>
    public string PrimaryActionText
    {
        get
        {
            if (Update?.SuggestedVersion is { } version)
            {
                return string.Format(CultureInfo.CurrentCulture, _localize("mods.detail.updateTo"), version);
            }

            return HasNexus ? _localize("mods.detail.nexus") : _localize("mods.openFolder");
        }
    }

    public string UpdateBannerText => Update?.SuggestedVersion is { } version
        ? string.Format(CultureInfo.CurrentCulture, _localize("mods.detail.updateBanner"), version)
        : string.Empty;

    /// <summary>从全局模组快照里补齐依赖 / 被依赖关系。页面每次刷新或切换语言后调用。</summary>
    public void AttachRelations(IReadOnlyList<InstalledMod> all)
    {
        var byId = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in all)
        {
            byId.TryAdd(mod.UniqueId, mod);
        }

        var rows = new List<ModRelationRow>();
        var missing = 0;
        var missingRequired = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dependency in EnumerateDependencies())
        {
            if (!seen.Add(dependency.UniqueId))
            {
                continue;
            }

            if (byId.TryGetValue(dependency.UniqueId, out var installed))
            {
                rows.Add(new ModRelationRow(
                    installed.Name,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        _localize("mods.detail.depInstalled"),
                        installed.Version?.ToString() ?? "—"),
                    false));
            }
            else
            {
                missing++;

                if (dependency.IsRequired)
                {
                    missingRequired++;
                }

                rows.Add(new ModRelationRow(
                    dependency.UniqueId,
                    _localize("mods.detail.depMissing"),
                    true));
            }
        }

        _dependencyRows = rows;
        _missingDependencyCount = missing;
        _missingRequiredDependencyCount = missingRequired;

        _dependentNames = all
            .Where(other => !string.Equals(other.UniqueId, Mod.UniqueId, StringComparison.OrdinalIgnoreCase)
                            && (other.Dependencies.Any(dep =>
                                    string.Equals(dep.UniqueId, Mod.UniqueId, StringComparison.OrdinalIgnoreCase))
                                || string.Equals(other.ContentPackFor, Mod.UniqueId, StringComparison.OrdinalIgnoreCase)))
            .Select(other => other.Name)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        OnPropertyChanged(nameof(DependencyRows));
        OnPropertyChanged(nameof(DependencyCount));
        OnPropertyChanged(nameof(DependencyCountText));
        OnPropertyChanged(nameof(HasDependencies));
        OnPropertyChanged(nameof(DependenciesHeaderText));
        OnPropertyChanged(nameof(AllDependenciesMet));
        OnPropertyChanged(nameof(HasMissingDependencies));
        OnPropertyChanged(nameof(DependenciesSummaryText));
        OnPropertyChanged(nameof(HasDependents));
        OnPropertyChanged(nameof(DependentsInlineText));
        OnPropertyChanged(nameof(DependentsHeaderText));
        OnPropertyChanged(nameof(HasRisk));
        OnPropertyChanged(nameof(RiskPillText));
        OnPropertyChanged(nameof(RiskTip));
    }

    /// <summary>manifest 声明的依赖 + 内容包指向的框架。</summary>
    private IEnumerable<ModDependencyRef> EnumerateDependencies()
    {
        foreach (var dependency in Mod.Dependencies)
        {
            yield return dependency;
        }

        if (!string.IsNullOrWhiteSpace(Mod.ContentPackFor))
        {
            yield return new ModDependencyRef(Mod.ContentPackFor, true, null);
        }
    }

    /// <summary>语言切换后重算所有本地化文案。</summary>
    public void RefreshTexts(IReadOnlyList<InstalledMod> all)
    {
        AttachRelations(all);

        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(KindTagText));
        OnPropertyChanged(nameof(LoadOrderText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(UpdateBannerText));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(MinimumApiText));
    }

    public string? NexusUrl => string.IsNullOrWhiteSpace(Mod.NexusId)
        ? null
        : $"https://www.nexusmods.com/stardewvalley/mods/{Mod.NexusId}";

    public bool HasNexus => NexusUrl is not null;
}
