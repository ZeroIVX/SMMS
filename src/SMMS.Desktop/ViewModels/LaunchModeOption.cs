using SMMS.Core.Game;

namespace SMMS.Desktop.ViewModels;

/// <summary>启动方式下拉项。<see cref="DisplayName"/> 在语言切换时重建集合。</summary>
public sealed record LaunchModeOption(LaunchMode Mode, string DisplayName);
