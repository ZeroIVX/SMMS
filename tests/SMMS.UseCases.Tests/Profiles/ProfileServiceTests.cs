using Microsoft.Extensions.Logging.Abstractions;
using SMMS.Core.Profiles;
using SMMS.UseCases.Game;
using SMMS.UseCases.Profiles;
using SMMS.UseCases.Tests.Fakes;

namespace SMMS.UseCases.Tests.Profiles;

public sealed class ProfileServiceTests
{
    private static (ProfileService Service, GameInstallationService Games, TempGameDirectory Game, InMemoryProfileManifestStore Manifests) CreateSut()
    {
        var game = new TempGameDirectory();
        var locator = new StubGameLocator { Installation = game.ToInstallation() };
        var store = new InMemorySettingsStore();
        var games = new GameInstallationService(locator, store, NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        var manifests = new InMemoryProfileManifestStore();
        var service = new ProfileService(games, new EmptyModScanner(), manifests, locator, NullLogger<ProfileService>.Instance);
        return (service, games, game, manifests);
    }

    [Fact]
    public async Task CreateAsync_CreatesFolderAndActivates()
    {
        var (service, games, game, _) = CreateSut();
        using var _game = game;

        var result = await service.CreateAsync("单人档", copyFromActive: false);

        Assert.Null(result.ErrorKey);
        Assert.NotNull(result.Profile);
        Assert.True(Directory.Exists(result.Profile!.ModsPath));
        Assert.Equal(result.Profile.Id, games.Settings.ActiveProfileId);
        Assert.Single(games.Settings.ProfileRecords);
    }

    [Fact]
    public async Task CreateAsync_WithCopy_CopiesMods()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;
        game.AddMod("ContentPatcher", "Pathoschild.ContentPatcher");

        var result = await service.CreateAsync("复制档", copyFromActive: true);

        Assert.NotNull(result.Profile);
        Assert.True(File.Exists(Path.Combine(result.Profile!.ModsPath, "ContentPatcher", "manifest.json")));
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptyName()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var result = await service.CreateAsync("   ", copyFromActive: false);

        Assert.Equal("profiles.error.emptyName", result.ErrorKey);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateName()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        await service.CreateAsync("重复档", copyFromActive: false);
        var second = await service.CreateAsync("重复档", copyFromActive: false);

        Assert.Equal("profiles.error.duplicate", second.ErrorKey);
    }

    [Fact]
    public void GetAll_AlwaysContainsDefaultProfile()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var all = service.GetAll();

        Assert.Single(all);
        Assert.Equal("default", all[0].Id);
        Assert.Equal(game.ModsPath, all[0].ModsPath);
    }

    [Fact]
    public async Task CreateAsync_WritesManifest()
    {
        var (service, _, game, manifests) = CreateSut();
        using var _game = game;

        var result = await service.CreateAsync("清单档", copyFromActive: false);

        Assert.NotNull(result.Profile);
        var directory = Path.GetDirectoryName(result.Profile!.ModsPath)!;
        Assert.True(manifests.Written.ContainsKey(directory));

        var manifest = manifests.Written[directory];
        Assert.Equal(result.Profile.Id, manifest.Id);
        Assert.Equal("清单档", manifest.Name);
        Assert.Equal("1.6.15", manifest.Requirements.GameVersion);
        Assert.False(manifest.IsolateGameInstance);
    }

    [Fact]
    public void SyncManifest_SkipsBuiltInProfile()
    {
        var (service, _, game, manifests) = CreateSut();
        using var _game = game;

        service.SyncManifest(SMMS.Core.Profiles.ModProfile.CreateDefault(game.Root));

        Assert.Empty(manifests.Written);
    }

    [Fact]
    public async Task SetActive_SwitchesActiveProfile()
    {
        var (service, games, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("联机档", copyFromActive: false);
        Assert.True(service.SetActive("default"));

        Assert.Equal("default", games.Settings.ActiveProfileId);
        Assert.Equal("default", service.GetActive()?.Id);
        Assert.NotEqual("default", created.Profile!.Id);
    }

    [Fact]
    public async Task Delete_RemovesDirectoryAndRecord()
    {
        var (service, games, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("待删除", copyFromActive: false);
        var directory = Path.GetDirectoryName(created.Profile!.ModsPath)!;
        Assert.True(Directory.Exists(directory));

        var result = service.Delete(created.Profile.Id);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(games.Settings.ProfileRecords);
    }

    [Fact]
    public void Delete_RejectsBuiltInProfile()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var result = service.Delete("default");

        Assert.False(result.Success);
        Assert.Equal("profiles.error.builtin", result.ErrorKey);
    }

    [Fact]
    public async Task Delete_ActiveProfile_FallsBackToDefault()
    {
        var (service, games, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("激活档", copyFromActive: false);
        Assert.Equal(created.Profile!.Id, games.Settings.ActiveProfileId);

        service.Delete(created.Profile.Id);

        Assert.Equal("default", games.Settings.ActiveProfileId);
    }

    [Fact]
    public async Task Rename_UpdatesRecordName()
    {
        var (service, games, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("旧名", copyFromActive: false);
        var result = service.Rename(created.Profile!.Id, "新名");

        Assert.True(result.Success);
        Assert.Equal("新名", games.Settings.ProfileRecords[0].Name);
    }

    [Fact]
    public async Task Rename_RejectsDuplicateName()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        await service.CreateAsync("甲", copyFromActive: false);
        var second = await service.CreateAsync("乙", copyFromActive: false);

        var result = service.Rename(second.Profile!.Id, "甲");

        Assert.False(result.Success);
        Assert.Equal("profiles.error.duplicate", result.ErrorKey);
    }

    [Fact]
    public void Rename_RejectsBuiltInProfile()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var result = service.Rename("default", "新默认");

        Assert.False(result.Success);
        Assert.Equal("profiles.error.builtin", result.ErrorKey);
    }

    [Fact]
    public async Task CreateAsync_FullInstance_CopiesGameButSkipsMods()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;
        game.AddMod("ExistingMod", "existing.mod");

        var result = await service.CreateAsync("完整实例", copyFromActive: false, ProfileIsolation.FullInstance);

        Assert.Null(result.ErrorKey);
        Assert.NotNull(result.Profile);
        Assert.True(result.Profile!.Isolation.IsolateGameInstance);
        Assert.NotNull(result.Profile.InstanceRootPath);

        var instanceRoot = result.Profile.InstanceRootPath!;
        Assert.True(File.Exists(Path.Combine(instanceRoot, "Stardew Valley.exe")));
        Assert.Equal(Path.Combine(instanceRoot, "Mods"), result.Profile.ModsPath);

        // 原游戏的 Mods 不应被带进副本，实例的模组由配置档自己决定。
        Assert.False(Directory.Exists(Path.Combine(instanceRoot, "Mods", "ExistingMod")));
    }

    [Fact]
    public async Task GetAll_ResolvesInstanceRootForFullProfile()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("完整实例", copyFromActive: false, ProfileIsolation.FullInstance);
        var resolved = service.GetAll().FirstOrDefault(item => item.Id == created.Profile!.Id);

        Assert.NotNull(resolved);
        Assert.True(resolved!.HasOwnInstance);
        Assert.Equal(created.Profile!.InstanceRootPath, resolved.InstanceRootPath);
        Assert.True(resolved.Isolation.IsolateGameInstance);
    }

    [Fact]
    public async Task Delete_FullInstance_RemovesEntireProfileRoot()
    {
        var (service, _, game, _) = CreateSut();
        using var _game = game;

        var created = await service.CreateAsync("完整实例", copyFromActive: false, ProfileIsolation.FullInstance);
        var instanceRoot = created.Profile!.InstanceRootPath!;
        Assert.True(Directory.Exists(instanceRoot));

        var result = service.Delete(created.Profile.Id);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(instanceRoot));
    }

    [Fact]
    public void AdoptExternal_LinksExistingGameDirectory()
    {
        var mainGame = new TempGameDirectory();
        using var _main = mainGame;
        using var external = new TempGameDirectory();
        external.AddMod("LegacyMod", "legacy.mod");

        var locator = new StubGameLocator { Installation = mainGame.ToInstallation() };
        locator.ExtraInstallations.Add(external.ToInstallation());

        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        var service = new ProfileService(games, new DirectoryModScanner(), new InMemoryProfileManifestStore(), locator, NullLogger<ProfileService>.Instance);

        var result = service.AdoptExternal("旧备份", external.Root);

        Assert.Null(result.ErrorKey);
        Assert.NotNull(result.Profile);
        Assert.Equal(Path.Combine(external.Root, "Mods"), result.Profile!.ModsPath);
        Assert.True(result.Profile.IsExternal);
        Assert.Equal(external.Root, result.Profile.InstanceRootPath);
    }

    [Fact]
    public void AdoptExternal_RejectsDirectoryThatIsNotAGame()
    {
        var mainGame = new TempGameDirectory();
        using var _main = mainGame;

        var locator = new StubGameLocator { Installation = mainGame.ToInstallation() };
        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        var service = new ProfileService(games, new EmptyModScanner(), new InMemoryProfileManifestStore(), locator, NullLogger<ProfileService>.Instance);

        var result = service.AdoptExternal("不是游戏", Path.GetTempPath());

        Assert.Equal("profiles.error.invalidGameDir", result.ErrorKey);
    }

    [Fact]
    public void Delete_ExternalProfile_KeepsGameDirectoryIntact()
    {
        var mainGame = new TempGameDirectory();
        using var _main = mainGame;
        using var external = new TempGameDirectory();
        external.AddMod("LegacyMod", "legacy.mod");

        var locator = new StubGameLocator { Installation = mainGame.ToInstallation() };
        locator.ExtraInstallations.Add(external.ToInstallation());

        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        var service = new ProfileService(games, new DirectoryModScanner(), new InMemoryProfileManifestStore(), locator, NullLogger<ProfileService>.Instance);

        var adopted = service.AdoptExternal("旧备份", external.Root);
        Assert.NotNull(adopted.Profile);

        var result = service.Delete(adopted.Profile!.Id);

        Assert.True(result.Success);

        // 关键：外部目录属于玩家，删配置档只解绑，不能带走一个文件。
        Assert.True(Directory.Exists(external.Root));
        Assert.True(File.Exists(Path.Combine(external.ModsPath, "LegacyMod", "manifest.json")));
    }
}
