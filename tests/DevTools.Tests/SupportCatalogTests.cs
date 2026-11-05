using DevTools.Core.Model;
using DevTools.Desktop;
using DevTools.Scan;
using Xunit;

namespace DevTools.Tests;

public sealed class SupportCatalogTests
{
    [Fact]
    public void EveryToolchainAppearsExactlyOnce()
    {
        var listed = SupportCatalog.Build().SelectMany(group => group.Tools).Select(tool => tool.Tool).ToList();
        Assert.Equal(ToolKinds.All.Count, listed.Count);
        Assert.Equal(ToolKinds.All.OrderBy(t => t), listed.OrderBy(t => t));
    }

    [Fact]
    public void EveryFrameworkAppearsExactlyOnceUnderItsEcosystem()
    {
        var groups = SupportCatalog.Build();
        Assert.Equal(FrameworkCatalog.All.Count, groups.Sum(group => group.Frameworks.Count));
        foreach (var group in groups)
            Assert.All(group.Frameworks, framework => Assert.Equal(group.Ecosystem, framework.Ecosystem));
    }

    [Theory]
    [InlineData("pom.xml", Ecosystem.Java)]
    [InlineData("gleam.toml", Ecosystem.Gleam)]
    [InlineData("dune-project", Ecosystem.OCaml)]
    public void SearchingAMarkerFileFindsItsEcosystem(string marker, Ecosystem expected) =>
        Assert.Contains(SupportCatalog.Build(marker), group => group.Ecosystem == expected);

    [Fact]
    public void ToolsAreGroupedUnderTheEcosystemTheyBelongTo()
    {
        foreach (var group in SupportCatalog.Build())
            Assert.All(group.Tools, tool => Assert.Equal(group.Ecosystem, ToolKinds.EcosystemOf(tool.Tool)));
    }

    [Fact]
    public void CapabilityFlagsComeFromTheRealProvidersAndInstallPlanner()
    {
        var tools = SupportCatalog.Build().SelectMany(group => group.Tools).ToDictionary(tool => tool.Tool);
        Assert.True(tools[ToolKind.Node].Discovered);
        Assert.True(tools[ToolKind.Python].Installable);
        Assert.False(tools[ToolKind.Cmake].Installable);
        // Declared-only build systems are neither found on the machine nor installed.
        foreach (var advisory in new[] { ToolKind.Bazel, ToolKind.Pants, ToolKind.Xmake, ToolKind.Mill, ToolKind.Sbt, ToolKind.Leiningen })
        {
            Assert.False(tools[advisory].Discovered, advisory + " must not claim discovery");
            Assert.False(tools[advisory].Installable, advisory + " must not claim installation");
        }
    }

    [Fact]
    public void EcosystemsListTheMarkerFilesThatIdentifyThem()
    {
        var node = SupportCatalog.Build().Single(group => group.Ecosystem == Ecosystem.Node);
        Assert.Contains("package.json", node.Markers);
    }

    [Theory]
    [InlineData(Ecosystem.DotNet, ".NET")]
    [InlineData(Ecosystem.Cpp, "C / C++")]
    [InlineData(Ecosystem.Node, "Node.js")]
    [InlineData(Ecosystem.Java, "Java")]
    public void EcosystemTitlesAreWrittenTheWayPeopleWriteThem(Ecosystem ecosystem, string title) =>
        Assert.Equal(title, SupportCatalog.EcosystemTitle(ecosystem));

    [Fact]
    public void FilteringByAFrameworkKeepsOnlyThatFramework()
    {
        var groups = SupportCatalog.Build("react");
        var group = Assert.Single(groups);
        Assert.Equal(Ecosystem.Node, group.Ecosystem);
        Assert.Empty(group.Tools);
        Assert.Contains(group.Frameworks, framework => framework.Name == "React");
        Assert.All(group.Frameworks, framework => Assert.Contains("react", framework.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilteringByAnEcosystemNameKeepsTheWholeGroup()
    {
        var group = Assert.Single(SupportCatalog.Build("python"), g => g.Ecosystem == Ecosystem.Python);
        Assert.Contains(group.Tools, tool => tool.Tool == ToolKind.Uv);
        Assert.NotEmpty(group.Frameworks);
    }

    [Fact]
    public void FilteringIsCaseInsensitiveAndIgnoresSurroundingSpace() =>
        Assert.Equal(SupportCatalog.Build("MAVEN").Select(g => g.Ecosystem), SupportCatalog.Build("  maven ").Select(g => g.Ecosystem));

    [Fact]
    public void AFilterThatMatchesNothingReturnsNoGroups() => Assert.Empty(SupportCatalog.Build("no-such-tool-xyz"));

    [Fact]
    public void SummaryCountsMatchTheCatalogue()
    {
        var summary = SupportCatalog.Summary();
        Assert.Contains(ToolKinds.All.Count.ToString(), summary);
        Assert.Contains(FrameworkCatalog.All.Count.ToString(), summary);
    }
}
