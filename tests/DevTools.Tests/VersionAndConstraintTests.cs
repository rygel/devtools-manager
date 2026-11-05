using DevTools.Core.Constraints;
using DevTools.Core.Model;
using DevTools.Core.Versioning;
using Xunit;

namespace DevTools.Tests;

public class VersionNumberTests
{
    [Theory]
    [InlineData("20.11.0", "20.11.0")]
    [InlineData("v20.11.0", "20.11.0")]
    [InlineData("20", "20")]
    [InlineData("21.0.2+13", "21.0.2")]
    [InlineData("temurin-21.0.2+13", "21.0.2")]
    [InlineData("1.8.0_292", "1.8.0.292")]
    [InlineData("3.12.0b1", "3.12.0-b1")]
    public void ParsesRealWorldVersionStrings(string input, string expected)
    {
        Assert.True(VersionNumber.TryParse(input, out var version));
        Assert.Equal(expected, version!.ToString());
    }

    [Fact]
    public void MavenOrderingIsNumericNotLexicographic()
    {
        var older = VersionNumber.Parse("3.9.9");
        var newer = VersionNumber.Parse("3.9.10");

        Assert.True(newer > older);
    }

    [Fact]
    public void PreReleaseSortsBeforeRelease()
    {
        Assert.True(VersionNumber.Parse("1.0.0-alpha") < VersionNumber.Parse("1.0.0"));
        Assert.True(VersionNumber.Parse("1.0.0-rc.1") < VersionNumber.Parse("1.0.0-rc.2"));
    }

    [Fact]
    public void TrailingZeroesAreInsignificant()
    {
        Assert.Equal(VersionNumber.Parse("8"), VersionNumber.Parse("8.0.0"));
        Assert.True(VersionNumber.Parse("8") == VersionNumber.Parse("8.0"));
    }

    [Fact]
    public void JavaLegacySchemeCollapsesToMajor()
    {
        var legacy = VersionNumber.Parse("1.8");
        var normalized = JavaVersions.Normalize(legacy);

        Assert.Equal(8, normalized.Major);
        Assert.Equal(8, JavaVersions.Major(VersionNumber.Parse("1.8.0_292")));
        Assert.Equal(21, JavaVersions.Major(VersionNumber.Parse("21.0.2")));
    }

    [Theory]
    [InlineData("JavaVersion.VERSION_1_8", 8)]
    [InlineData("JavaVersion.VERSION_17", 17)]
    [InlineData("1.8", 8)]
    [InlineData("21", 21)]
    public void ReadsGradleCompatibilityStrings(string input, int expectedMajor)
    {
        Assert.True(JavaVersions.TryParseCompatibility(input, out var version));
        Assert.Equal(expectedMajor, JavaVersions.Major(version!));
    }
}

public class ConstraintTests
{
    private static bool Satisfies(ToolKind tool, string raw, string version) =>
        ConstraintParser.Parse(tool, raw).Satisfies(VersionNumber.Parse(version), tool);

    [Theory]
    [InlineData("18", "18.20.2", true)]
    [InlineData("18", "20.11.0", false)]
    [InlineData("18.20.2", "18.20.2", true)]
    [InlineData("18.20.2", "18.20.3", false)]
    [InlineData("20.x", "20.11.0", true)]
    [InlineData("20.x", "21.0.0", false)]
    public void NodePins(string raw, string version, bool expected) =>
        Assert.Equal(expected, Satisfies(ToolKind.Node, raw, version));

    [Theory]
    [InlineData(">=18 <21", "20.11.0", true)]
    [InlineData(">=18 <21", "21.0.0", false)]
    [InlineData("^20.0.0", "20.11.0", true)]
    [InlineData("^20.0.0", "21.0.0", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=3.9,<4", "3.12.1", true)]
    [InlineData("~=3.10", "3.12.0", true)]
    [InlineData("~=3.10", "4.0.0", false)]
    [InlineData("~=3.10.1", "3.11.0", false)]
    public void RangesAcrossEcosystems(string raw, string version, bool expected) =>
        Assert.Equal(expected, Satisfies(ToolKind.Python, raw, version));

    [Theory]
    [InlineData("lts/iron", "20.11.0", true)]
    [InlineData("lts/iron", "22.4.0", false)]
    [InlineData("lts/*", "22.4.0", true)]
    [InlineData("lts/*", "21.0.0", false)]
    public void NodeLtsAliases(string raw, string version, bool expected) =>
        Assert.Equal(expected, Satisfies(ToolKind.Node, raw, version));

    [Theory]
    [InlineData("1.8", "1.8.0_292", true)]
    [InlineData("1.8", "21", false)]
    [InlineData("17", "17.0.10", true)]
    [InlineData("21", "21.0.2+13", true)]
    public void JavaPinsCompareByMajor(string raw, string version, bool expected) =>
        Assert.Equal(expected, Satisfies(ToolKind.Jdk, raw, version));

    [Fact]
    public void MavenIntervalNotation()
    {
        var constraint = ConstraintParser.Parse(ToolKind.Jdk, "[17,22)");

        Assert.Equal(ConstraintKind.Range, constraint.Kind);
        Assert.True(constraint.Satisfies(VersionNumber.Parse("17"), ToolKind.Jdk));
        Assert.True(constraint.Satisfies(VersionNumber.Parse("21"), ToolKind.Jdk));
        Assert.False(constraint.Satisfies(VersionNumber.Parse("22"), ToolKind.Jdk));
    }

    [Fact]
    public void GarbageBecomesUnknownWithAReason()
    {
        var constraint = ConstraintParser.Parse(ToolKind.Python, "unknown");

        Assert.Equal(ConstraintKind.Unknown, constraint.Kind);
        Assert.NotNull(constraint.Reason);
    }

    [Fact]
    public void SdkmanCandidateKeepsVendorButComparesVersion()
    {
        var constraint = ConstraintParser.Parse(ToolKind.Jdk, "21.0.2-tem");

        Assert.Equal(ConstraintKind.Toolchain, constraint.Kind);
        Assert.True(constraint.Satisfies(VersionNumber.Parse("21.0.2"), ToolKind.Jdk));
    }

    [Fact]
    public void AlternativesAreUnionedNotIntersected()
    {
        var caret = ConstraintParser.Parse(ToolKind.Php, "^8.0 || ^7.4");
        var pipe = ConstraintParser.Parse(ToolKind.Php, "^7.3|^8.0");

        Assert.Equal(ConstraintKind.Union, caret.Kind);
        Assert.Equal(ConstraintKind.Union, pipe.Kind);
        Assert.True(pipe.Satisfies(VersionNumber.Parse("7.3.33"), ToolKind.Php));
        Assert.True(caret.Satisfies(VersionNumber.Parse("8.2.0"), ToolKind.Php));
        Assert.False(caret.Satisfies(VersionNumber.Parse("6.0.0"), ToolKind.Php));
        Assert.Contains("|", caret.Display());
    }
}
