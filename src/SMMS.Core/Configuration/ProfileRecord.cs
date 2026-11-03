namespace SMMS.Core.Configuration;

/// <summary>配置档的持久化记录。<see cref="ModsPath"/> 为空表示落在 SMMS 工作区内。</summary>
public sealed class ProfileRecord
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ModsPath { get; set; }

    /// <summary>是否复制了完整游戏副本。为 true 时模组目录落在副本内。</summary>
    public bool IsolateGameInstance { get; set; }

    /// <summary>
    /// 该配置档使用的游戏根目录。
    /// 为空表示由 SMMS 在 <c>profiles/&lt;id&gt;/instance</c> 下创建副本；
    /// 非空表示直接引用这个目录（玩家已有的独立备份），SMMS 不会复制也不会删除它。
    /// </summary>
    public string? InstanceRootPath { get; set; }

    /// <summary>是否隔离存档。尚未实现，仅记录用户意图。</summary>
    public bool IsolateSaves { get; set; }
}
