using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;

namespace SMMS.Infrastructure.Platform.Unsupported;

/// <summary>
/// 未支持平台的占位实现。行为是"明确失败 + 可读提示"，而不是静默无响应或抛出晦涩异常。
/// 开源后新增平台只需实现 Core 中的四个端口并在 DI 中替换注册，无需改动核心逻辑。
/// </summary>
internal sealed class UnsupportedGameLocator(ILogger<UnsupportedGameLocator> logger) : IGameLocator
{
    private const string Message = "当前平台尚未实现游戏定位，欢迎提交实现。";

    public IReadOnlyList<GameInstallation> Discover()
    {
        logger.LogWarning("{Message} 平台：{Platform}", Message, Environment.OSVersion.Platform);
        return [];
    }

    public bool TryInspect(string rootPath, out GameInstallation? installation)
    {
        installation = null;
        logger.LogWarning("{Message}", Message);
        return false;
    }
}

internal sealed class UnsupportedGameLauncher(ILogger<UnsupportedGameLauncher> logger) : IGameLauncher
{
    public LaunchPlan BuildLaunchPlan(GameInstallation game, ModProfile profile, LaunchMode mode)
    {
        logger.LogWarning("当前平台尚未实现启动计划构造，欢迎提交实现。");
        throw new PlatformNotSupportedException("当前平台尚未实现启动计划构造。");
    }
}

internal sealed class UnsupportedProtocolRegistrar(ILogger<UnsupportedProtocolRegistrar> logger) : IProtocolRegistrar
{
    public bool IsRegistered(string scheme) => false;

    public bool Register(string scheme, string executablePath)
    {
        logger.LogWarning("当前平台尚未实现协议注册，nxm:// 自动接管不可用。");
        return false;
    }

    public bool Unregister(string scheme) => false;
}

internal sealed class UnsupportedFileSystemLinker(ILogger<UnsupportedFileSystemLinker> logger) : IFileSystemLinker
{
    public bool AreOnSameVolume(string pathA, string pathB)
    {
        logger.LogDebug("当前平台尚未实现同卷判断，按不同卷处理。");
        return false;
    }

    public bool TryCreateDirectoryLink(string linkPath, string targetPath) => false;
}
