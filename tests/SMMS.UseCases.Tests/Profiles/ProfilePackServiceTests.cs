using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using SMMS.UseCases.Game;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Tests.Fakes;

namespace SMMS.UseCases.Tests.Profiles;

public sealed class ProfilePackServiceTests
{
    private static (ProfilePackService Packs, ProfileService Profiles, TempGameDirectory Game, string ZipPath) CreateSut()
    {
        var game = new TempGameDirectory();
        var locator = new StubGameLocator { Installation = game.ToInstallation() };
        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();

        var scanner = new DirectoryModScanner();
        var profiles = new ProfileService(games, scanner, new InMemoryProfileManifestStore(), locator, NullLogger<ProfileService>.Instance);
        var packs = new ProfilePackService(games, profiles, scanner, NullLogger<ProfilePackService>.Instance);

        var zip = Path.Combine(Path.GetTempPath(), "smms-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        return (packs, profiles, game, zip);
    }

    [Fact]
    public async Task Export_WritesManifestAndReadmeOnly()
    {
        var (packs, profiles, game, zip) = CreateSut();
        using var _game = game;

        try
        {
            game.AddMod("Automate", "Pathoschild.Automate");
            var profile = (await profiles.CreateAsync("导出档", copyFromActive: true)).Profile!;

            var result = await packs.ExportAsync(profile, includeMods: false, zip);

            Assert.True(result.Success);
            Assert.Equal(1, result.ModCount);

            using var archive = ZipFile.OpenRead(zip);
            Assert.Contains(archive.Entries, entry => entry.FullName == "profile.json");
            Assert.Contains(archive.Entries, entry => entry.FullName == "README.md");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("mods/", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(zip);
        }
    }

    [Fact]
    public async Task ExportWithMods_ThenImport_RestoresFiles()
    {
        var (packs, profiles, game, zip) = CreateSut();
        using var _game = game;

        try
        {
            game.AddMod("Automate", "Pathoschild.Automate");
            var profile = (await profiles.CreateAsync("源档", copyFromActive: true)).Profile!;

            await packs.ExportAsync(profile, includeMods: true, zip);

            // 删掉原档，模拟换一台机器导入。
            profiles.Delete(profile.Id);

            var imported = await packs.ImportAsync(zip);

            Assert.True(imported.Success);
            Assert.NotNull(imported.Profile);
            Assert.Equal(1, imported.RestoredMods);
            Assert.Empty(imported.MissingMods);
            Assert.True(File.Exists(Path.Combine(imported.Profile!.ModsPath, "Automate", "manifest.json")));
        }
        finally
        {
            TryDelete(zip);
        }
    }

    [Fact]
    public async Task ImportWithoutModsFolder_ListsMissingMods()
    {
        var (packs, profiles, game, zip) = CreateSut();
        using var _game = game;

        try
        {
            game.AddMod("Automate", "Pathoschild.Automate");
            var profile = (await profiles.CreateAsync("清单档", copyFromActive: true)).Profile!;

            await packs.ExportAsync(profile, includeMods: false, zip);
            profiles.Delete(profile.Id);

            var imported = await packs.ImportAsync(zip);

            Assert.True(imported.Success);
            Assert.Equal(0, imported.RestoredMods);
            Assert.Single(imported.MissingMods);
            Assert.Contains("Pathoschild.Automate", imported.MissingMods[0]);
        }
        finally
        {
            TryDelete(zip);
        }
    }

    [Fact]
    public async Task Import_RejectsInvalidArchive()
    {
        var (packs, _, game, zip) = CreateSut();
        using var _game = game;

        try
        {
            await File.WriteAllTextAsync(zip, "this is not a zip");

            var result = await packs.ImportAsync(zip);

            Assert.False(result.Success);
            Assert.NotNull(result.ErrorKey);
        }
        finally
        {
            TryDelete(zip);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时包清理失败不影响测试结论。
        }
    }
}
