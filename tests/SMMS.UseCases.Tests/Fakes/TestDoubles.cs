using System.Text.Json;
using SMMS.Core.Configuration;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Ports;
using SMMS.Core.Profiles;
using SMMS.Core.Versioning;

namespace SMMS.UseCases.Tests.Fakes;

internal sealed class EmptyModScanner : IModScanner
{
    public ModScanResult Result { get; set; } = ModScanResult.Empty;

    public ModScanResult Scan(string modsPath, string? disabledPath = null) => Result;
}

internal sealed class InMemoryProfileManifestStore : IProfileManifestStore
{
    public Dictionary<string, ProfileManifest> Written { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProfileManifest? Read(string profileDirectory)
        => Written.TryGetValue(profileDirectory, out var manifest) ? manifest : null;

    public bool Write(string profileDirectory, ProfileManifest manifest)
    {
        Written[profileDirectory] = manifest;
        return true;
    }
}

/// <summary>真实读盘但只解析关键字段的扫描器，用于需要真实目录结构的集成测试。</summary>
internal sealed class DirectoryModScanner : IModScanner
{
    public ModScanResult Scan(string modsPath, string? disabledPath = null)
    {
        var mods = new List<InstalledMod>();
        ScanDirectory(modsPath, isEnabled: true, mods);

        if (!string.IsNullOrWhiteSpace(disabledPath))
        {
            ScanDirectory(disabledPath, isEnabled: false, mods);
        }

        return new ModScanResult(mods, []);
    }

    private static void ScanDirectory(string path, bool isEnabled, List<InstalledMod> mods)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path))
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            var folder = Path.GetFileName(directory);

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;

            var uniqueId = root.TryGetProperty("UniqueID", out var idElement) ? idElement.GetString() : null;
            var name = root.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
            var version = root.TryGetProperty("Version", out var versionElement) ? versionElement.GetString() : null;

            mods.Add(new InstalledMod
            {
                DirectoryPath = directory,
                FolderName = folder,
                UniqueId = uniqueId ?? folder,
                Name = name ?? folder,
                Version = SemanticVersion.TryParse(version, out var parsed) ? parsed : null,
                IsEnabled = isEnabled
            });
        }
    }
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettings Settings { get; private set; } = new();

    public AppSettings Load() => Settings;

    public void Save(AppSettings settings) => Settings = settings;
}

internal sealed class StubGameLocator : IGameLocator
{
    public GameInstallation? Installation { get; set; }

    /// <summary>额外的可识别目录，用于「纳入已有游戏目录」这类按路径校验的场景。</summary>
    public List<GameInstallation> ExtraInstallations { get; } = [];

    public IReadOnlyList<GameInstallation> Discover()
        => Installation is null ? [] : [Installation];

    public bool TryInspect(string rootPath, out GameInstallation? installation)
    {
        var normalized = Normalize(rootPath);

        installation = ExtraInstallations
            .Concat(Installation is null ? [] : [Installation])
            .FirstOrDefault(candidate => string.Equals(Normalize(candidate.RootPath), normalized, StringComparison.OrdinalIgnoreCase));

        return installation is not null;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
}

/// <summary>在临时目录里造一个最小可用的游戏目录。</summary>
internal sealed class TempGameDirectory : IDisposable
{
    public TempGameDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "smms-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        GameExecutable = Path.Combine(Root, "Stardew Valley.exe");
        File.WriteAllText(GameExecutable, string.Empty);

        ModsPath = Path.Combine(Root, "Mods");
        Directory.CreateDirectory(ModsPath);
    }

    public string Root { get; }

    public string GameExecutable { get; }

    public string ModsPath { get; }

    public string AddMod(string folderName, string uniqueId)
    {
        var directory = Path.Combine(ModsPath, folderName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            $$"""{"Name":"{{folderName}}","Author":"tester","Version":"1.0.0","UniqueID":"{{uniqueId}}"}""");
        return directory;
    }

    public GameInstallation ToInstallation() => new()
    {
        RootPath = Root,
        ExecutablePath = GameExecutable,
        GameVersion = "1.6.15",
        Platform = GamePlatform.Windows,
        Distribution = GameDistribution.Steam
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }
}
