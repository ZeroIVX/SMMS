namespace SMMS.Core.Snapshots;

/// <summary>快照的触发来源。决定清理策略：手动的不动，启动前只留一份。</summary>
public enum SnapshotKind
{
    /// <summary>玩家手动创建，永不自动清理。</summary>
    Manual = 0,

    /// <summary>更新模组前自动创建，保留最近若干份。</summary>
    PreUpdate = 1,

    /// <summary>恢复操作前自动创建，保留最近若干份。</summary>
    PreRestore = 2,

    /// <summary>启动游戏前自动创建，始终只保留最新一份——它就是「上次能正常启动的状态」。</summary>
    PreLaunch = 3
}

/// <summary>一次模组目录快照的元信息，落盘为快照目录下的 snapshot.json。</summary>
public sealed class SnapshotRecord
{
    public string Id { get; set; } = string.Empty;

    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>触发原因或用户备注，例如「恢复前自动保护」。</summary>
    public string? Label { get; set; }

    public int ModCount { get; set; }

    /// <summary>为这份快照实际新增占用的字节数。硬链接复用上一份的文件时不计入。</summary>
    public long SizeBytes { get; set; }

    public SnapshotKind Kind { get; set; } = SnapshotKind.Manual;

    /// <summary>旧字段：早期只有「是否自动」两态，读取时映射到 <see cref="Kind"/>。</summary>
    public bool IsAutomatic { get; set; }

    /// <summary>读取旧快照时补齐 Kind。</summary>
    public SnapshotKind EffectiveKind =>
        Kind == SnapshotKind.Manual && IsAutomatic ? SnapshotKind.PreRestore : Kind;
}
