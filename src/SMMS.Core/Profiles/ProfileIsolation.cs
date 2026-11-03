namespace SMMS.Core.Profiles;

/// <summary>
/// 配置档的隔离范围。两个维度独立组合，比三级枚举更灵活：
/// 可以「隔离完整实例但不隔离存档」，也可以「共享本体但隔离存档」。
/// </summary>
public sealed record ProfileIsolation
{
    public bool IsolateGameInstance { get; init; }

    public bool IsolateSaves { get; init; }

    public static ProfileIsolation ModsOnly { get; } = new();

    public static ProfileIsolation WithSaves { get; } = new() { IsolateSaves = true };

    public static ProfileIsolation FullInstance { get; } = new() { IsolateGameInstance = true };

    public static ProfileIsolation FullInstanceWithSaves { get; } = new()
    {
        IsolateGameInstance = true,
        IsolateSaves = true
    };

    /// <summary>界面展示用的本地化键。</summary>
    public string PresetKey => (IsolateGameInstance, IsolateSaves) switch
    {
        (false, false) => "profiles.isolation.modsOnly",
        (false, true) => "profiles.isolation.withSaves",
        (true, false) => "profiles.isolation.full",
        _ => "profiles.isolation.fullWithSaves"
    };
}
