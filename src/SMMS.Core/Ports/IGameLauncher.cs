using SMMS.Core.Game;
using SMMS.Core.Profiles;

namespace SMMS.Core.Ports;

/// <summary>
/// 构造启动计划。平台差异在此消化：
/// Windows 用 <c>--mods-path</c> 命令行参数，Linux / macOS 用 <c>SMAPI_MODS_PATH</c> 环境变量。
/// </summary>
public interface IGameLauncher
{
    /// <summary>
    /// 按指定方式构造启动计划。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 请求 SMAPI 启动但游戏未安装 SMAPI 时抛出。调用方应在此之前用
    /// <see cref="GameInstallation.IsSmapiInstalled"/> 判断并禁用入口。
    /// </exception>
    LaunchPlan BuildLaunchPlan(GameInstallation game, ModProfile profile, LaunchMode mode);
}
