using SMMS.Core.Game;

namespace SMMS.Core.Ports;

/// <summary>
/// 定位本机的游戏安装。跨平台端口，Windows 提供 Steam / GOG 双来源探测。
/// </summary>
public interface IGameLocator
{
    /// <summary>自动发现所有可用的游戏安装，可能返回多个（多 Steam 库）。</summary>
    IReadOnlyList<GameInstallation> Discover();

    /// <summary>校验指定目录是否为有效的游戏根目录，并读取版本信息。</summary>
    bool TryInspect(string rootPath, out GameInstallation? installation);
}
