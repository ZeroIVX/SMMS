namespace SMMS.Core.Updates;

/// <summary>单个模组的更新检查结果。</summary>
public sealed record ModUpdateInfo
{
    public required string UniqueId { get; init; }

    public string? CurrentVersion { get; init; }

    /// <summary>smapi.io 建议的目标版本。留空表示无需更新，不代表"没有更新"就一定是最新。</summary>
    public string? SuggestedVersion { get; init; }

    public string? UpdateUrl { get; init; }

    /// <summary>Wiki 上的兼容性状态：Ok / WorkaroundRequired / Obsolete 等。</summary>
    public string? CompatibilityStatus { get; init; }

    /// <summary>导致该模组失效的 SMAPI 或游戏版本。</summary>
    public string? BrokeIn { get; init; }

    public string? Name { get; init; }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(SuggestedVersion);

    public bool IsIncompatible =>
        !string.IsNullOrWhiteSpace(CompatibilityStatus)
        && !string.Equals(CompatibilityStatus, "Ok", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一次批量更新检查的结果。</summary>
public sealed record UpdateCheckResult
{
    public static UpdateCheckResult Empty { get; } = new();

    public IReadOnlyList<ModUpdateInfo> Mods { get; init; } = [];

    /// <summary>接口返回的可读错误（某个模组页面版本号异常等），不阻断整体结果。</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public int UpdateCount => Mods.Count(mod => mod.HasUpdate);

    public int IncompatibleCount => Mods.Count(mod => mod.IsIncompatible);

    public bool Succeeded { get; init; } = true;

    public ModUpdateInfo? Find(string uniqueId)
        => Mods.FirstOrDefault(mod => string.Equals(mod.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>批量检查的请求参数。</summary>
public sealed record UpdateCheckRequest(
    string? ApiVersion,
    string? GameVersion,
    string Platform,
    IReadOnlyList<UpdateCheckMod> Mods);

public sealed record UpdateCheckMod(
    string Id,
    IReadOnlyList<string> UpdateKeys,
    string? InstalledVersion,
    bool IsBroken = false);
