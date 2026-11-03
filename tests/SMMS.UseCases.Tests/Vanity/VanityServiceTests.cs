using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using SMMS.UseCases.Tests.Fakes;
using SMMS.UseCases.Vanity;

namespace SMMS.UseCases.Tests.Vanity;

public sealed class VanityServiceTests
{
    private static (VanityService Service, TempGameDirectory Game) CreateSut()
    {
        var game = new TempGameDirectory();
        Directory.CreateDirectory(Path.Combine(game.Root, "Content"));
        File.WriteAllText(Path.Combine(game.Root, "Content", "Abigail.xnb"), "ORIGINAL");
        File.WriteAllText(Path.Combine(game.Root, "Content", "Penny.xnb"), "ORIGINAL");

        return (new VanityService(NullLogger<VanityService>.Instance), game);
    }

    /// <summary>造一个带嵌套目录（包名/包名/Content/…）的压缩包，模拟作者的真实交付结构。</summary>
    private static string CreatePackageZip(string moddedContent, string file = "Abigail.xnb")
    {
        var path = Path.Combine(Path.GetTempPath(), "vanity-" + Guid.NewGuid().ToString("N") + ".zip");

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry($"某美化包/某美化包/Content/{file}");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(moddedContent);

        return path;
    }

    private static string ReadContent(TempGameDirectory game, string file)
        => File.ReadAllText(Path.Combine(game.Root, "Content", file));

    [Fact]
    public void Import_FindsContentFolderInsideNestedDirectories()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var zip = CreatePackageZip("MODDED");

        try
        {
            var result = service.Import(game.ToInstallation(), zip);

            Assert.True(result.Success);
            Assert.NotNull(result.Package);
            Assert.Equal(1, result.FileCount);
            Assert.Single(service.List(game.ToInstallation()));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Apply_OverwritesContentAndKeepsBaseline()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var zip = CreatePackageZip("MODDED");

        try
        {
            var imported = service.Import(game.ToInstallation(), zip);
            var applied = service.Apply(game.ToInstallation(), imported.Package!.Id);

            Assert.True(applied.Success);
            Assert.Equal("MODDED", ReadContent(game, "Abigail.xnb"));

            var state = service.GetState(game.ToInstallation());
            Assert.True(state.IsActive);
            Assert.Single(state.AppliedFiles);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Revert_RestoresOriginalContent()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var zip = CreatePackageZip("MODDED");

        try
        {
            var imported = service.Import(game.ToInstallation(), zip);
            service.Apply(game.ToInstallation(), imported.Package!.Id);

            var reverted = service.Revert(game.ToInstallation());

            Assert.True(reverted.Success);
            Assert.Equal("ORIGINAL", ReadContent(game, "Abigail.xnb"));
            Assert.False(service.GetState(game.ToInstallation()).IsActive);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void SwitchingPackages_KeepsBaselineAsOriginal()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var zipA = CreatePackageZip("PACK_A");
        var zipB = CreatePackageZip("PACK_B");

        try
        {
            var a = service.Import(game.ToInstallation(), zipA);
            var b = service.Import(game.ToInstallation(), zipB);

            service.Apply(game.ToInstallation(), a.Package!.Id);
            Assert.Equal("PACK_A", ReadContent(game, "Abigail.xnb"));

            // 切到 B：应先还原原版再覆盖 B，baseline 不能被 A 污染。
            service.Apply(game.ToInstallation(), b.Package!.Id);
            Assert.Equal("PACK_B", ReadContent(game, "Abigail.xnb"));

            service.Revert(game.ToInstallation());
            Assert.Equal("ORIGINAL", ReadContent(game, "Abigail.xnb"));
        }
        finally
        {
            File.Delete(zipA);
            File.Delete(zipB);
        }
    }

    [Fact]
    public void Delete_RejectsActivePackage()
    {
        var (service, game) = CreateSut();
        using var _game = game;
        var zip = CreatePackageZip("MODDED");

        try
        {
            var imported = service.Import(game.ToInstallation(), zip);
            service.Apply(game.ToInstallation(), imported.Package!.Id);

            var result = service.Delete(game.ToInstallation(), imported.Package.Id);

            Assert.False(result.Success);
            Assert.Equal("vanity.error.deleteActive", result.ErrorKey);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Import_RejectsArchiveWithoutContent()
    {
        var (service, game) = CreateSut();
        using var _game = game;

        var path = Path.Combine(Path.GetTempPath(), "empty-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("nothing here");
        }

        try
        {
            var result = service.Import(game.ToInstallation(), path);

            Assert.False(result.Success);
            Assert.Equal("vanity.error.noContent", result.ErrorKey);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
