using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SMMS.Core.Configuration;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Profiles;

public sealed record ProfileCreateResult(ModProfile? Profile, string? ErrorKey)
{
    public static ProfileCreateResult Ok(ModProfile profile) => new(profile, null);

    public static ProfileCreateResult Fail(string errorKey) => new(null, errorKey);
}

public sealed record ProfileActionResult(bool Success, string? ErrorKey)
{
    public static ProfileActionResult Ok() => new(true, null);

    public static ProfileActionResult Fail(string errorKey) => new(false, errorKey);
}

/// <summary>
/// 配置档管理。内置默认档直指游戏目录下的 Mods，自建档落在 <c>_smms/profiles/&lt;id&gt;/Mods</c>。
/// </summary>
public sealed class ProfileService
{
    private readonly GameInstallationService _games;
    private readonly IModScanner _scanner;
    private readonly IProfileManifestStore _manifests;
    private readonly IGameLocator _locator;
    private readonly ILogger<ProfileService> _logger;

    public ProfileService(
        GameInstallationService games,
        IModScanner scanner,
        IProfileManifestStore manifests,
        IGameLocator locator,
        ILogger<ProfileService> logger)
    {
        _games = games;
        _scanner = scanner;
        _manifests = manifests;
        _locator = locator;
        _logger = logger;
    }

    /// <summary>
    /// 内置「纯净档（原版）」的固定 Id。它是一个真正的配置档（独立空 Mods 目录），
    /// 与「默认」档的区别是启动时走原版（LaunchMode.Vanilla），不加载任何 SMAPI 模组。
    /// </summary>
    public const string VanillaProfileId = "vanilla";

    /// <summary>配置档列表或激活项发生变化。</summary>
    public event EventHandler? ProfilesChanged;

    /// <summary>取内置纯净档（原版）。尚未创建时返回 <c>null</c>。</summary>
    public ModProfile? GetVanillaProfile()
        => GetAll().FirstOrDefault(profile => string.Equals(profile.Id, VanillaProfileId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 幂等创建内置纯净档：独立的空 Mods 目录，不复制任何模组。
    /// 已存在则直接返回原对象，不重复写设置，也不改动其它配置档。
    /// </summary>
    /// <param name="game">目标安装；为空时自行解析，解析不到返回 <c>null</c>。</param>
    /// <param name="displayName">持久化名称；建议留空，由界面按 Id 本地化显示。</param>
    public ModProfile? EnsureVanillaProfile(GameInstallation? game = null, string? displayName = null)
    {
        var installation = game ?? _games.Current ?? _games.Resolve();
        if (installation is null)
        {
            return null;
        }

        if (GetVanillaProfile() is { } existing)
        {
            return existing;
        }

        var profileRoot = GetProfileRoot(installation, VanillaProfileId);
        var modsPath = Path.Combine(profileRoot, "Mods");

        try
        {
            Directory.CreateDirectory(modsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "创建纯净档目录失败：{Path}", modsPath);
            return null;
        }

        _games.Settings.ProfileRecords.Add(new ProfileRecord
        {
            Id = VanillaProfileId,
            Name = displayName ?? string.Empty,
            ModsPath = null,
            IsolateGameInstance = false,
            IsolateSaves = false
        });
        _games.SaveSettings();

        _logger.LogInformation("已创建内置纯净档（原版）：{Path}", modsPath);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return new ModProfile
        {
            Id = VanillaProfileId,
            Name = displayName ?? string.Empty,
            ModsPath = modsPath,
            IsBuiltIn = true,
            Isolation = ProfileIsolation.ModsOnly
        };
    }

    public IReadOnlyList<ModProfile> GetAll()
    {
        var game = _games.Current ?? _games.Resolve();
        if (game is null)
        {
            return [];
        }

        var profiles = new List<ModProfile> { ModProfile.CreateDefault(game.RootPath) };

        foreach (var record in _games.Settings.ProfileRecords)
        {
            if (string.IsNullOrWhiteSpace(record.Id)
                || string.Equals(record.Id, ModProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            profiles.Add(new ModProfile
            {
                Id = record.Id,
                Name = record.Name,
                ModsPath = ResolveModsPath(game, record),
                InstanceRootPath = ResolveInstanceRoot(game, record),
                // 纯净档同样是内置档：不可改名/删除，禁用区放在工作区里。
                IsBuiltIn = string.Equals(record.Id, VanillaProfileId, StringComparison.OrdinalIgnoreCase),
                Isolation = new ProfileIsolation
                {
                    IsolateGameInstance = record.IsolateGameInstance,
                    IsolateSaves = record.IsolateSaves
                }
            });
        }

        // 内置档固定前两位（原版 → 默认），其余按玩家手动排的顺序；没排过的追加在后面。
        var order = _games.Settings.ProfileOrder;

        return profiles
            .OrderBy(profile => profile.Id switch
            {
                VanillaProfileId => 0,
                ModProfile.DefaultId => 1,
                _ => 2
            })
            .ThenBy(profile =>
            {
                var index = order.FindIndex(id => string.Equals(id, profile.Id, StringComparison.OrdinalIgnoreCase));
                return index >= 0 ? index : int.MaxValue;
            })
            .ToList();
    }

    /// <summary>
    /// 手动调整配置档顺序。delta = -1 上移、+1 下移。内置档不参与（位置由语义决定）。
    /// </summary>
    public bool MoveProfile(string id, int delta)
    {
        var movable = GetAll()
            .Where(profile => !profile.IsBuiltIn)
            .Select(profile => profile.Id)
            .ToList();

        var index = movable.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        var target = index + delta;

        if (index < 0 || target < 0 || target >= movable.Count)
        {
            return false;
        }

        (movable[index], movable[target]) = (movable[target], movable[index]);

        _games.Settings.ProfileOrder = movable;
        _games.SaveSettings();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public ModProfile? GetActive()
    {
        var profiles = GetAll();
        if (profiles.Count == 0)
        {
            return null;
        }

        var activeId = _games.Settings.ActiveProfileId;
        return profiles.FirstOrDefault(profile => profile.Id == activeId) ?? profiles[0];
    }

    public bool SetActive(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        var exists = GetAll().Any(profile => profile.Id == profileId);
        if (!exists)
        {
            return false;
        }

        if (_games.Settings.ActiveProfileId == profileId)
        {
            return true;
        }

        _games.Settings.ActiveProfileId = profileId;
        _games.SaveSettings();
        _logger.LogInformation("切换配置档：{ProfileId}", profileId);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<ProfileCreateResult> CreateAsync(
        string name,
        bool copyFromActive,
        ProfileIsolation? isolation = null,
        CancellationToken cancellationToken = default)
    {
        var game = _games.Current ?? _games.Resolve();
        if (game is null)
        {
            return ProfileCreateResult.Fail("profiles.error.noGame");
        }

        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ProfileCreateResult.Fail("profiles.error.emptyName");
        }

        if (GetAll().Any(profile => string.Equals(profile.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileCreateResult.Fail("profiles.error.duplicate");
        }

        var options = isolation ?? ProfileIsolation.ModsOnly;
        var id = $"p{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..20];
        var profileRoot = GetProfileRoot(game, id);
        var instanceRoot = Path.Combine(profileRoot, "instance");

        var modsPath = options.IsolateGameInstance
            ? Path.Combine(instanceRoot, "Mods")
            : Path.Combine(profileRoot, "Mods");

        try
        {
            if (options.IsolateGameInstance)
            {
                // 完整实例：复制游戏本体（跳过 Mods 与工作区），副本自带一份 SMAPI。
                await Task.Run(() => CopyGameDirectory(game.RootPath, instanceRoot, cancellationToken), cancellationToken);
            }

            Directory.CreateDirectory(modsPath);

            if (copyFromActive && GetActive() is { } active && Directory.Exists(active.ModsPath))
            {
                // 不加 ConfigureAwait(false)：后续要更新集合与触发事件，必须在原上下文继续。
                await Task.Run(() => CopyDirectory(active.ModsPath, modsPath, cancellationToken), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogError(ex, "创建配置档失败：{Name}", trimmed);

            // 回滚整个配置档目录，包含可能复制了一半的游戏副本。
            TryDeleteDirectory(profileRoot);

            return ProfileCreateResult.Fail(
                options.IsolateGameInstance ? "profiles.error.instanceCopyFailed" : "profiles.error.copyFailed");
        }

        _games.Settings.ProfileRecords.Add(new ProfileRecord
        {
            Id = id,
            Name = trimmed,
            ModsPath = null,
            IsolateGameInstance = options.IsolateGameInstance,
            IsolateSaves = options.IsolateSaves
        });
        _games.Settings.ActiveProfileId = id;
        _games.SaveSettings();

        var profile = new ModProfile
        {
            Id = id,
            Name = trimmed,
            ModsPath = modsPath,
            InstanceRootPath = options.IsolateGameInstance ? instanceRoot : null,
            Isolation = options
        };

        // 清单是配置档自描述的基础，创建后立即生成一份。
        SyncManifest(profile, game);

        _logger.LogInformation(
            "已创建配置档 {Name}（{Id}），隔离={Isolation}，复制现有模组={Copy}",
            trimmed, id, options.PresetKey, copyFromActive);

        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return ProfileCreateResult.Ok(profile);
    }

    /// <summary>
    /// 一键复制配置档：真实复制源档的 Mods 目录与禁用区（不共享写权限），
    /// 新档永远是可启动的独立 Mods 档；复制后自动切换为当前档。
    /// </summary>
    public async Task<ProfileCreateResult> CopyAsync(
        string sourceProfileId,
        string newName,
        CancellationToken cancellationToken = default)
    {
        var game = _games.Current ?? _games.Resolve();
        if (game is null)
        {
            return ProfileCreateResult.Fail("profiles.error.noGame");
        }

        var source = GetAll()
            .FirstOrDefault(profile => string.Equals(profile.Id, sourceProfileId, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            return ProfileCreateResult.Fail("profiles.error.notFound");
        }

        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ProfileCreateResult.Fail("profiles.error.emptyName");
        }

        if (GetAll().Any(profile => string.Equals(profile.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileCreateResult.Fail("profiles.error.duplicate");
        }

        var id = $"p{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..20];
        var profileRoot = GetProfileRoot(game, id);
        var modsPath = Path.Combine(profileRoot, "Mods");

        try
        {
            Directory.CreateDirectory(modsPath);

            if (Directory.Exists(source.ModsPath))
            {
                await Task.Run(() => CopyDirectory(source.ModsPath, modsPath, cancellationToken), cancellationToken);
            }

            // 内置档的禁用区在共享工作区里，不属于它私有，复制过去会污染新档，直接跳过。
            var sourceDisabled = source.IsBuiltIn ? null : GetDisabledPath(source, game);
            if (!string.IsNullOrEmpty(sourceDisabled) && Directory.Exists(sourceDisabled))
            {
                var disabledTarget = Path.Combine(profileRoot, "_disabled");
                await Task.Run(
                    () => CopyDirectory(sourceDisabled, disabledTarget, cancellationToken),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogError(ex, "复制配置档失败：{Source} → {Name}", sourceProfileId, trimmed);
            TryDeleteDirectory(profileRoot);
            return ProfileCreateResult.Fail("profiles.error.copyFailed");
        }

        _games.Settings.ProfileRecords.Add(new ProfileRecord
        {
            Id = id,
            Name = trimmed,
            ModsPath = null,
            IsolateGameInstance = false,
            IsolateSaves = source.Isolation.IsolateSaves
        });
        _games.Settings.ActiveProfileId = id;
        _games.SaveSettings();

        var profile = new ModProfile
        {
            Id = id,
            Name = trimmed,
            ModsPath = modsPath,
            Isolation = new ProfileIsolation { IsolateSaves = source.Isolation.IsolateSaves }
        };

        SyncManifest(profile, game);

        _logger.LogInformation("已复制配置档：{Source} → {Name}（{Id}）", source.Name, trimmed, id);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return ProfileCreateResult.Ok(profile);
    }

    /// <summary>
    /// 纳入一个已存在的游戏目录（玩家自己备份的独立整合包）。
    /// 不做任何复制或移动——只建立一个指向它的配置档，秒级完成。
    /// </summary>
    public ProfileCreateResult AdoptExternal(string name, string gameRootPath)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ProfileCreateResult.Fail("profiles.error.emptyName");
        }

        var game = _games.Current ?? _games.Resolve();
        if (game is null)
        {
            return ProfileCreateResult.Fail("profiles.error.noGame");
        }

        if (string.IsNullOrWhiteSpace(gameRootPath) || !Directory.Exists(gameRootPath))
        {
            return ProfileCreateResult.Fail("profiles.error.invalidGameDir");
        }

        var normalized = Path.GetFullPath(gameRootPath).TrimEnd(Path.DirectorySeparatorChar);

        // 同一目录重复纳管会让两个配置档互相打架，先挡住。
        var alreadyAdopted = GetAll().Any(profile =>
            profile.InstanceRootPath is { } root
            && string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), normalized, StringComparison.OrdinalIgnoreCase));

        if (alreadyAdopted)
        {
            return ProfileCreateResult.Fail("profiles.error.alreadyAdopted");
        }

        if (!_locator.TryInspect(normalized, out var installation) || installation is null)
        {
            return ProfileCreateResult.Fail("profiles.error.invalidGameDir");
        }

        if (GetAll().Any(profile => string.Equals(profile.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileCreateResult.Fail("profiles.error.duplicate");
        }

        var id = $"p{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..20];
        var profileRoot = GetProfileRoot(game, id);

        try
        {
            // 只建清单目录，不碰外部游戏目录。
            Directory.CreateDirectory(profileRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "创建配置档清单目录失败：{Root}", profileRoot);
            return ProfileCreateResult.Fail("profiles.error.failed");
        }

        _games.Settings.ProfileRecords.Add(new ProfileRecord
        {
            Id = id,
            Name = trimmed,
            IsolateGameInstance = true,
            InstanceRootPath = normalized
        });
        _games.Settings.ActiveProfileId = id;
        _games.SaveSettings();

        var profile = new ModProfile
        {
            Id = id,
            Name = trimmed,
            ModsPath = Path.Combine(normalized, "Mods"),
            InstanceRootPath = normalized,
            Isolation = ProfileIsolation.FullInstance
        };

        SyncManifest(profile, installation);

        _logger.LogInformation("已纳入已有游戏目录：{Name} → {Path}", trimmed, normalized);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return ProfileCreateResult.Ok(profile);
    }

    /// <summary>
    /// 删除自建配置档。只删除 SMMS 工作区内的目录——若记录指向玩家自选的外部路径，
    /// 仅移除记录，绝不递归删除非托管目录。
    /// </summary>
    public ProfileActionResult Delete(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ProfileActionResult.Fail("profiles.error.notFound");
        }

        if (string.Equals(profileId, ModProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return ProfileActionResult.Fail("profiles.error.builtin");
        }

        var profile = GetAll().FirstOrDefault(item => item.Id == profileId);
        var record = _games.Settings.ProfileRecords.FirstOrDefault(item => item.Id == profileId);

        if (profile is null || record is null)
        {
            return ProfileActionResult.Fail("profiles.error.notFound");
        }

        var game = _games.Current ?? _games.Resolve();
        var profileRoot = game is null ? null : GetProfileRoot(game, profileId);

        if (!string.IsNullOrEmpty(profileRoot) && IsManagedDirectory(profileRoot))
        {
            try
            {
                if (Directory.Exists(profileRoot))
                {
                    Directory.Delete(profileRoot, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "删除配置档目录失败：{Root}", profileRoot);
                return ProfileActionResult.Fail("profiles.error.deleteFailed");
            }
        }
        else
        {
            // 外部路径：只解绑，不动文件。
            _logger.LogWarning("配置档不在工作区内，仅移除记录：{Root}", profileRoot);
        }

        _games.Settings.ProfileRecords.Remove(record);

        if (string.Equals(_games.Settings.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            _games.Settings.ActiveProfileId = ModProfile.DefaultId;
        }

        _games.SaveSettings();
        _logger.LogInformation("已删除配置档 {Name}（{Id}）", profile.Name, profileId);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return ProfileActionResult.Ok();
    }

    /// <summary>重命名。只改显示名与清单，不改目录名——避免路径变化导致引用失效。</summary>
    public ProfileActionResult Rename(string profileId, string newName)
    {
        var trimmed = newName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return ProfileActionResult.Fail("profiles.error.emptyName");
        }

        if (string.Equals(profileId, ModProfile.DefaultId, StringComparison.OrdinalIgnoreCase))
        {
            return ProfileActionResult.Fail("profiles.error.builtin");
        }

        var record = _games.Settings.ProfileRecords.FirstOrDefault(item => item.Id == profileId);
        if (record is null)
        {
            return ProfileActionResult.Fail("profiles.error.notFound");
        }

        if (GetAll().Any(item =>
                item.Id != profileId
                && string.Equals(item.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileActionResult.Fail("profiles.error.duplicate");
        }

        record.Name = trimmed;
        _games.SaveSettings();

        var profile = GetAll().FirstOrDefault(item => item.Id == profileId);
        if (profile is not null)
        {
            SyncManifest(profile);
        }

        _logger.LogInformation("配置档 {Id} 重命名为「{Name}」", profileId, trimmed);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);

        return ProfileActionResult.Ok();
    }

    private bool IsManagedDirectory(string directory)
    {
        var game = _games.Current ?? _games.Resolve();
        if (game is null)
        {
            return false;
        }

        var profilesRoot = Path.Combine(game.WorkspacePath, "profiles");
        return directory.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 重新扫描配置档的模组目录并重写清单。内置默认档不纳管，直接跳过——
    /// 它是 SMAPI 原生目录，写清单会污染游戏根目录。
    /// </summary>
    public void SyncManifest(ModProfile profile, GameInstallation? game = null)
    {
        if (profile.IsBuiltIn)
        {
            return;
        }

        var installation = game ?? _games.Current ?? _games.Resolve();
        if (installation is null)
        {
            return;
        }

        var profileRoot = GetProfileRoot(installation, profile.Id);
        var existing = _manifests.Read(profileRoot);
        var scan = _scanner.Scan(profile.ModsPath, GetDisabledPath(profile, installation));

        var manifest = new ProfileManifest
        {
            SchemaVersion = ProfileManifest.CurrentSchemaVersion,
            Id = profile.Id,
            Name = profile.Name,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.Now,
            IsolateGameInstance = profile.Isolation.IsolateGameInstance,
            IsolateSaves = profile.Isolation.IsolateSaves,
            Requirements = new ProfileRequirements
            {
                GameVersion = installation.GameVersion,
                SmapiVersion = installation.SmapiVersion
            },
            Mods = scan.Mods.Select(ToEntry).ToList()
        };

        _manifests.Write(profileRoot, manifest);
        _logger.LogInformation("已同步配置档清单：{Path}（{Count} 个模组）", profileRoot, manifest.Mods.Count);
    }

    /// <summary>
    /// 复制游戏本体到实例目录。刻意跳过 Mods、_smms 与隐藏目录：
    /// 实例只带走游戏与 SMAPI，模组由配置档自己决定要不要复制。
    /// </summary>
    private static void CopyGameDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);
            if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase)
                || name.Equals("_smms", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith('.'))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destination, name), cancellationToken);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>禁用区路径，与 <c>ModToggleService</c> 保持一致：内置档落在工作区，自建档落在档目录内。</summary>
    private static string? GetDisabledPath(ModProfile profile, GameInstallation game)
    {
        if (profile.IsBuiltIn)
        {
            return Path.Combine(game.WorkspacePath, "disabled");
        }

        var parent = Path.GetDirectoryName(profile.ModsPath);
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "_disabled");
    }

    private static ProfileModEntry ToEntry(InstalledMod mod) => new()
    {
        UniqueId = mod.UniqueId,
        Name = mod.Name,
        Version = mod.Version?.ToString() ?? string.Empty,
        NexusId = mod.NexusId,
        Folder = mod.FolderName,
        Enabled = mod.IsEnabled,
        ManifestSha256 = TryHashManifest(mod.DirectoryPath),
        IsContentPack = mod.IsContentPack
    };

    private static string? TryHashManifest(string directoryPath)
    {
        try
        {
            var path = Path.Combine(directoryPath, "manifest.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetProfileRoot(GameInstallation game, string profileId)
        => Path.Combine(game.WorkspacePath, "profiles", profileId);

    private static string ResolveModsPath(GameInstallation game, ProfileRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ModsPath))
        {
            return record.ModsPath;
        }

        // 完整实例（自建副本或外部引用）的模组落在该实例目录内，
        // 这样扫描、启用禁用、启动三条链路都只认一个路径。
        var instanceRoot = ResolveInstanceRoot(game, record);
        if (instanceRoot is not null)
        {
            return Path.Combine(instanceRoot, "Mods");
        }

        return Path.Combine(GetProfileRoot(game, record.Id), "Mods");
    }

    private static string? ResolveInstanceRoot(GameInstallation game, ProfileRecord record)
    {
        // 外部引用：直接指向玩家已有的独立备份，SMMS 不复制也不删除。
        if (!string.IsNullOrWhiteSpace(record.InstanceRootPath))
        {
            return record.InstanceRootPath;
        }

        return record.IsolateGameInstance
            ? Path.Combine(GetProfileRoot(game, record.Id), "instance")
            : null;
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

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "回滚配置档目录失败：{Path}", path);
        }
    }
}
