using SMMS.Core.Versioning;

namespace SMMS.Core.Mods;

/// <summary>扫描得到的一个已安装模组。</summary>
public sealed record InstalledMod
{
    public required string DirectoryPath { get; init; }

    /// <summary>模组所在文件夹名，用于展示与后续启用 / 禁用定位。</summary>
    public required string FolderName { get; init; }

    public required string UniqueId { get; init; }

    public required string Name { get; init; }

    public string Author { get; init; } = string.Empty;

    public SemanticVersion? Version { get; init; }

    public string? Description { get; init; }

    public SemanticVersion? MinimumApiVersion { get; init; }

    public IReadOnlyList<string> UpdateKeys { get; init; } = [];

    /// <summary>manifest 里声明的依赖，用于启动前检查必需依赖是否齐全。</summary>
    public IReadOnlyList<ModDependencyRef> Dependencies { get; init; } = [];

    /// <summary>从 <see cref="UpdateKeys"/> 中解析出的 Nexus 模组 ID。</summary>
    public string? NexusId { get; init; }

    /// <summary>内容包没有入口程序集，依赖其他框架模组加载。</summary>
    public bool IsContentPack { get; init; }

    /// <summary>内容包依赖的框架 UniqueID，例如 <c>Pathoschild.ContentPatcher</c>。</summary>
    public string? ContentPackFor { get; init; }

    /// <summary>模组是否自带简体/繁体中文翻译（i18n 下有 zh 开头的 json）。</summary>
    public bool HasChineseTranslation { get; init; }

    /// <summary>是否处于启用状态。禁用态位于配置档的 <c>_disabled</c> 目录，SMAPI 不会扫描。</summary>
    public bool IsEnabled { get; init; } = true;

    public bool IsCodeMod => !IsContentPack;
}

/// <summary>一次扫描的结果。警告用于提示被跳过的目录，不阻断整体扫描。</summary>
public sealed record ModScanResult(IReadOnlyList<InstalledMod> Mods, IReadOnlyList<string> Warnings)
{
    public static ModScanResult Empty { get; } = new([], []);
}

/// <summary>模组声明的依赖项。</summary>
public sealed record ModDependencyRef(string UniqueId, bool IsRequired, string? MinimumVersion);
