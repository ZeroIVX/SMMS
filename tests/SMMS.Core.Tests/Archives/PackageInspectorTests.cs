using System.IO.Compression;
using SMMS.Core.Archives;

namespace SMMS.Core.Tests.Archives;

public sealed class PackageInspectorTests
{
    private static string BuildZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "inspect-" + Guid.NewGuid().ToString("N") + ".zip");

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
    public void Inspect_DetectsVanityPackWithoutManifest()
    {
        var zip = BuildZip(
            ("美化/Content/Characters/Abigail.xnb", "binary"),
            ("美化/说明.txt", "readme"));

        try
        {
            Assert.Equal(PackageKind.Vanity, PackageInspector.Inspect(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Inspect_ContentPatcherPackIsTreatedAsMod()
    {
        // CP 美化包同时带 manifest.json 和 xnb 素材，必须判定为模组，否则会被错误地覆盖进 Content。
        var zip = BuildZip(
            ("SomePack/manifest.json", "{\"UniqueID\":\"x\"}"),
            ("SomePack/content.json", "{}"),
            ("SomePack/assets/Abigail.xnb", "binary"));

        try
        {
            Assert.Equal(PackageKind.Mod, PackageInspector.Inspect(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Inspect_DetectsCodeMod()
    {
        var zip = BuildZip(
            ("Automate/manifest.json", "{\"UniqueID\":\"Pathoschild.Automate\"}"),
            ("Automate/Automate.dll", "binary"));

        try
        {
            Assert.Equal(PackageKind.Mod, PackageInspector.Inspect(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Inspect_DetectsProfilePack()
    {
        var zip = BuildZip(
            ("profile.json", "{}"),
            ("README.md", "# pack"));

        try
        {
            Assert.Equal(PackageKind.ProfilePack, PackageInspector.Inspect(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }

    [Fact]
    public void Inspect_ReturnsUnknownForIrrelevantArchive()
    {
        var zip = BuildZip(("readme.txt", "nothing"));

        try
        {
            Assert.Equal(PackageKind.Unknown, PackageInspector.Inspect(zip));
        }
        finally
        {
            File.Delete(zip);
        }
    }
}
