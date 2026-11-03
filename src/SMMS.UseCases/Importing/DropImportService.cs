using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Archives;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Core.Snapshots;
using SMMS.Core.Versioning;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;
using SMMS.UseCases.Vanity;

namespace SMMS.UseCases.Importing;

[Flags]
public enum DropEffects
{
    None = 0,
    Mods = 1,
    Vanity = 2,
    Profiles = 4
}

/// <summary>一次拖入导入的结果。<see cref="MessageKey"/> 交给表现层做本地化。</summary>
public sealed record DropOutcome(
    bool Success,
    string MessageKey,
    IReadOnlyList<string> Arguments,
    DropEffects Effects,
    IReadOnlyList<string>? Downgrades = null)
{
    public static DropOutcome Fail(string key, params string[] args) => new(false, key, args, DropEffects.None);

    /// <summary>非空表示检测到降级，需要用户确认后才能继续。</summary>
    public bool NeedsDowngradeConfirmation => Downgrades is { Count: > 0 };
}

/// <summary>
/// 拖入压缩包后的分流：
/// 配置档包 → 导入配置档，模组 → 解压进 Mods，XNB 美化包 → 进美化包库。
/// </summary>
public sealed class DropImportService(
    ProfileService profiles,
    GameInstallationService games,
    VanityService vanity,
    ProfilePackService packs,
    ModLibraryService library,
    ModToggleService toggle,
    SnapshotService snapshots,
    ILogger<DropImportService> logger)
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public async Task<DropOutcome> ImportAsync(
        string archivePath,
        bool allowDowngrade = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            return DropOutcome.Fail("drop.error.missing", Path.GetFileName(archivePath));
        }

        var kind = PackageInspector.Inspect(archivePath);
        var activeProfile = profiles.GetActive();
        var game = activeProfile is null ? games.Current : games.ResolveForProfile(activeProfile);

        logger.LogInformation(
            "拖入导入：{Name} 判定为 {Kind}", Path.GetFileName(archivePath), kind);

        return kind switch
        {
            PackageKind.ProfilePack => await ImportProfileAsync(archivePath, cancellationToken).ConfigureAwait(false),
            PackageKind.Mod => ImportMods(archivePath, activeProfile, game, allowDowngrade),
            PackageKind.Vanity => ImportVanity(archivePath, game),
            _ => DropOutcome.Fail("drop.error.unknown", Path.GetFileName(archivePath))
        };
    }

    private async Task<DropOutcome> ImportProfileAsync(string archivePath, CancellationToken cancellationToken)
    {
        var result = await packs.ImportAsync(archivePath, cancellationToken).ConfigureAwait(false);

        if (!result.Success || result.Profile is null)
        {
            return DropOutcome.Fail(result.ErrorKey ?? "profiles.error.invalidPack");
        }

        return new DropOutcome(
            true,
            "drop.success.profile",
            [result.Profile.Name, result.RestoredMods.ToString(CultureInfo.InvariantCulture)],
            DropEffects.Profiles);
    }

    private DropOutcome ImportMods(string archivePath, ModProfile? profile, GameInstallation? game, bool allowDowngrade)
    {
        if (profile is null)
        {
            return DropOutcome.Fail("drop.error.noProfile");
        }

        var staging = CreateStaging();

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);

            var modDirectories = FindModDirectories(staging);
            if (modDirectories.Count == 0)
            {
                return DropOutcome.Fail("drop.error.noMod", Path.GetFileName(archivePath));
            }

            Directory.CreateDirectory(profile.ModsPath);

            // 按 UniqueID 匹配而不是目录名：作者经常改文件夹名，
            // 用目录名判断会把「同一模组的新版本」当成新模组塞进去，变成两份并存。
            var installed = library.Load(profile.ModsPath, toggle.GetDisabledPath(profile)).Mods;
            var byUniqueId = installed
                .GroupBy(mod => mod.UniqueId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // 第一遍只做分类，不落盘——降级需要用户确认，不能先执行一半再问。
            var plan = new List<PendingMod>();
            var skipped = 0;

            foreach (var directory in modDirectories)
            {
                var manifest = TryReadManifest(directory);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.UniqueId))
                {
                    skipped++;
                    continue;
                }

                byUniqueId.TryGetValue(manifest.UniqueId, out var existing);

                // 目录同名但 UniqueID 不同 → 是另一个模组，不能覆盖。
                if (existing is null && Directory.Exists(Path.Combine(profile.ModsPath, Path.GetFileName(directory))))
                {
                    skipped++;
                    continue;
                }

                plan.Add(new PendingMod(directory, manifest, existing));
            }

            if (!allowDowngrade)
            {
                var downgrades = plan
                    .Where(item => IsDowngrade(item.Existing, item.Manifest))
                    .Select(item => $"{item.Existing!.Name}  {item.Existing.Version} → {item.Manifest.Version}")
                    .ToList();

                if (downgrades.Count > 0)
                {
                    logger.LogInformation("检测到降级模组，等待确认：{List}", string.Join("; ", downgrades));
                    return new DropOutcome(
                        false,
                        "drop.confirm.downgrade",
                        downgrades,
                        DropEffects.None,
                        downgrades);
                }
            }

            // 更新前先留一份完整快照：覆盖式更新改坏了，整批模组都能退回去。
            // 只在确实有更新时创建，纯新增不占用空间。
            if (plan.Any(item => item.Existing is not null))
            {
                var snapshot = snapshots.Create(profile, "更新模组前", SnapshotKind.PreUpdate);

                if (!snapshot.Success)
                {
                    logger.LogWarning("更新前快照失败：{Error}，中止本次更新", snapshot.ErrorKey);
                    return DropOutcome.Fail(snapshot.ErrorKey ?? "snapshots.error.createFailed");
                }

                snapshots.Prune(profile.Id);
            }

            var backupRoot = Path.Combine(
                game?.WorkspacePath ?? Path.GetDirectoryName(profile.ModsPath)!,
                "mod-backup",
                // 毫秒级时间戳：秒级在同一秒内连续拖两次更新会撞名，第二次备份被跳过。
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));

            var added = 0;
            var updated = 0;

            foreach (var item in plan)
            {
                if (item.Existing is null)
                {
                    CopyDirectory(item.Directory, Path.Combine(profile.ModsPath, Path.GetFileName(item.Directory)));
                    added++;
                    continue;
                }

                UpdateMod(item.Existing, item.Directory, backupRoot);
                updated++;
            }

            logger.LogInformation(
                "拖入导入模组：新增 {Added}，更新 {Updated}，跳过 {Skipped}", added, updated, skipped);

            if (updated > 0)
            {
                // 每次更新都会留一份旧版本，不淘汰的话会无限增长。
                PruneBackups(backupRoot);
            }

            return new DropOutcome(
                true,
                "drop.success.modsMixed",
                [
                    added.ToString(CultureInfo.InvariantCulture),
                    updated.ToString(CultureInfo.InvariantCulture),
                    skipped.ToString(CultureInfo.InvariantCulture)
                ],
                DropEffects.Mods);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "拖入导入模组失败：{Path}", archivePath);
            return DropOutcome.Fail("drop.error.extractFailed");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private static bool IsDowngrade(InstalledMod? existing, ModManifest manifest)
    {
        if (existing?.Version is null)
        {
            return false;
        }

        return SemanticVersion.TryParse(manifest.Version, out var incoming)
               && incoming is not null
               && incoming.CompareTo(existing.Version) < 0;
    }

    private sealed record PendingMod(string Directory, ModManifest Manifest, InstalledMod? Existing);

    /// <summary>
    /// 只保留最近几次更新的旧版本备份。目录名是时间戳，字符串倒序即时间倒序。
    /// </summary>
    private void PruneBackups(string currentBackupRoot, int keep = 5)
    {
        try
        {
            var parent = Path.GetDirectoryName(currentBackupRoot);
            if (parent is null || !Directory.Exists(parent))
            {
                return;
            }

            var stale = Directory
                .GetDirectories(parent)
                .OrderByDescending(directory => Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)
                .Skip(keep)
                .ToList();

            foreach (var directory in stale)
            {
                Directory.Delete(directory, recursive: true);
                logger.LogInformation("已清理过期备份：{Directory}", Path.GetFileName(directory));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响导入结果，下次更新再试。
            logger.LogWarning(ex, "清理旧备份失败");
        }
    }

    /// <summary>
    /// 覆盖已安装的模组：先整目录备份，再复制到临时目录，成功后原子替换，最后把玩家自己的 config.json 还回去。
    /// 配置文件就躺在模组目录里，直接覆盖等于把玩家的键位和开关全清掉。
    /// </summary>
    private static void UpdateMod(InstalledMod existing, string source, string backupRoot)
    {
        var target = existing.DirectoryPath;

        var backup = Path.Combine(backupRoot, Path.GetFileName(target));
        if (!Directory.Exists(backup))
        {
            CopyDirectory(target, backup);
        }

        var configPath = Path.Combine(target, "config.json");
        var config = File.Exists(configPath) ? File.ReadAllText(configPath) : null;

        // 先复制到同目录下的临时目录，成功后才替换原目录。
        // 否则复制中途失败（磁盘满、权限）会留下一个被清空的损坏模组。
        var temp = target + ".smms-updating-" + Guid.NewGuid().ToString("N");
        CopyDirectory(source, temp);

        // 新版本自带配置就尊重新版本的默认值，否则还原旧配置。
        if (config is not null && !File.Exists(Path.Combine(temp, "config.json")))
        {
            File.WriteAllText(Path.Combine(temp, "config.json"), config);
        }

        Directory.Delete(target, recursive: true);
        Directory.Move(temp, target);
    }

    private static ModManifest? TryReadManifest(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), ManifestJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private DropOutcome ImportVanity(string archivePath, GameInstallation? game)
    {
        if (game is null)
        {
            return DropOutcome.Fail("drop.error.noGame");
        }

        var result = vanity.Import(game, archivePath);

        if (!result.Success || result.Package is null)
        {
            return DropOutcome.Fail(result.ErrorKey ?? "vanity.error.invalidArchive");
        }

        return new DropOutcome(
            true,
            "drop.success.vanity",
            [result.Package.Name, result.FileCount.ToString(CultureInfo.InvariantCulture)],
            DropEffects.Vanity);
    }

    /// <summary>找出所有模组根目录：含 manifest.json，且父目录不含（避免嵌套重复计数）。</summary>
    private static List<string> FindModDirectories(string root)
    {
        var candidates = Directory
            .EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .ToList();

        return candidates
            .Where(directory => !candidates.Any(other =>
                !string.Equals(other, directory, StringComparison.OrdinalIgnoreCase)
                && directory.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string CreateStaging()
    {
        var path = Path.Combine(Path.GetTempPath(), "smms-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
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
            // 临时目录清理失败不影响导入结果。
        }
    }
}
