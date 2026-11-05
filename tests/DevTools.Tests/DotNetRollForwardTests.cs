using DevTools.Core.Constraints;
using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Core.Versioning;
using Xunit;

namespace DevTools.Tests;

public class DotNetRollForwardTests
{
    private static bool Satisfies(string required, string? policy, string installed)
    {
        var constraint = ConstraintParser.Parse(ToolKind.DotNetSdk, required);
        return DotNetRollForward.Satisfies(constraint, VersionNumber.Parse(installed), policy);
    }

    [Theory]
    [InlineData("10.0.300", "latestFeature", "10.0.403", true)]
    [InlineData("10.0.300", "latestFeature", "10.0.100", false)]
    [InlineData("10.0.300", "latestFeature", "9.0.400", false)]
    [InlineData("8.0.100", "disable", "8.0.100", true)]
    [InlineData("8.0.100", "disable", "8.0.200", false)]
    [InlineData("8.0.100", "major", "10.0.403", true)]
    [InlineData("8.0.100", null, "8.0.200", true)]
    [InlineData("8.0.100", null, "9.0.100", false)]
    [InlineData("10.0.300", "patch", "10.0.301", false)]
    public void AppliesThePolicyTheSdkWouldApply(string required, string? policy, string installed, bool expected) =>
        Assert.Equal(expected, Satisfies(required, policy, installed));

    [Fact]
    public void ExplainIsSilentWhenTheExactVersionIsInstalled()
    {
        var constraint = ConstraintParser.Parse(ToolKind.DotNetSdk, "10.0.403");

        Assert.Null(DotNetRollForward.Explain(constraint, VersionNumber.Parse("10.0.403"), "latestFeature"));
        Assert.NotNull(DotNetRollForward.Explain(constraint, VersionNumber.Parse("10.0.500"), "latestFeature"));
    }
}
