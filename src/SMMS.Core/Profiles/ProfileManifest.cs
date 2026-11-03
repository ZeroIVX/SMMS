namespace SMMS.Core.Profiles;

/// <summary>
/// 配置档清单，对应实例目录下的 <c>profile.json</c>。
/// 这是"清单驱动"的核心：先有它，才能校验完整性、复现环境、导出分享。
/// </summary>
public sealed class ProfileManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsolateGameInstance { get; set; }

    public bool IsolateSaves { get; set; }

    public ProfileRequirements Requirements { get; set; } = new();

    public List<ProfileModEntry> Mods { get; set; } = [];
}

/// <summary>创建该配置档时的环境版本，用于导入到其他机器时提示差异。</summary>
public sealed class ProfileRequirements
{
    public string? GameVersion { get; set; }

    public string? SmapiVersion { get; set; }
}

/// <summary>
/// 清单中的一个模组。以 <see cref="UniqueId"/> 为主键——星露谷玩家可以随意改文件夹名，
/// 但 UniqueID 是稳定标识。
/// </summary>
public sealed class ProfileModEntry
{
    public string UniqueId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string? NexusId { get; set; }

    public string Folder { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>manifest.json 的内容哈希，用于检测模组文件被替换或损坏。</summary>
    public string? ManifestSha256 { get; set; }

    public bool IsContentPack { get; set; }
}
