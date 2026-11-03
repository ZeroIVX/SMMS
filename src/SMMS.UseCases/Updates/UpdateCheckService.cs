using Microsoft.Extensions.Logging;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;
using SMMS.Core.Updates;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Updates;

/// <summary>
/// 批量更新检查。只提交启用中的模组——禁用模组不参与加载，也不该占用检查配额。
/// </summary>
public sealed class UpdateCheckService(
    ISmapiCatalog catalog,
    GameInstallationService games,
    IModScanner scanner,
    ILogger<UpdateCheckService> logger)
{
    public async Task<UpdateCheckResult> CheckAsync(
        ModProfile profile,
        string? disabledPath,
        CancellationToken cancellationToken = default)
    {
        var game = games.Current ?? games.Resolve();
        if (game is null)
        {
            return new UpdateCheckResult { Succeeded = false, Errors = ["preflight.noGame"] };
        }

        var scan = scanner.Scan(profile.ModsPath, disabledPath);
        var enabled = scan.Mods.Where(mod => mod.IsEnabled).ToList();

        if (enabled.Count == 0)
        {
            return UpdateCheckResult.Empty;
        }

        var request = new UpdateCheckRequest(
            ApiVersion: game.SmapiVersion,
            GameVersion: game.GameVersion,
            Platform: "Windows",
            Mods: enabled
                .Select(mod => new UpdateCheckMod(
                    mod.UniqueId,
                    mod.UpdateKeys,
                    mod.Version?.ToString(),
                    IsBroken: false))
                .ToList());

        var result = await catalog.CheckAsync(request, cancellationToken).ConfigureAwait(false);

        // 响应里没有本地版本，回填后界面才能显示 "当前 → 最新"。
        var localVersions = BuildVersionLookup(enabled);

        var merged = result.Mods
            .Select(info => info with
            {
                CurrentVersion = localVersions.TryGetValue(info.UniqueId, out var version) ? version : null
            })
            .ToList();

        logger.LogInformation(
            "更新检查完成：{Total} 个模组，{Updates} 个有新版本，{Incompatible} 个不兼容",
            merged.Count,
            merged.Count(info => info.HasUpdate),
            merged.Count(info => info.IsIncompatible));

        return result with { Mods = merged };
    }

    private static Dictionary<string, string> BuildVersionLookup(IEnumerable<InstalledMod> mods)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (mod.Version is not null)
            {
                lookup[mod.UniqueId] = mod.Version.ToString();
            }
        }

        return lookup;
    }
}
