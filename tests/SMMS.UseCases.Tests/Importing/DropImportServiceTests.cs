using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using SMMS.UseCases.Game;
using SMMS.UseCases.Importing;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Snapshots;
using SMMS.UseCases.Tests.Fakes;
using SMMS.UseCases.Vanity;

namespace SMMS.UseCases.Tests.Importing;

public sealed class DropImportServiceTests
{
    private static (DropImportService Service, TempGameDirectory Game) CreateSut()
    {
        var game = new TempGameDirectory();
        var locator = new StubGameLocator { Installation = game.ToInstallation() };
        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();

        var scanner = new DirectoryModScanner();
        var profiles = new ProfileService(games, scanner, new InMemoryProfileManifestStore(), locator, NullLogger<ProfileService>.Instance);
        var vanity = new VanityService(NullLogger<VanityService>.Instance);
        var packs = new ProfilePackService(games, profiles, scanner, NullLogger<ProfilePackService>.Instance);
        var library = new ModLibraryService(scanner);
        var toggle = new ModToggleService(games, NullLogger<ModToggleService>.Instance);
        var snapshots = new SnapshotService(games, NullLogger<SnapshotService>.Instance);

        var service = new DropImportService(
            profiles, games, vanity, packs, library, toggle, snapshots, NullLogger<DropImportService>.Instance);

        return (service, game);
    }

    private static string BuildZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "drop-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }

    [Fact]
    public async Task Import_Mod_ExtractsIntoModsFolder()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var zip = BuildZip(
            ("TestMod/manifest.json", "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\"}"),
            ("TestMod/Test.dll", "binary"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.True(outcome.Success);
            Assert.Equal(DropEffects.Mods, outcome.Effects);
            Assert.True(File.Exists(Path.Combine(game.ModsPath, "TestMod", "manifest.json")));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Mod_SkipsExistingFolder()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        game.AddMod("TestMod", "test.mod");

        var zip = BuildZip(("TestMod/manifest.json", "{\"UniqueID\":\"test.mod\"}"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.True(outcome.Success);
            // 同 UniqueID 视为更新，即使版本没变也会覆盖。
            Assert.Equal("drop.success.modsMixed", outcome.MessageKey);
            Assert.Equal("1", outcome.Arguments[1]);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Mod_UpdatesByUniqueIdEvenIfFolderRenamed()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var installed = game.AddMod("OldFolderName", "test.mod");
        File.WriteAllText(Path.Combine(installed, "manifest.json"),
            "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(installed, "config.json"), "{\"HpBars\":true}");

        // 新版本换了文件夹名，靠目录名匹配就会变成"新增一份"，这里必须按 UniqueID 认出来。
        var zip = BuildZip(
            ("NewFolderName/manifest.json", "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"2.0.0\"}"),
            ("NewFolderName/Test.dll", "new-binary"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.True(outcome.Success);
            Assert.Equal("1", outcome.Arguments[1]);   // updated

            // 旧目录被覆盖，且在备份目录里留了一份
            Assert.False(Directory.Exists(Path.Combine(game.ModsPath, "NewFolderName")));
            Assert.True(File.Exists(Path.Combine(installed, "Test.dll")));

            // 玩家自己的配置必须还在
            Assert.True(File.Exists(Path.Combine(installed, "config.json")));
            Assert.Contains("HpBars", File.ReadAllText(Path.Combine(installed, "config.json")));

            // 更新前必须留一份快照，否则改坏了无法整批回滚
            Assert.NotEmpty(Directory.EnumerateFiles(game.Root, "snapshot.json", SearchOption.AllDirectories));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Vanity_RoutesToVanityLibrary()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        Directory.CreateDirectory(Path.Combine(game.Root, "Content"));
        File.WriteAllText(Path.Combine(game.Root, "Content", "Abigail.xnb"), "ORIGINAL");

        var zip = BuildZip(("美化/Content/Abigail.xnb", "MODDED"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.True(outcome.Success);
            Assert.Equal(DropEffects.Vanity, outcome.Effects);
            // 导入阶段不应改动游戏资源，应用是单独的动作。
            Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(game.Root, "Content", "Abigail.xnb")));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_UnrecognizedArchive_ReportsFailure()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var zip = BuildZip(("readme.txt", "nothing useful"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.False(outcome.Success);
            Assert.Equal("drop.error.unknown", outcome.MessageKey);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Downgrade_AsksForConfirmationWithoutTouchingFiles()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var installed = game.AddMod("TestMod", "test.mod");
        File.WriteAllText(
            Path.Combine(installed, "manifest.json"),
            "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"2.0.0\"}");

        var zip = BuildZip(
            ("TestMod/manifest.json", "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"1.0.0\"}"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.False(outcome.Success);
            Assert.True(outcome.NeedsDowngradeConfirmation);
            Assert.Single(outcome.Downgrades!);

            // 未确认前不能先执行一半
            Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(installed, "manifest.json")));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Downgrade_ProceedsAfterConfirmation()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var installed = game.AddMod("TestMod", "test.mod");
        File.WriteAllText(
            Path.Combine(installed, "manifest.json"),
            "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"2.0.0\"}");

        var zip = BuildZip(
            ("TestMod/manifest.json", "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"1.0.0\"}"));

        try
        {
            var outcome = await service.ImportAsync(zip, allowDowngrade: true);

            Assert.True(outcome.Success);
            Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(installed, "manifest.json")));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public async Task Import_Upgrade_DoesNotAskForConfirmation()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var installed = game.AddMod("TestMod", "test.mod");
        File.WriteAllText(
            Path.Combine(installed, "manifest.json"),
            "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"1.0.0\"}");

        var zip = BuildZip(
            ("TestMod/manifest.json", "{\"UniqueID\":\"test.mod\",\"Name\":\"Test\",\"Version\":\"2.0.0\"}"));

        try
        {
            var outcome = await service.ImportAsync(zip);

            Assert.True(outcome.Success);
            Assert.False(outcome.NeedsDowngradeConfirmation);
            Assert.Contains("2.0.0", File.ReadAllText(Path.Combine(installed, "manifest.json")));
        }
        finally
        {
            File.Delete(zip);
        }
    }
}
