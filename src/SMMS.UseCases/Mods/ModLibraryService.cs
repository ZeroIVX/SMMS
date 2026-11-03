using SMMS.Core.Mods;
using SMMS.Core.Ports;

namespace SMMS.UseCases.Mods;

/// <summary>一次模组库读取的快照，供界面直接消费。</summary>
public sealed record ModLibrarySnapshot(
    IReadOnlyList<InstalledMod> Mods,
    IReadOnlyList<string> Warnings,
    string? ModsPath)
{
    public static ModLibrarySnapshot Empty { get; } = new([], [], null);

    public int CodeModCount => Mods.Count(mod => mod.IsCodeMod);

    public int ContentPackCount => Mods.Count(mod => mod.IsContentPack);
}

public sealed class ModLibraryService(IModScanner scanner)
{
    public ModLibrarySnapshot Load(string? modsPath, string? disabledPath = null)
    {
        if (string.IsNullOrWhiteSpace(modsPath))
        {
            return ModLibrarySnapshot.Empty;
        }

        var result = scanner.Scan(modsPath, disabledPath);

        // 按名称排序，保证每次展示顺序稳定，避免玩家在列表里找不到刚才的模组。
        var ordered = result.Mods
            .OrderBy(mod => mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new ModLibrarySnapshot(ordered, result.Warnings, modsPath);
    }
}
