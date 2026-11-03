using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Profiles;

public sealed record PackExportResult(bool Success, string? ErrorKey, int ModCount, long SizeBytes)
{
    public static PackExportResult Fail(string errorKey) => new(false, errorKey, 0, 0);
}

public sealed record PackImportResult(
    bool Success,
    string? ErrorKey,
    ModProfile? Profile,
    IReadOnlyList<string> MissingMods,
    int RestoredMods)
{
    public static PackImportResult Fail(string errorKey) => new(false, errorKey, null, [], 0);
}

/// <summary>
/// 配置档导出 / 导入。默认只导出清单，打包模组需要显式开启——
/// 模组版权属于作者，二次分发须取得授权，产品不该默认引导。
/// </summary>
public sealed class ProfilePackService(
    GameInstallationService games,
    ProfileService profiles,
    IModScanner scanner,
    ILogger<ProfilePackService> logger)
{
    private const string ManifestEntry = "profile.json";
    private const string ReadmeEntry = "README.md";
    private const string ModsFolder = "mods";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<PackExportResult> ExportAsync(
        ModProfile profile,
        bool includeMods,
        string targetZipPath,
        CancellationToken cancellationToken = default)
    {
        var game = games.Current ?? games.Resolve();
        if (game is null)
        {
            return PackExportResult.Fail("profiles.error.noGame");
        }

        var scan = scanner.Scan(profile.ModsPath, TryGetDisabledPath(profile));
        var manifest = BuildManifest(profile, game, scan);

        try
        {
            await Task.Run(
                () => WriteArchive(profile, manifest, scan.Mods, includeMods, targetZipPath, cancellationToken),
                cancellationToken);

            var size = new FileInfo(targetZipPath).Length;
            logger.LogInformation(
                "已导出配置档 {Name}：{Count} 个模组，包含文件={IncludeMods}，{Size} 字节",
                profile.Name, manifest.Mods.Count, includeMods, size);

            return new PackExportResult(true, null, manifest.Mods.Count, size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "导出配置档失败：{Path}", targetZipPath);
            return PackExportResult.Fail("profiles.error.exportFailed");
        }
    }

    public async Task<PackImportResult> ImportAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "smms-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempRoot, overwriteFiles: true), cancellationToken);

            var manifestPath = Path.Combine(tempRoot, ManifestEntry);
            if (!File.Exists(manifestPath))
            {
                return PackImportResult.Fail("profiles.error.invalidPack");
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = JsonSerializer.Deserialize<ProfileManifest>(json, JsonOptions);

            if (manifest is null || manifest.SchemaVersion != ProfileManifest.CurrentSchemaVersion)
            {
                return PackImportResult.Fail("profiles.error.invalidPack");
            }

            var name = string.IsNullOrWhiteSpace(manifest.Name) ? "Imported" : manifest.Name;
            var created = await profiles.CreateAsync(name, copyFromActive: false, ProfileIsolation.ModsOnly, cancellationToken);

            if (created.Profile is null)
            {
                return PackImportResult.Fail(created.ErrorKey ?? "profiles.error.failed");
            }

            var restored = await RestoreModsAsync(tempRoot, created.Profile, manifest, cancellationToken);
            var missing = FindMissing(created.Profile, manifest);

            logger.LogInformation(
                "导入配置档 {Name}：还原 {Restored} 个模组，缺失 {Missing} 个",
                name, restored, missing.Count);

            return new PackImportResult(true, null, created.Profile, missing, restored);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            logger.LogError(ex, "导入配置档失败：{Path}", zipPath);
            return PackImportResult.Fail("profiles.error.invalidPack");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static ProfileManifest BuildManifest(ModProfile profile, GameInstallation game, ModScanResult scan)
    {
        return new ProfileManifest
        {
            SchemaVersion = ProfileManifest.CurrentSchemaVersion,
            Id = profile.Id,
            Name = profile.Name,
            CreatedAt = DateTimeOffset.Now,
            IsolateGameInstance = profile.Isolation.IsolateGameInstance,
            IsolateSaves = profile.Isolation.IsolateSaves,
            Requirements = new ProfileRequirements
            {
                GameVersion = game.GameVersion,
                SmapiVersion = game.SmapiVersion
            },
            Mods = scan.Mods
                .Select(mod => new ProfileModEntry
                {
                    UniqueId = mod.UniqueId,
                    Name = mod.Name,
                    Version = mod.Version?.ToString() ?? string.Empty,
                    NexusId = mod.NexusId,
                    Folder = mod.FolderName,
                    Enabled = mod.IsEnabled,
                    IsContentPack = mod.IsContentPack
                })
                .ToList()
        };
    }

    private static void WriteArchive(
        ModProfile profile,
        ProfileManifest manifest,
        IReadOnlyList<InstalledMod> mods,
        bool includeMods,
        string targetZipPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(targetZipPath))
        {
            File.Delete(targetZipPath);
        }

        using var stream = File.Create(targetZipPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        AddTextEntry(archive, ManifestEntry, JsonSerializer.Serialize(manifest, JsonOptions));
        AddTextEntry(archive, ReadmeEntry, BuildReadme(profile, manifest, includeMods));

        if (!includeMods)
        {
            return;
        }

        foreach (var mod in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(mod.DirectoryPath))
            {
                continue;
            }

            AddDirectory(archive, mod.DirectoryPath, $"{ModsFolder}/{mod.FolderName}", cancellationToken);
        }
    }

    private static string BuildReadme(ModProfile profile, ProfileManifest manifest, bool includeMods)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var builder = new StringBuilder();

        builder.AppendLine(culture, $"# {profile.Name}");
        builder.AppendLine();
        builder.AppendLine(culture, $"- 游戏版本要求：{manifest.Requirements.GameVersion ?? "未记录"}");
        builder.AppendLine(culture, $"- SMAPI 版本要求：{manifest.Requirements.SmapiVersion ?? "未记录"}");
        builder.AppendLine(culture, $"- 模组数量：{manifest.Mods.Count}");
        builder.AppendLine(culture, $"- 包含模组文件：{(includeMods ? "是" : "否")}");
        builder.AppendLine();
        builder.AppendLine("## 模组清单");
        builder.AppendLine();

        foreach (var mod in manifest.Mods)
        {
            var nexus = string.IsNullOrWhiteSpace(mod.NexusId)
                ? string.Empty
                : $" · https://www.nexusmods.com/stardewvalley/mods/{mod.NexusId}";
            var state = mod.Enabled ? string.Empty : "（禁用）";
            builder.AppendLine(culture, $"- {mod.Name} {mod.Version}{state} — `{mod.UniqueId}`{nexus}");
        }

        builder.AppendLine();
        builder.AppendLine("> 由 SMMS 生成。模组版权归各自作者所有。");

        return builder.ToString();
    }

    private static async Task<int> RestoreModsAsync(
        string tempRoot,
        ModProfile profile,
        ProfileManifest manifest,
        CancellationToken cancellationToken)
    {
        var packMods = Path.Combine(tempRoot, ModsFolder);
        if (!Directory.Exists(packMods))
        {
            return 0;
        }

        var disabledTarget = Path.Combine(
            Path.GetDirectoryName(profile.ModsPath) ?? profile.ModsPath,
            "_disabled");

        var restored = 0;

        foreach (var entry in manifest.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = Path.Combine(packMods, entry.Folder);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = entry.Enabled
                ? Path.Combine(profile.ModsPath, entry.Folder)
                : Path.Combine(disabledTarget, entry.Folder);

            if (Directory.Exists(target))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await Task.Run(() => CopyDirectory(source, target, cancellationToken), cancellationToken);
            restored++;
        }

        return restored;
    }

    private List<string> FindMissing(ModProfile profile, ProfileManifest manifest)
    {
        var scan = scanner.Scan(profile.ModsPath, TryGetDisabledPath(profile));
        var installed = scan.Mods
            .Select(mod => mod.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return manifest.Mods
            .Where(mod => !installed.Contains(mod.UniqueId))
            .Select(mod => string.IsNullOrWhiteSpace(mod.NexusId)
                ? $"{mod.Name} ({mod.UniqueId})"
                : $"{mod.Name} ({mod.UniqueId}) — https://www.nexusmods.com/stardewvalley/mods/{mod.NexusId}")
            .ToList();
    }

    private string? TryGetDisabledPath(ModProfile profile)
    {
        if (profile.IsBuiltIn)
        {
            var game = games.Current ?? games.Resolve();
            return game is null ? null : Path.Combine(game.WorkspacePath, "disabled");
        }

        var parent = Path.GetDirectoryName(profile.ModsPath);
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "_disabled");
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddDirectory(ZipArchive archive, string source, string entryPrefix, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var entry = archive.CreateEntry($"{entryPrefix}/{relative}", CompressionLevel.Optimal);

            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            fileStream.CopyTo(entryStream);
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响主流程。
        }
    }
}
