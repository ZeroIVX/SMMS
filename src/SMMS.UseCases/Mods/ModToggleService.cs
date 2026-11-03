using Microsoft.Extensions.Logging;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.UseCases.Game;

namespace SMMS.UseCases.Mods;

public sealed record ModToggleResult(bool Success, string? ErrorKey);

/// <summary>
/// 模组启用 / 禁用。通过在同卷目录间移动模组文件夹实现——
/// 不依赖 SMAPI 的目录命名规则，语义清晰且完全可逆。
/// </summary>
public sealed class ModToggleService(GameInstallationService games, ILogger<ModToggleService> logger)
{
    /// <summary>
    /// 禁用区路径。自建档放在配置档目录内，内置默认档放在 SMMS 工作区，
    /// 避免往游戏根目录扔文件夹。两者都与 Mods 同卷，移动是秒级的元数据操作。
    /// </summary>
    public string? GetDisabledPath(ModProfile profile)
    {
        if (profile.IsBuiltIn)
        {
            var game = games.Current ?? games.Resolve();
            return game is null ? null : Path.Combine(game.WorkspacePath, "disabled");
        }

        var parent = Path.GetDirectoryName(profile.ModsPath);
        return string.IsNullOrEmpty(parent) ? null : Path.Combine(parent, "_disabled");
    }

    public ModToggleResult SetEnabled(ModProfile profile, InstalledMod mod, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(mod);

        var disabledRoot = GetDisabledPath(profile);
        if (disabledRoot is null)
        {
            return new ModToggleResult(false, "mods.toggle.noGame");
        }

        var enabledRoot = profile.ModsPath;
        var source = Path.Combine(enabled ? disabledRoot : enabledRoot, mod.FolderName);
        var target = Path.Combine(enabled ? enabledRoot : disabledRoot, mod.FolderName);

        if (!Directory.Exists(source))
        {
            logger.LogWarning("源目录不存在，无法切换状态：{Source}", source);
            return new ModToggleResult(false, "mods.toggle.missing");
        }

        // 同名目录已存在时不覆盖——可能是同名不同版本的残留，交由玩家判断。
        if (Directory.Exists(target))
        {
            logger.LogWarning("目标目录已存在：{Target}", target);
            return new ModToggleResult(false, "mods.toggle.conflict");
        }

        try
        {
            Directory.CreateDirectory(disabledRoot);
            Directory.Move(source, target);
            logger.LogInformation("模组「{Mod}」已{Action}", mod.Name, enabled ? "启用" : "禁用");
            return new ModToggleResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "切换模组状态失败：{Mod}", mod.Name);
            return new ModToggleResult(false, "mods.toggle.failed");
        }
    }
}
