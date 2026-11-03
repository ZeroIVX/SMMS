namespace SMMS.Core.Game;

/// <summary>
/// 启动游戏的完整计划。平台差异（命令行参数 vs 环境变量）在构建阶段消化，
/// 上层只需把计划交给启动器执行，不感知平台。
/// </summary>
public sealed record LaunchPlan(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables);
