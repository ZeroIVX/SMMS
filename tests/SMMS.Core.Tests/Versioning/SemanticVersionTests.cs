using SMMS.Core.Versioning;

namespace SMMS.Core.Tests.Versioning;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.6.0", 1, 6, 0)]
    [InlineData("2.1", 2, 1, 0)]
    [InlineData("1.0.0.4", 1, 0, 0)]
    [InlineData("0.0.1", 0, 0, 1)]
    public void Parse_ReadsNumericSegments(string input, int major, int minor, int patch)
    {
        var version = SemanticVersion.Parse(input);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.False(version.IsPrerelease);
    }

    [Fact]
    public void Parse_SeparatesPrereleaseAndBuildMetadata()
    {
        var version = SemanticVersion.Parse("1.0.0-beta.5+build.42");

        Assert.True(version.IsPrerelease);
        Assert.Equal(["beta", "5"], version.PrereleaseIdentifiers);
        Assert.Equal("build.42", version.BuildMetadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2")]
    [InlineData("1.0.0-")]
    [InlineData("-1.0.0")]
    public void TryParse_RejectsMalformedInput(string input)
    {
        Assert.False(SemanticVersion.TryParse(input, out _));
    }

    [Fact]
    public void Compare_StableIsGreaterThanPrerelease()
    {
        var stable = SemanticVersion.Parse("1.0.0");
        var beta = SemanticVersion.Parse("1.0.0-beta.5");

        Assert.True(stable > beta);
        Assert.True(beta < stable);
    }

    [Fact]
    public void Compare_NumericIdentifierIsLowerThanAlphanumeric()
    {
        var numeric = SemanticVersion.Parse("1.0.0-alpha.1");
        var mixed = SemanticVersion.Parse("1.0.0-alpha.beta");

        Assert.True(numeric < mixed);
    }

    [Fact]
    public void Compare_MorePrereleaseSegmentsWinWhenPrefixEqual()
    {
        var shorter = SemanticVersion.Parse("1.0.0-beta");
        var longer = SemanticVersion.Parse("1.0.0-beta.1");

        Assert.True(shorter < longer);
    }

    [Fact]
    public void Compare_MissingSegmentsDefaultToZero()
    {
        var sparse = SemanticVersion.Parse("1.0");
        var explicitVersion = SemanticVersion.Parse("1.0.0");

        Assert.Equal(0, sparse.CompareTo(explicitVersion));
        Assert.Equal(sparse.GetHashCode(), explicitVersion.GetHashCode());
        Assert.True(sparse == explicitVersion);
    }

    [Fact]
    public void Compare_OrdersRealWorldModVersions()
    {
        string[] ordered = ["1.6.8", "1.6.9", "1.6.10", "1.7.0", "2.0.0-beta.1", "2.0.0"];

        var versions = ordered.Select(SemanticVersion.Parse).ToList();
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i] > versions[i - 1], $"{versions[i]} 应大于 {versions[i - 1]}");
        }
    }

    [Fact]
    public void Compare_BuildMetadataIsIgnored()
    {
        var withBuild = SemanticVersion.Parse("1.2.3+build.1");
        var withoutBuild = SemanticVersion.Parse("1.2.3");

        Assert.True(withBuild == withoutBuild);
    }

    [Fact]
    public void TryParseLenient_AcceptsVersionPrefix()
    {
        Assert.True(SemanticVersion.TryParseLenient("v1.6.15", out var version));
        Assert.Equal(1, version.Major);
        Assert.Equal(15, version.Patch);
    }

    [Fact]
    public void ToString_RestoresOriginalShape()
    {
        Assert.Equal("1.6.0-beta.2", SemanticVersion.Parse("1.6.0-beta.2").ToString());
    }
}
