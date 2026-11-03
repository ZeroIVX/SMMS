namespace SMMS.Core.Game;

/// <summary>启动方式。原版不经过 SMAPI，不加载任何模组。</summary>
public enum LaunchMode
{
    /// <summary>直接启动游戏本体，模组一律不加载。</summary>
    Vanilla = 0,

    /// <summary>通过 SMAPI 启动，按配置档加载模组。</summary>
    Smapi = 1
}
