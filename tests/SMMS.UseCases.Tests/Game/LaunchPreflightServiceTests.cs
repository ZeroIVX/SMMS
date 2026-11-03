using Microsoft.Extensions.Logging.Abstractions;
using SMMS.Core.Game;
using SMMS.Core.Mods;
using SMMS.Core.Profiles;
using SMMS.Core.Versioning;
using SMMS.UseCases.Game;
using SMMS.UseCases.Tests.Fakes;

namespace SMMS.UseCases.Tests.Game;

public sealed class LaunchPreflightServiceTests
{
    private static GameInstallation Game(string? smapiVersion = "4.5.1") => new()
    {
        RootPath = @"C:\game",
        ExecutablePath = @"C:\game\Stardew Valley.exe",
        SmapiExecutablePath = smapiVersion is null ? null : @"C:\game\StardewModdingAPI.exe",
        GameVersion = "1.6.15",
        SmapiVersion = smapiVersion,
        Platform = GamePlatform.Windows
    };

    private static InstalledMod Mod(
        string uniqueId,
        string? minimumApiVersion = null,
        params ModDependencyRef[] dependencies) => new()
    {
        DirectoryPath = $@"C:\game\Mods\{uniqueId}",
        FolderName = uniqueId,
        UniqueId = uniqueId,
        Name = uniqueId,
        Version = SemanticVersion.Parse("1.0.0"),
        MinimumApiVersion = minimumApiVersion is null ? null : SemanticVersion.Parse(minimumApiVersion),
        Dependencies = dependencies
    };

    private static LaunchPreflightService CreateSut(ModScanResult? scan = null)
        => new(new EmptyModScanner { Result = scan ?? ModScanResult.Empty }, NullLogger<LaunchPreflightService>.Instance);

    private static ModProfile Profile() => new()
    {
        Id = "p1",
        Name = "test",
        ModsPath = Path.GetTempPath()
    };

    [Fact]
    public void Check_Clean_WhenEverythingIsFine()
    {
        var result = CreateSut().Check(Game(), Profile(), LaunchMode.Smapi, isGameRunning: false);

        Assert.Empty(result.Issues);
        Assert.False(result.NeedsConfirmation);
    }

    [Fact]
    public void Check_Errors_WhenSmapiMissingButSmapiModeRequested()
    {
        var result = CreateSut().Check(Game(smapiVersion: null), Profile(), LaunchMode.Smapi, false);

        Assert.Contains(result.Issues, issue => issue.Code == "preflight.smapiMissing");
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Check_VanillaMode_DoesNotRequireSmapi()
    {
        var result = CreateSut().Check(Game(smapiVersion: null), Profile(), LaunchMode.Vanilla, false);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "preflight.smapiMissing");
    }

    [Fact]
    public void Check_Errors_WhenModRequiresNewerSmapi()
    {
        var scan = new ModScanResult([Mod("Some.Mod", minimumApiVersion: "4.6.0")], []);
        var result = CreateSut(scan).Check(Game("4.5.1"), Profile(), LaunchMode.Smapi, false);

        var issue = Assert.Single(result.Issues, i => i.Code == "preflight.smapiTooOld");
        Assert.Contains("Some.Mod", issue.Arguments[1]);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Check_Warns_WhenRequiredDependencyMissing()
    {
        var scan = new ModScanResult(
            [Mod("Pack.A", dependencies: new ModDependencyRef("Pathoschild.ContentPatcher", true, "2.0.0"))],
            []);

        var result = CreateSut(scan).Check(Game(), Profile(), LaunchMode.Smapi, false);

        var issue = Assert.Single(result.Issues, i => i.Code == "preflight.missingDependencies");
        Assert.Contains("Pathoschild.ContentPatcher", issue.Arguments[0]);
        Assert.False(result.HasErrors);
        Assert.True(result.HasWarning());
    }

    [Fact]
    public void Check_IgnoresOptionalDependencies()
    {
        var scan = new ModScanResult(
            [Mod("Pack.B", dependencies: new ModDependencyRef("Optional.Mod", false, null))],
            []);

        var result = CreateSut(scan).Check(Game(), Profile(), LaunchMode.Smapi, false);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "preflight.missingDependencies");
    }

    [Fact]
    public void Check_Errors_WhenGameRunning()
    {
        var result = CreateSut().Check(Game(), Profile(), LaunchMode.Smapi, isGameRunning: true);

        Assert.Contains(result.Issues, issue => issue.Code == "preflight.gameRunning");
    }

    [Fact]
    public void Check_Errors_WhenNoGameLocated()
    {
        var result = CreateSut().Check(null, Profile(), LaunchMode.Smapi, false);

        Assert.Single(result.Issues, issue => issue.Code == "preflight.noGame");
    }
}

internal static class PreflightResultTestExtensions
{
    public static bool HasWarning(this PreflightResult result)
        => result.Issues.Any(issue => issue.Severity == PreflightSeverity.Warning);
}
