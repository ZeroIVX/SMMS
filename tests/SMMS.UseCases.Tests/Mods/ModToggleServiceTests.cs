using Microsoft.Extensions.Logging.Abstractions;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Core.Versioning;
using SMMS.UseCases.Game;
using SMMS.UseCases.Mods;
using SMMS.UseCases.Tests.Fakes;

namespace SMMS.UseCases.Tests.Mods;

public sealed class ModToggleServiceTests
{
    private static (ModToggleService Service, TempGameDirectory Game) CreateSut()
    {
        var game = new TempGameDirectory();
        var locator = new StubGameLocator { Installation = game.ToInstallation() };
        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        return (new ModToggleService(games, NullLogger<ModToggleService>.Instance), game);
    }

    private static InstalledMod Mod(string folderName, string directoryPath) => new()
    {
        DirectoryPath = directoryPath,
        FolderName = folderName,
        UniqueId = folderName.ToLowerInvariant(),
        Name = folderName,
        Version = SemanticVersion.Parse("1.0.0")
    };

    [Fact]
    public void SetEnabled_MovesModToDisabledArea()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var profile = ModProfile.CreateDefault(game.Root);
        var modDirectory = game.AddMod("TestMod", "test.mod");

        var result = service.SetEnabled(profile, Mod("TestMod", modDirectory), enabled: false);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(Path.Combine(game.ModsPath, "TestMod")));
        Assert.True(Directory.Exists(Path.Combine(game.Root, "_smms", "disabled", "TestMod")));
    }

    [Fact]
    public void SetEnabled_MovesModBackToMods()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var profile = ModProfile.CreateDefault(game.Root);
        var modDirectory = game.AddMod("TestMod", "test.mod");
        service.SetEnabled(profile, Mod("TestMod", modDirectory), enabled: false);

        var disabledPath = Path.Combine(game.Root, "_smms", "disabled", "TestMod");
        var result = service.SetEnabled(profile, Mod("TestMod", disabledPath), enabled: true);

        Assert.True(result.Success);
        Assert.True(Directory.Exists(Path.Combine(game.ModsPath, "TestMod")));
        Assert.False(Directory.Exists(disabledPath));
    }

    [Fact]
    public void SetEnabled_Fails_WhenSourceMissing()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var profile = ModProfile.CreateDefault(game.Root);
        var result = service.SetEnabled(profile, Mod("Ghost", Path.Combine(game.ModsPath, "Ghost")), enabled: false);

        Assert.False(result.Success);
        Assert.Equal("mods.toggle.missing", result.ErrorKey);
    }

    [Fact]
    public void SetEnabled_Fails_WhenTargetAlreadyExists()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var profile = ModProfile.CreateDefault(game.Root);
        var modDirectory = game.AddMod("TestMod", "test.mod");

        // 预先在禁用区造一个同名目录，模拟历史残留。
        var disabled = Path.Combine(game.Root, "_smms", "disabled", "TestMod");
        Directory.CreateDirectory(disabled);

        var result = service.SetEnabled(profile, Mod("TestMod", modDirectory), enabled: false);

        Assert.False(result.Success);
        Assert.Equal("mods.toggle.conflict", result.ErrorKey);
        Assert.True(Directory.Exists(Path.Combine(game.ModsPath, "TestMod")));
    }

    [Fact]
    public void GetDisabledPath_CustomProfile_StaysInsideProfileFolder()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var profile = new ModProfile
        {
            Id = "p1",
            Name = "custom",
            ModsPath = Path.Combine(game.Root, "_smms", "profiles", "p1", "Mods")
        };

        var path = service.GetDisabledPath(profile);

        Assert.Equal(Path.Combine(game.Root, "_smms", "profiles", "p1", "_disabled"), path);
    }
}
