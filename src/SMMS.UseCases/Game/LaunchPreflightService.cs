using System.Globalization;
using Microsoft.Extensions.Logging;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;
using SMMS.Core.Versioning;

namespace SMMS.UseCases.Game;

/// <summary>
/// 启动前检查。目标不是拦住玩家，而是让玩家在点下去之前就知道会发生什么——
/// 模组不兼容的表现是游戏闪退，玩家很难自行关联到「某个模组要求更高的 SMAPI 版本」。
/// </summary>
public sealed class LaunchPreflightService(IModScanner scanner, ILogger<LaunchPreflightService> logger)
{
    public PreflightResult Check(
        GameInstallation? game,
        ModProfile? profile,
        LaunchMode mode,
        bool isGameRunning)
    {
        var issues = new List<PreflightIssue>();

        if (game is null)
        {
            issues.Add(Error("preflight.noGame"));
            return new PreflightResult(issues);
        }

        if (isGameRunning)
        {
            issues.Add(Error("preflight.gameRunning"));
        }

        if (mode == LaunchMode.Smapi && !game.IsSmapiInstalled)
        {
            issues.Add(Error("preflight.smapiMissing"));
        }

        if (profile is null)
        {
            issues.Add(Error("preflight.noProfile"));
            return new PreflightResult(issues);
        }

        if (!Directory.Exists(profile.ModsPath))
        {
            issues.Add(Error("preflight.modsMissing", profile.ModsPath));
            return new PreflightResult(issues);
        }

        var scan = scanner.Scan(profile.ModsPath);

        if (scan.Warnings.Count > 0)
        {
            issues.Add(Warning(
                "preflight.manifestBroken",
                scan.Warnings.Count.ToString(CultureInfo.CurrentCulture)));
        }

        CheckSmapiCompatibility(game, mode, scan, issues);
        CheckDependencies(scan, issues);

        logger.LogInformation(
            "启动预检完成：{Count} 项问题（错误 {Errors} 项）",
            issues.Count,
            issues.Count(issue => issue.Severity == PreflightSeverity.Error));

        return new PreflightResult(issues);
    }

    private static void CheckSmapiCompatibility(
        GameInstallation game,
        LaunchMode mode,
        ModScanResult scan,
        List<PreflightIssue> issues)
    {
        if (mode != LaunchMode.Smapi || !SemanticVersion.TryParse(game.SmapiVersion, out var smapiVersion))
        {
            return;
        }

        var blocked = scan.Mods
            .Where(mod => mod.MinimumApiVersion is not null && smapiVersion < mod.MinimumApiVersion)
            .Select(mod => mod.Name)
            .ToList();

        if (blocked.Count == 0)
        {
            return;
        }

        issues.Add(Error(
            "preflight.smapiTooOld",
            smapiVersion.ToString(),
            Summarize(blocked)));
    }

    private static void CheckDependencies(ModScanResult scan, List<PreflightIssue> issues)
    {
        var installed = scan.Mods
            .Select(mod => mod.UniqueId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();

        foreach (var mod in scan.Mods)
        {
            foreach (var dependency in mod.Dependencies.Where(dependency => dependency.IsRequired))
            {
                if (!installed.Contains(dependency.UniqueId))
                {
                    missing.Add($"{mod.Name} → {dependency.UniqueId}");
                }
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        // 缺依赖通常是内容包没有对应的框架模组，属于能玩但功能残缺，定为警告。
        issues.Add(Warning("preflight.missingDependencies", Summarize(missing)));
    }

    private static string Summarize(List<string> items, int limit = 5)
        => items.Count <= limit
            ? string.Join("、", items)
            : string.Join("、", items.Take(limit)) + $" 等 {items.Count} 项";

    private static PreflightIssue Error(string code, params string[] arguments)
        => new() { Severity = PreflightSeverity.Error, Code = code, Arguments = arguments };

    private static PreflightIssue Warning(string code, params string[] arguments)
        => new() { Severity = PreflightSeverity.Warning, Code = code, Arguments = arguments };
}
