using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SMMS.Core.Archives;
using SMMS.Core.Game;
using SMMS.Core.Vanity;

namespace SMMS.UseCases.Vanity;

public sealed record VanityActionResult(bool Success, string? ErrorKey)
{
    public static VanityActionResult Ok() => new(true, null);

    public static VanityActionResult Fail(string errorKey) => new(false, errorKey);
}

public sealed record VanityImportResult(bool Success, string? ErrorKey, VanityPackage? Package, int FileCount)
{
    public static VanityImportResult Fail(string errorKey) => new(false, errorKey, null, 0);
}

/// <summary>
/// Content 替换型美化包管理。
/// </summary>
/// <remarks>
/// 核心约束：星露谷从 exe 所在目录读 Content，没有参数能改变这一点。
/// 所以"切换美化包"只能靠替换文件，而不是指向别的目录。
/// 为了让替换可逆，第一次覆盖某文件前会把它存进 baseline——全局唯一一份，
/// 多个包共享，避免出现「B 的基线是 A 美化后的样子」。
/// </remarks>
[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "路径方法与会话状态保留为实例成员，后续要按游戏根做缓存时不必再改签名。")]
public sealed class VanityService(ILogger<VanityService> logger)
{
    private const string PackageFileName = "package.json";
    private const string StateFileName = "state.json";
    private const string ContentFolderName = "Content";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<VanityPackage> List(GameInstallation game)
    {
        var root = GetPackagesRoot(game);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var packages = new List<VanityPackage>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var package = ReadPackage(Path.Combine(directory, PackageFileName));
            if (package is not null)
            {
                packages.Add(package);
            }
        }

        return packages.OrderBy(package => package.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public VanityState GetState(GameInstallation game)
    {
        try
        {
            var path = Path.Combine(GetVanityRoot(game), StateFileName);
            if (!File.Exists(path))
            {
                return new VanityState();
            }

            return JsonSerializer.Deserialize<VanityState>(File.ReadAllText(path), JsonOptions) ?? new VanityState();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new VanityState();
        }
    }

    /// <summary>导入压缩包：解压到包目录，自动定位 Content 层。</summary>
    public VanityImportResult Import(GameInstallation game, string archivePath)
    {
        if (!File.Exists(archivePath))
        {
            return VanityImportResult.Fail("vanity.error.archiveMissing");
        }

        // 有 manifest.json 的是模组（含 Content Patcher 美化包），不该走内容替换流程。
        var kind = PackageInspector.Inspect(archivePath);

        if (kind == PackageKind.Mod)
        {
            logger.LogWarning("拒绝导入：{Path} 是模组包，应放入 Mods 目录", archivePath);
            return VanityImportResult.Fail("vanity.error.isMod");
        }

        if (kind == PackageKind.ProfilePack)
        {
            return VanityImportResult.Fail("vanity.error.isProfilePack");
        }

        var name = Path.GetFileNameWithoutExtension(archivePath);
        var id = $"v{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..20];
        var packageRoot = Path.Combine(GetPackagesRoot(game), id);
        var staging = Path.Combine(Path.GetTempPath(), "smms-vanity-" + Guid.NewGuid().ToString("N"));

        try
        {
            ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);

            // 包内常有多层嵌套（作者习惯把 Content 埋在 包名/包名/ 下），
            // 因此从解压结果里搜索真正的 Content 目录，而不是假设固定层级。
            var contentRoot = FindContentRoot(staging);
            if (contentRoot is null)
            {
                TryDeleteDirectory(staging);
                return VanityImportResult.Fail("vanity.error.noContent");
            }

            var target = Path.Combine(packageRoot, "files", ContentFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long totalBytes = 0;
            var count = CopyDirectory(contentRoot, target, ref totalBytes);

            var notePath = FindReadme(staging);

            var package = new VanityPackage
            {
                Id = id,
                Name = name,
                SourceArchive = archivePath,
                ImportedAt = DateTimeOffset.Now,
                FileCount = count,
                TotalBytes = totalBytes,
                ContentRoot = ContentFolderName,
                Note = notePath is null ? null : TryReadText(notePath)
            };

            File.WriteAllText(
                Path.Combine(packageRoot, PackageFileName),
                JsonSerializer.Serialize(package, JsonOptions));

            logger.LogInformation("已导入美化包「{Name}」：{Count} 个文件，{Size} 字节", name, count, totalBytes);

            return new VanityImportResult(true, null, package, count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogError(ex, "导入美化包失败：{Path}", archivePath);
            TryDeleteDirectory(packageRoot);
            return VanityImportResult.Fail("vanity.error.invalidArchive");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>应用某个美化包。已有其它包生效时会先还原，避免文件互相覆盖。</summary>
    public VanityActionResult Apply(GameInstallation game, string packageId)
    {
        var packageDirectory = Path.Combine(GetPackagesRoot(game), packageId);
        var package = ReadPackage(Path.Combine(packageDirectory, PackageFileName));
        if (package is null)
        {
            return VanityActionResult.Fail("vanity.error.notFound");
        }

        var sourceRoot = Path.Combine(packageDirectory, "files", ContentFolderName);
        if (!Directory.Exists(sourceRoot))
        {
            return VanityActionResult.Fail("vanity.error.broken");
        }

        if (GetState(game).IsActive)
        {
            var revert = Revert(game);
            if (!revert.Success)
            {
                return revert;
            }
        }

        try
        {
            var targetRoot = Path.Combine(game.RootPath, ContentFolderName);
            var baselineRoot = Path.Combine(GetBaselineRoot(game), ContentFolderName);

            var applied = new List<string>();
            long copied = 0;

            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var destination = Path.Combine(targetRoot, relative);
                var baselineFile = Path.Combine(baselineRoot, relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                // 只备份一次原版：baseline 里已有说明之前存过，不能再用当前（可能已被美化的）文件覆盖。
                if (!File.Exists(baselineFile) && File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(baselineFile)!);
                    File.Copy(destination, baselineFile, overwrite: false);
                }

                File.Copy(file, destination, overwrite: true);
                applied.Add(relative);
                copied++;
            }

            WriteState(game, new VanityState
            {
                ActivePackageId = package.Id,
                ActivePackageName = package.Name,
                AppliedAt = DateTimeOffset.Now,
                AppliedFiles = applied
            });

            logger.LogInformation("已应用美化包「{Name}」：覆盖 {Count} 个文件", package.Name, copied);
            return VanityActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "应用美化包失败：{Id}", packageId);
            return VanityActionResult.Fail("vanity.error.applyFailed");
        }
    }

    /// <summary>还原为原版 Content。</summary>
    public VanityActionResult Revert(GameInstallation game)
    {
        var state = GetState(game);
        if (!state.IsActive)
        {
            return VanityActionResult.Ok();
        }

        try
        {
            var targetRoot = Path.Combine(game.RootPath, ContentFolderName);
            var baselineRoot = Path.Combine(GetBaselineRoot(game), ContentFolderName);

            foreach (var relative in state.AppliedFiles)
            {
                var destination = Path.Combine(targetRoot, relative);
                var baselineFile = Path.Combine(baselineRoot, relative);

                if (File.Exists(baselineFile))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(baselineFile, destination, overwrite: true);
                }
                else if (File.Exists(destination))
                {
                    // baseline 里没有 → 该文件原本不存在，是美化包新增的，还原即删除。
                    File.Delete(destination);
                }
            }

            WriteState(game, new VanityState());
            logger.LogInformation("已还原原版 Content（{Count} 个文件）", state.AppliedFiles.Count);
            return VanityActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "还原美化包失败");
            return VanityActionResult.Fail("vanity.error.revertFailed");
        }
    }

    public VanityActionResult Delete(GameInstallation game, string packageId)
    {
        if (string.Equals(GetState(game).ActivePackageId, packageId, StringComparison.OrdinalIgnoreCase))
        {
            return VanityActionResult.Fail("vanity.error.deleteActive");
        }

        var directory = Path.Combine(GetPackagesRoot(game), packageId);
        if (!Directory.Exists(directory))
        {
            return VanityActionResult.Fail("vanity.error.notFound");
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return VanityActionResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "删除美化包失败：{Id}", packageId);
            return VanityActionResult.Fail("vanity.error.deleteFailed");
        }
    }

    /// <summary>
    /// 判断美化是否被外部覆盖（典型场景：Steam 更新重写了 Content）。
    /// 抽样比对大小即可，不必逐文件哈希。
    /// </summary>
    public bool IsAppliedButOverwritten(GameInstallation game)
    {
        var state = GetState(game);
        if (!state.IsActive || state.AppliedFiles.Count == 0)
        {
            return false;
        }

        var packageRoot = Path.Combine(GetPackagesRoot(game), state.ActivePackageId!, "files", ContentFolderName);
        if (!Directory.Exists(packageRoot))
        {
            return true;
        }

        var targetRoot = Path.Combine(game.RootPath, ContentFolderName);
        var sample = state.AppliedFiles.Take(8);

        foreach (var relative in sample)
        {
            var source = Path.Combine(packageRoot, relative);
            var destination = Path.Combine(targetRoot, relative);

            if (!File.Exists(source))
            {
                continue;
            }

            if (!File.Exists(destination))
            {
                return true;
            }

            if (new FileInfo(source).Length != new FileInfo(destination).Length)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetVanityRoot(GameInstallation game) => Path.Combine(game.WorkspacePath, "vanity");

    private string GetPackagesRoot(GameInstallation game) => Path.Combine(GetVanityRoot(game), "packages");

    private static string GetBaselineRoot(GameInstallation game) => Path.Combine(GetVanityRoot(game), "baseline");

    private void WriteState(GameInstallation game, VanityState state)
    {
        var root = GetVanityRoot(game);
        Directory.CreateDirectory(root);

        File.WriteAllText(
            Path.Combine(root, StateFileName),
            JsonSerializer.Serialize(state, JsonOptions));
    }

    private static VanityPackage? ReadPackage(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<VanityPackage>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>在解压结果里找真正的 Content 目录：名字匹配且含 xnb 文件。</summary>
    private static string? FindContentRoot(string root)
    {
        var candidates = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(directory => string.Equals(Path.GetFileName(directory), ContentFolderName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var candidate in candidates)
        {
            var hasFiles = Directory.EnumerateFiles(candidate, "*.xnb", SearchOption.AllDirectories).Any();
            if (hasFiles)
            {
                return candidate;
            }
        }

        // 允许包内直接平铺文件（没有 Content 这一层）。
        return Directory.EnumerateFiles(root, "*.xnb", SearchOption.AllDirectories).Any() ? root : null;
    }

    private static string? FindReadme(string root)
        => Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).FirstOrDefault();

    private static string? TryReadText(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length > 300 ? text[..300] : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CopyDirectory(string source, string destination, ref long totalBytes)
    {
        Directory.CreateDirectory(destination);

        var count = 0;

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            count += CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), ref totalBytes);
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            var info = new FileInfo(file);
            totalBytes += info.Length;
            info.CopyTo(target, overwrite: true);
            count++;
        }

        return count;
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
            logger.LogWarning(ex, "清理目录失败：{Path}", path);
        }
    }
}
