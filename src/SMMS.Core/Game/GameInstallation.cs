namespace SMMS.Core.Game;

public enum GamePlatform
{
    Unknown = 0,
    Windows,
    Linux,
    MacOS
}

public enum GameDistribution
{
    Unknown = 0,
    Steam,
    Gog,
    Manual
}

/// <summary>
/// 一次游戏安装的快照。所有路径均为绝对路径。
/// </summary>
public sealed record GameInstallation
{
    public required string RootPath { get; init; }

    public required string ExecutablePath { get; init; }

    /// <summary>SMAPI 启动器路径，未安装时为 <c>null</c>。</summary>
    public string? SmapiExecutablePath { get; init; }

    public string? GameVersion { get; init; }

    public string? SmapiVersion { get; init; }

    public GamePlatform Platform { get; init; } = GamePlatform.Unknown;

    public GameDistribution Distribution { get; init; } = GameDistribution.Unknown;

    public bool IsSmapiInstalled => !string.IsNullOrWhiteSpace(SmapiExecutablePath);

    /// <summary>SMAPI 默认模组目录。</summary>
    public string ModsPath => Path.Combine(RootPath, "Mods");

    /// <summary>SMMS 工作区，与 Mods 同级，不会被 SMAPI 扫描。</summary>
    public string WorkspacePath => Path.Combine(RootPath, "_smms");
}
