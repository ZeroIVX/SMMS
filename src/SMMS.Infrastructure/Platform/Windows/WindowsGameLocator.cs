using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SMMS.Core.Game;
using SMMS.Core.Ports;

namespace SMMS.Infrastructure.Platform.Windows;

/// <summary>
/// Windows 游戏定位：Steam（注册表 + libraryfolders.vdf 多库）与 GOG（注册表）双来源。
/// 手动指定的目录由上层通过 <see cref="TryInspect"/> 校验，不在此处探测。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsGameLocator(ILogger<WindowsGameLocator> logger) : IGameLocator
{
    private const string GameFolderName = "Stardew Valley";
    private const string GameExecutableName = "Stardew Valley.exe";
    private const string GameAssemblyName = "Stardew Valley.dll";
    private const string SmapiExecutableName = "StardewModdingAPI.exe";

    public IReadOnlyList<GameInstallation> Discover()
    {
        var found = new Dictionary<string, GameInstallation>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, distribution) in EnumerateCandidates())
        {
            if (!TryInspect(root, out var installation) || installation is null)
            {
                continue;
            }

            found[installation.RootPath] = installation with { Distribution = distribution };
        }

        logger.LogInformation("自动发现到 {Count} 个游戏安装", found.Count);
        return found.Values.ToList();
    }

    public bool TryInspect(string rootPath, out GameInstallation? installation)
    {
        installation = null;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        string root;
        try
        {
            root = Path.GetFullPath(rootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!Directory.Exists(root))
        {
            return false;
        }

        var executable = Path.Combine(root, GameExecutableName);
        var assembly = Path.Combine(root, GameAssemblyName);

        // 以程序集或可执行文件之一存在作为有效安装的判据。
        if (!File.Exists(assembly) && !File.Exists(executable))
        {
            return false;
        }

        var smapi = Path.Combine(root, SmapiExecutableName);
        var hasSmapi = File.Exists(smapi);

        installation = new GameInstallation
        {
            RootPath = root,
            ExecutablePath = File.Exists(executable) ? executable : assembly,
            SmapiExecutablePath = hasSmapi ? smapi : null,
            GameVersion = ReadFileVersion(File.Exists(assembly) ? assembly : executable),
            SmapiVersion = hasSmapi ? ReadFileVersion(smapi) : null,
            Platform = GamePlatform.Windows,
            Distribution = GameDistribution.Unknown
        };

        return true;
    }

    private static IEnumerable<(string Root, GameDistribution Distribution)> EnumerateCandidates()
    {
        foreach (var library in EnumerateSteamLibraries())
        {
            yield return (Path.Combine(library, "steamapps", "common", GameFolderName), GameDistribution.Steam);
        }

        foreach (var gogPath in EnumerateGogInstalls())
        {
            yield return (gogPath, GameDistribution.Gog);
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        var roots = new List<string>();

        var steamPath = ReadSteamPathFromRegistry();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            roots.Add(steamPath);
        }

        roots.Add(@"C:\Program Files (x86)\Steam");

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libraryFolders = Path.Combine(root, "steamapps", "libraryfolders.vdf");

            if (File.Exists(libraryFolders))
            {
                foreach (var library in ParseLibraryFolders(libraryFolders))
                {
                    yield return library;
                }
            }
            else if (Directory.Exists(Path.Combine(root, "steamapps", "common")))
            {
                yield return root;
            }
        }
    }

    private static IEnumerable<string> ParseLibraryFolders(string libraryFoldersPath)
    {
        string? content;
        try
        {
            content = File.ReadAllText(libraryFoldersPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = null;
        }

        if (content is null)
        {
            yield break;
        }

        foreach (Match match in LibraryPathRegex().Matches(content))
        {
            // VDF 中路径为 "D:\\SteamLibrary" 形式的转义写法。
            var path = match.Groups["path"].Value.Replace(@"\\", @"\");
            if (path.Length > 0)
            {
                yield return path;
            }
        }
    }

    private static List<string> EnumerateGogInstalls()
    {
        var results = new List<string>();
        string[] hives =
        [
            @"SOFTWARE\WOW6432Node\GOG.com\Games",
            @"SOFTWARE\GOG.com\Games"
        ];

        foreach (var hive in hives)
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(hive);
                if (root is null)
                {
                    continue;
                }

                foreach (var subKeyName in root.GetSubKeyNames())
                {
                    using var subKey = root.OpenSubKey(subKeyName);
                    if (subKey?.GetValue("path") is string path
                        && path.Contains(GameFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // 无权限读取该注册表分支时跳过，不影响其他来源。
            }
        }

        return results;
    }

    private static string? ReadSteamPathFromRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var raw = info.ProductVersion ?? info.FileVersion;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var match = VersionRegex().Match(raw);
            return match.Success ? match.Value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex(@"\d+(?:\.\d+){1,3}")]
    private static partial Regex VersionRegex();
}
