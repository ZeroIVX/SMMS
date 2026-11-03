namespace SMMS.Core.Profiles;

/// <summary>
/// 模组配置档。只隔离模组集合，不隔离 SMAPI 版本——一个游戏目录只有一份 SMAPI。
/// </summary>
public sealed record ModProfile
{
    public const string DefaultId = "default";

    public required string Id { get; init; }

    /// <summary>用户可见名称。内置档为空，由界面按 Id 翻译。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>该配置档对应的模组目录绝对路径。</summary>
    public required string ModsPath { get; init; }

    public string? Description { get; init; }

    public bool IsBuiltIn { get; init; }

    public ProfileIsolation Isolation { get; init; } = ProfileIsolation.ModsOnly;

    /// <summary>
    /// 完整实例模式下的游戏副本根目录。
    /// 非空时该配置档用副本启动，与主安装的游戏版本互不影响。
    /// </summary>
    public string? InstanceRootPath { get; init; }

    public bool HasOwnInstance => !string.IsNullOrWhiteSpace(InstanceRootPath);

    /// <summary>
    /// 实例目录是否为玩家已有的独立备份（而非 SMMS 复制的副本）。
    /// 这类目录只被引用，删除配置档时不得动其文件。
    /// </summary>
    public bool IsExternal => HasOwnInstance && !InstanceRootPath!.Contains(
        $"{Path.DirectorySeparatorChar}profiles{Path.DirectorySeparatorChar}",
        StringComparison.OrdinalIgnoreCase);

    public static ModProfile CreateDefault(string gameRootPath) => new()
    {
        Id = DefaultId,
        ModsPath = Path.Combine(gameRootPath, "Mods"),
        IsBuiltIn = true
    };
}
