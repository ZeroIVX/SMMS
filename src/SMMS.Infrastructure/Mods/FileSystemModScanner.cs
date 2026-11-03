using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Versioning;

namespace SMMS.Infrastructure.Mods;

/// <summary>
/// 遍历模组目录读取 <c>manifest.json</c>。单个模组解析失败只记警告并跳过，
/// 不影响其余模组的识别——玩家目录里损坏的 manifest 是常见现象。
/// </summary>
internal sealed class FileSystemModScanner(ILogger<FileSystemModScanner> logger) : IModScanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ModScanResult Scan(string modsPath, string? disabledPath = null)
    {
        var mods = new List<InstalledMod>();
        var warnings = new List<string>();

        ScanDirectory(modsPath, isEnabled: true, mods, warnings);

        if (!string.IsNullOrWhiteSpace(disabledPath) && Directory.Exists(disabledPath))
        {
            ScanDirectory(disabledPath, isEnabled: false, mods, warnings);
        }

        logger.LogInformation(
            "扫描模组目录 {Path}：识别 {Count} 个模组（禁用 {Disabled} 个），跳过 {Skipped} 个",
            modsPath, mods.Count, mods.Count(mod => !mod.IsEnabled), warnings.Count);

        return new ModScanResult(mods, warnings);
    }

    private static void ScanDirectory(
        string directory,
        bool isEnabled,
        List<InstalledMod> mods,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            var folderName = Path.GetFileName(subDirectory);

            // 以点开头视为隐藏目录，不参与扫描。
            if (folderName.StartsWith('.'))
            {
                continue;
            }

            var manifestPath = Path.Combine(subDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                warnings.Add($"缺少 manifest.json：{folderName}");
                continue;
            }

            var mod = TryReadManifest(manifestPath, subDirectory, folderName, isEnabled);
            if (mod is not null)
            {
                mods.Add(mod);
            }
            else
            {
                warnings.Add($"manifest.json 解析失败：{folderName}");
            }
        }
    }

    private static InstalledMod? TryReadManifest(
        string manifestPath,
        string directory,
        string folderName,
        bool isEnabled)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOptions);

            if (manifest is null)
            {
                return null;
            }

            return new InstalledMod
            {
                DirectoryPath = directory,
                FolderName = folderName,
                IsEnabled = isEnabled,
                UniqueId = string.IsNullOrWhiteSpace(manifest.UniqueId) ? folderName : manifest.UniqueId.Trim(),
                Name = string.IsNullOrWhiteSpace(manifest.Name) ? folderName : manifest.Name.Trim(),
                Author = manifest.Author?.Trim() ?? string.Empty,
                Version = ParseVersion(manifest.Version),
                Description = string.IsNullOrWhiteSpace(manifest.Description) ? null : manifest.Description.Trim(),
                MinimumApiVersion = ParseVersion(manifest.MinimumApiVersion),
                UpdateKeys = manifest.UpdateKeys ?? [],
                Dependencies = (manifest.Dependencies ?? [])
                    .Where(dependency => !string.IsNullOrWhiteSpace(dependency.UniqueId))
                    .Select(dependency => new ModDependencyRef(
                        dependency.UniqueId!.Trim(),
                        dependency.IsRequired,
                        dependency.MinimumVersion))
                    .ToList(),
                NexusId = ExtractNexusId(manifest.UpdateKeys),
                IsContentPack = manifest.ContentPackFor is not null,
                ContentPackFor = manifest.ContentPackFor?.UniqueId,
                HasChineseTranslation = HasChineseI18n(directory)
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断模组是否自带中文翻译。SMAPI 约定把翻译放在模组目录的 i18n/ 下，
    /// 文件名以 zh 开头（zh.json、zh-CN.json 等）。
    /// </summary>
    private static bool HasChineseI18n(string modDirectory)
    {
        try
        {
            var i18n = Path.Combine(modDirectory, "i18n");
            if (!Directory.Exists(i18n))
            {
                return false;
            }

            return Directory.EnumerateFiles(i18n, "zh*.json").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static SemanticVersion? ParseVersion(string? value)        => SemanticVersion.TryParse(value, out var version) ? version : null;

    private static string? ExtractNexusId(IReadOnlyList<string>? updateKeys)
    {
        if (updateKeys is null)
        {
            return null;
        }

        const string prefix = "Nexus:";
        foreach (var key in updateKeys)
        {
            if (key is not null && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var id = key[prefix.Length..].Trim();
                return id.Length > 0 ? id : null;
            }
        }

        return null;
    }
}
