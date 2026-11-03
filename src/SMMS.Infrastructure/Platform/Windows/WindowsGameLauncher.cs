using System.Runtime.Versioning;
using SMMS.Core.Game;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;

namespace SMMS.Infrastructure.Platform.Windows;

/// <summary>
/// Windows 启动计划。原版直接运行游戏本体；SMAPI 运行 <c>StardewModdingAPI.exe</c>
/// 并通过 <c>--mods-path</c> 指定模组目录（默认目录时省略该参数）。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGameLauncher : IGameLauncher
{
    public LaunchPlan BuildLaunchPlan(GameInstallation game, ModProfile profile, LaunchMode mode)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(profile);

        return mode switch
        {
            LaunchMode.Smapi => BuildSmapiPlan(game, profile),
            _ => BuildVanillaPlan(game)
        };
    }

    private static LaunchPlan BuildVanillaPlan(GameInstallation game) => new(
        ExecutablePath: game.ExecutablePath,
        WorkingDirectory: game.RootPath,
        Arguments: [],
        EnvironmentVariables: new Dictionary<string, string>());

    private static LaunchPlan BuildSmapiPlan(GameInstallation game, ModProfile profile)
    {
        if (!game.IsSmapiInstalled)
        {
            throw new InvalidOperationException("SMAPI 未安装，无法以 SMAPI 方式启动。");
        }

        var arguments = new List<string>();

        // 配置档就是默认 Mods 目录时不必传参，减少与手动启动的差异，便于排查问题。
        var usesDefaultModsFolder = string.Equals(
            profile.ModsPath.TrimEnd(Path.DirectorySeparatorChar),
            game.ModsPath.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        if (!usesDefaultModsFolder)
        {
            arguments.Add("--mods-path");
            arguments.Add(profile.ModsPath);
        }

        return new LaunchPlan(
            ExecutablePath: game.SmapiExecutablePath!,
            WorkingDirectory: game.RootPath,
            Arguments: arguments,
            EnvironmentVariables: new Dictionary<string, string>());
    }
}
