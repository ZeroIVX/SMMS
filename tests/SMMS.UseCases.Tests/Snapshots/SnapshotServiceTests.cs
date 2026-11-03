using Microsoft.Extensions.Logging.Abstractions;
using SMMS.Core.Profiles;
using SMMS.Core.Snapshots;
using SMMS.UseCases.Game;
using SMMS.UseCases.Snapshots;
using SMMS.UseCases.Tests.Fakes;

namespace SMMS.UseCases.Tests.Snapshots;

public sealed class SnapshotServiceTests
{
    private static (SnapshotService Service, TempGameDirectory Game) CreateSut()
    {
        var game = new TempGameDirectory();
        var locator = new StubGameLocator { Installation = game.ToInstallation() };
        var games = new GameInstallationService(locator, new InMemorySettingsStore(), NullLogger<GameInstallationService>.Instance);
        games.Resolve();
        return (new SnapshotService(games, NullLogger<SnapshotService>.Instance), game);
    }

    [Fact]
    public void Create_WritesSnapshotWithMetadata()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        game.AddMod("Automate", "pathoschild.automate");

        var profile = ModProfile.CreateDefault(game.Root);
        var result = service.Create(profile, "测试备份");

        Assert.True(result.Success);

        var list = service.List(profile.Id);
        var record = Assert.Single(list);
        Assert.Equal("测试备份", record.Label);
        Assert.Equal(1, record.ModCount);
        Assert.False(record.IsAutomatic);
    }

    [Fact]
    public void Create_SecondSnapshotLinksUnchangedFiles()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        // 造一个有点体积的文件，让"是否重复占空间"可测
        var modDir = game.AddMod("Automate", "pathoschild.automate");
        File.WriteAllBytes(Path.Combine(modDir, "big.bin"), new byte[256 * 1024]);

        var profile = ModProfile.CreateDefault(game.Root);

        Assert.True(service.Create(profile, "第一份").Success);
        Assert.True(service.Create(profile, "第二份").Success);

        // List 是时间倒序，[0] 是最新那份
        var records = service.List(profile.Id);
        Assert.Equal(2, records.Count);

        // 内容没变时第二份应全部走硬链接，新增占用接近 0
        Assert.True(
            records[0].SizeBytes * 4 < records[1].SizeBytes,
            $"第二份应明显更小：{records[0].SizeBytes} vs {records[1].SizeBytes}");
    }

    [Fact]
    public void Prune_KeepsOnlyLatestPreLaunchSnapshot()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        game.AddMod("Automate", "pathoschild.automate");

        var profile = ModProfile.CreateDefault(game.Root);

        service.Create(profile, "启动前 1", SnapshotKind.PreLaunch);
        service.Create(profile, "启动前 2", SnapshotKind.PreLaunch);
        service.Create(profile, "启动前 3", SnapshotKind.PreLaunch);
        service.Create(profile, "手动", SnapshotKind.Manual);

        service.Prune(profile.Id);

        var records = service.List(profile.Id);
        Assert.Single(records, record => record.EffectiveKind == SnapshotKind.PreLaunch);
        Assert.Single(records, record => record.EffectiveKind == SnapshotKind.Manual);
    }

    [Fact]
    public void Restore_ReplacesModsAndKeepsGuardSnapshot()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var modDirectory = game.AddMod("Automate", "pathoschild.automate");

        var profile = ModProfile.CreateDefault(game.Root);
        var created = service.Create(profile, "初始状态");
        Assert.True(created.Success);

        var snapshotId = service.List(profile.Id)[0].Id;

        // 模拟后续改动：把模组删掉
        Directory.Delete(modDirectory, recursive: true);
        Assert.Empty(Directory.GetDirectories(game.ModsPath));

        var restored = service.Restore(profile, snapshotId);

        Assert.True(restored.Success);
        Assert.True(File.Exists(Path.Combine(game.ModsPath, "Automate", "manifest.json")));

        // 恢复前必须自动留一份保护快照，否则「误恢复」就再也回不去。
        var list = service.List(profile.Id);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, record => record.IsAutomatic);
    }

    [Fact]
    public void Restore_Fails_WhenSnapshotMissing()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var profile = ModProfile.CreateDefault(game.Root);

        var result = service.Restore(profile, "不存在的快照");

        Assert.False(result.Success);
        Assert.Equal("snapshots.error.notFound", result.ErrorKey);
    }

    [Fact]
    public void Delete_RemovesSnapshotFromList()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        game.AddMod("Automate", "pathoschild.automate");

        var profile = ModProfile.CreateDefault(game.Root);
        service.Create(profile, "待删除");
        var snapshotId = service.List(profile.Id)[0].Id;

        var result = service.Delete(profile.Id, snapshotId);

        Assert.True(result.Success);
        Assert.Empty(service.List(profile.Id));
    }

    [Fact]
    public void List_ReturnsNewestFirst()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        game.AddMod("Automate", "pathoschild.automate");

        var profile = ModProfile.CreateDefault(game.Root);
        service.Create(profile, "第一次");
        Thread.Sleep(15);
        service.Create(profile, "第二次");

        var list = service.List(profile.Id);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt);
    }
}
