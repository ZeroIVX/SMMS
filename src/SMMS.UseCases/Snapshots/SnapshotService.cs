using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Snapshots;
using SMMS.Core.Profiles;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Snapshots;

public sealed record SnapshotActionResult(bool Success, string? ErrorKey)
{
    public static SnapshotActionResult Ok() => new(true, null);

    public static SnapshotActionResult Fail(string errorKey) => new(false, errorKey);
}

/// <summary>
/// 模组目录快照。整目录复制而非增量——星露谷模组总体积通常在数百 MB，
/// 增量方案带来的复杂度远超它省下的磁盘。
/// </summary>
public sealed class SnapshotService(
    GameInstallationService games,
    ILogger<SnapshotService> logger)
{
    private const string SnapshotFileName = "snapshot.json";
    private const string ModsFolderName = "Mods";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<SnapshotRecord> List(string profileId)
    {
        var root = GetProfileSnapshotRoot(profileId);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return [];
        }

        var records = new List<SnapshotRecord>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var record = ReadRecord(Path.Combine(directory, SnapshotFileName));
            if (record is not null)
            {
                records.Add(record);
            }
        }

        // 新的在前，符合「回到刚才那次操作之前」的使用直觉。
        return records.OrderByDescending(record => record.CreatedAt).ToList();
    }

    public SnapshotActionResult Create(ModProfile profile, string? label = null, SnapshotKind kind = SnapshotKind.Manual)
    {
        var game = games.Current ?? games.Resolve();
        if (game is null)
        {
            return SnapshotActionResult.Fail("snapshots.error.noGame");
        }

        if (!Directory.Exists(profile.ModsPath))
        {
            return SnapshotActionResult.Fail("snapshots.error.noMods");
        }

        var id = $"s{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22];
        var directory = Path.Combine(GetProfileSnapshotRoot(profile.Id)!, id);

        try
        {
            Directory.CreateDirectory(directory);

            long size = 0;

            // 拿上一份快照当基准：内容未变的文件走硬链接，共享物理数据，不额外占空间。
            // 每个快照仍是完整目录树，所以恢复逻辑不需要处理链式依赖。
            var existing = List(profile.Id);
            var previous = existing.Count > 0 ? existing[0] : null;
            var previousMods = previous is null
                ? null
                : Path.Combine(GetProfileSnapshotRoot(profile.Id)!, previous.Id, ModsFolderName);

            var count = CopyDirectory(profile.ModsPath, Path.Combine(directory, ModsFolderName), ref size, previousMods);

            var record = new SnapshotRecord
            {
                Id = id,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                CreatedAt = DateTimeOffset.Now,
                Label = label,
                ModCount = count,
                SizeBytes = size,
                Kind = kind,
                IsAutomatic = kind != SnapshotKind.Manual
            };

            File.WriteAllText(
                Path.Combine(directory, SnapshotFileName),
                JsonSerializer.Serialize(record, JsonOptions));

            logger.LogInformation(
                "已创建快照 {Id}（{Name}）：{Count} 个模组，{Size} 字节",
                id, profile.Name, count, size);

            return SnapshotActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "创建快照失败：{Path}", directory);
            TryDeleteDirectory(directory);
            return SnapshotActionResult.Fail("snapshots.error.createFailed");
        }
    }

    /// <summary>
    /// 按类型清理快照，手动创建的一律不动。
    /// 启动前快照只留最新一份（它代表「上次能正常启动的状态」），
    /// 更新/恢复前的保护快照保留最近若干份。
    /// </summary>
    public void Prune(string profileId, int keepProtective = 5)
    {
        try
        {
            var records = List(profileId);

            foreach (var record in records
                         .Where(record => record.EffectiveKind == SnapshotKind.PreLaunch)
                         .Skip(1))
            {
                Delete(profileId, record.Id);
                logger.LogInformation("已清理旧启动前快照 {Id}", record.Id);
            }

            foreach (var record in records
                         .Where(record => record.EffectiveKind is SnapshotKind.PreUpdate or SnapshotKind.PreRestore)
                         .Skip(keepProtective))
            {
                Delete(profileId, record.Id);
                logger.LogInformation("已清理过期自动快照 {Id}", record.Id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程，下次再试。
            logger.LogWarning(ex, "清理快照失败");
        }
    }

    public SnapshotActionResult Delete(string profileId, string snapshotId)
    {
        var directory = GetSnapshotDirectory(profileId, snapshotId);
        if (directory is null || !Directory.Exists(directory))
        {
            return SnapshotActionResult.Fail("snapshots.error.notFound");
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return SnapshotActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "删除快照失败：{Path}", directory);
            return SnapshotActionResult.Fail("snapshots.error.deleteFailed");
        }
    }

    /// <summary>
    /// 用快照覆盖当前模组目录。
    /// 执行前先给当前状态拍一张自动快照——恢复本身也是破坏性操作，必须可回退。
    /// </summary>
    public SnapshotActionResult Restore(ModProfile profile, string snapshotId)
    {
        var directory = GetSnapshotDirectory(profile.Id, snapshotId);
        if (directory is null || !Directory.Exists(directory))
        {
            return SnapshotActionResult.Fail("snapshots.error.notFound");
        }

        var source = Path.Combine(directory, ModsFolderName);
        if (!Directory.Exists(source))
        {
            return SnapshotActionResult.Fail("snapshots.error.broken");
        }

        if (!Directory.Exists(profile.ModsPath))
        {
            return SnapshotActionResult.Fail("snapshots.error.noMods");
        }

        var guard = Create(profile, "恢复前自动保护", SnapshotKind.PreRestore);
        if (!guard.Success)
        {
            // 保护快照建不出来就不动当前状态，避免把玩家唯一一份模组搞丢。
            logger.LogWarning("恢复中止：无法创建保护快照");
            return SnapshotActionResult.Fail("snapshots.error.guardFailed");
        }

        try
        {
            ClearDirectory(profile.ModsPath);

            long ignored = 0;

            // 恢复是全量铺开：不传 previousRoot，避免把快照里的硬链接关系带到玩家目录。
            CopyDirectory(source, profile.ModsPath, ref ignored, previousRoot: null);

            logger.LogInformation("已从快照 {Id} 恢复 {Name}", snapshotId, profile.Name);
            return SnapshotActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "从快照恢复失败：{Id}", snapshotId);
            return SnapshotActionResult.Fail("snapshots.error.restoreFailed");
        }
    }

    private string? GetProfileSnapshotRoot(string profileId)
    {
        var game = games.Current ?? games.Resolve();
        return game is null ? null : Path.Combine(game.WorkspacePath, "snapshots", profileId);
    }

    private string? GetSnapshotDirectory(string profileId, string snapshotId)
    {
        var root = GetProfileSnapshotRoot(profileId);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, snapshotId);
    }

    private static SnapshotRecord? ReadRecord(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<SnapshotRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void ClearDirectory(string path)
    {
        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            File.Delete(file);
        }
    }

    /// <summary>复制目录并统计新增字节数，返回顶层模组数量。</summary>
    /// <param name="previousRoot">上一份快照的 Mods 目录，用作硬链接基准；null 表示全量复制。</param>
    private static int CopyDirectory(string source, string destination, ref long totalSize, string? previousRoot)
    {
        Directory.CreateDirectory(destination);

        var modCount = 0;

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            modCount++;
            var name = Path.GetFileName(directory);
            CopyTree(
                directory,
                Path.Combine(destination, name),
                previousRoot is null ? null : Path.Combine(previousRoot, name),
                ref totalSize);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            LinkOrCopy(
                file,
                Path.Combine(destination, name),
                previousRoot is null ? null : Path.Combine(previousRoot, name),
                ref totalSize);
        }

        return modCount;
    }

    private static void CopyTree(string source, string destination, string? previousDestination, ref long totalSize)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            CopyTree(
                directory,
                Path.Combine(destination, name),
                previousDestination is null ? null : Path.Combine(previousDestination, name),
                ref totalSize);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            LinkOrCopy(
                file,
                Path.Combine(destination, name),
                previousDestination is null ? null : Path.Combine(previousDestination, name),
                ref totalSize);
        }
    }

    /// <summary>
    /// 内容与上一份快照一致就建硬链接（两处指向同一份磁盘数据，不额外占空间），
    /// 否则复制。跨卷或非 NTFS 时系统调用失败，自动回退到复制。
    /// </summary>
    private static void LinkOrCopy(string sourceFile, string targetFile, string? previousFile, ref long totalSize)
    {
        var info = new FileInfo(sourceFile);

        if (previousFile is not null && File.Exists(previousFile))
        {
            var previous = new FileInfo(previousFile);

            if (info.Length == previous.Length
                && info.LastWriteTimeUtc == previous.LastWriteTimeUtc
                && CreateHardLink(targetFile, previousFile, IntPtr.Zero))
            {
                return;
            }
        }

        info.CopyTo(targetFile, overwrite: true);
        totalSize += info.Length;
    }

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

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
            logger.LogWarning(ex, "回滚快照目录失败：{Path}", path);
        }
    }
}
