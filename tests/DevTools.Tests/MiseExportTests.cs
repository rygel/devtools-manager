using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class MiseExportTests
{
    private static Requirement Pinned(ToolKind tool, string raw) => Requirement.Pinned(tool, raw, Provenance.FileOf("f", 1));

    private static Requirement Declared(ToolKind tool, string raw) => Requirement.Declared(tool, raw, Provenance.FileOf("f", 1));

    private static ProjectRecord Project(params Requirement[] requirements) =>
        new() { Name = "demo", Path = "/p/demo", Requirements = [.. requirements] };

    [Fact]
    public void ExactVersionsBecomeToolEntries()
    {
        var result = MiseExport.Build(Project(Pinned(ToolKind.Node, "20.11.0"), Declared(ToolKind.Python, "3.12"), Declared(ToolKind.Go, "1.22")));
        Assert.Contains("[tools]", result.Text);
        Assert.Contains("node = \"20.11.0\"", result.Text);
        Assert.Contains("python = \"3.12\"", result.Text);
        Assert.Contains("go = \"1.22\"", result.Text);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void RangesAreListedAsNotExportedNeverInvented()
    {
        var result = MiseExport.Build(Project(Declared(ToolKind.Node, ">=18"), Pinned(ToolKind.Python, "3.12")));
        Assert.DoesNotContain("node =", result.Text);
        Assert.Contains("python = \"3.12\"", result.Text);
        var skipped = Assert.Single(result.Skipped);
        Assert.Contains("node", skipped);
        Assert.Contains(">=18", skipped);
        Assert.Contains("# not exported", result.Text);
    }

    [Fact]
    public void AdvisoryRequirementsAreIgnored()
    {
        var advisory = Requirement.Advisory(ToolKind.Node, "20", Provenance.FileOf("ci.yml", 3));
        var result = MiseExport.Build(Project(advisory));
        Assert.DoesNotContain("node =", result.Text);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void TheStrongestDeclarationOfAToolWins()
    {
        var result = MiseExport.Build(Project(Declared(ToolKind.Node, "18"), Pinned(ToolKind.Node, "20")));
        Assert.Contains("node = \"20\"", result.Text);
        Assert.DoesNotContain("\"18\"", result.Text);
    }

    [Theory]
    [InlineData(ToolKind.Mill)]
    [InlineData(ToolKind.Xmake)]
    [InlineData(ToolKind.Pants)]
    public void ToolsWithoutAMiseRegistryEntryAreSkippedWithAReason(ToolKind tool)
    {
        var result = MiseExport.Build(Project(Declared(tool, "1.2.3")));
        Assert.DoesNotContain(ToolKinds.Display(tool) + " =", result.Text);
        Assert.Contains("no mise registry entry", Assert.Single(result.Skipped));
    }

    [Fact]
    public void JdkIsSkippedBecauseItNeedsADistribution()
    {
        var result = MiseExport.Build(Project(Declared(ToolKind.Jdk, "21")));
        Assert.DoesNotContain("java =", result.Text);
        Assert.Contains("distribution", Assert.Single(result.Skipped));
    }

    [Fact]
    public void NothingToExportSaysSo()
    {
        var result = MiseExport.Build(Project());
        Assert.Contains("nothing to export", result.Text);
        Assert.DoesNotContain("[tools]", result.Text);
    }

    [Theory]
    [InlineData(ToolKind.DotNetSdk, "dotnet")]
    [InlineData(ToolKind.Rustc, "rust")]
    [InlineData(ToolKind.Uv, "uv")]
    [InlineData(ToolKind.Zig, "zig")]
    public void ToolNamesFollowTheMiseRegistry(ToolKind tool, string name) =>
        Assert.Contains(name + " = \"1.2\"", MiseExport.Build(Project(Declared(tool, "1.2"))).Text);
}
