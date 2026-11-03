using System.Text.Json.Serialization;

namespace SMMS.Core.Mods;

/// <summary>SMAPI <c>manifest.json</c> 的映射，字段名遵循 SMAPI 规范（注意 <c>UniqueID</c> 的大小写）。</summary>
public sealed class ModManifest
{
    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Author")]
    public string? Author { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    [JsonPropertyName("Description")]
    public string? Description { get; set; }

    [JsonPropertyName("UniqueID")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("EntryDll")]
    public string? EntryDll { get; set; }

    [JsonPropertyName("MinimumApiVersion")]
    public string? MinimumApiVersion { get; set; }

    [JsonPropertyName("UpdateKeys")]
    public List<string>? UpdateKeys { get; set; }

    [JsonPropertyName("ContentPackFor")]
    public ManifestReference? ContentPackFor { get; set; }

    [JsonPropertyName("Dependencies")]
    public List<ManifestDependency>? Dependencies { get; set; }
}

public sealed class ManifestReference
{
    [JsonPropertyName("UniqueID")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("MinimumVersion")]
    public string? MinimumVersion { get; set; }
}

public sealed class ManifestDependency
{
    [JsonPropertyName("UniqueID")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("IsRequired")]
    public bool IsRequired { get; set; } = true;

    [JsonPropertyName("MinimumVersion")]
    public string? MinimumVersion { get; set; }
}
