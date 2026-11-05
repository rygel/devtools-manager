using DevTools.Core.Model;
using DevTools.Core.Versioning;
using DevTools.Desktop;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class ToolGroupingTests
{
    // No Simple Icons entry exists for these: they show a letter tile on purpose.
    private static readonly HashSet<ToolKind> Unlogoed = [ToolKind.Meson, ToolKind.Pants, ToolKind.Xmake, ToolKind.Mill, ToolKind.Sbt, ToolKind.Leiningen, ToolKind.Fpc];

    private static readonly MachineView Machine = MachineView.Build(new IndexSnapshot());

    private static ToolRow Row(ToolKind tool, string version, string path = "") =>
        new(new Toolchain { Tool = tool, Version = version, Path = path.Length == 0 ? "/" + tool + "/" + version : path, Health = ToolchainHealth.Ok },
            Machine, false, 0, tool + " " + version);

    [Fact]
    public void EachToolGetsOneHeaderFollowedByItsVersionsNewestFirst()
    {
        var grouped = ToolGroups.Build([
            Row(ToolKind.Python, "3.9.18"), Row(ToolKind.Node, "20.11.0"), Row(ToolKind.Python, "3.12.1"),
            Row(ToolKind.Uv, "0.5.4"), Row(ToolKind.Python, "3.13.0")]);

        Assert.Collection(grouped,
            item => Assert.Equal((ToolKind.Node, 1), Header(item)),
            item => Assert.Equal("Node 20.11.0".ToLowerInvariant(), Plain(item).ToLowerInvariant()),
            item => Assert.Equal((ToolKind.Python, 3), Header(item)),
            item => Assert.Equal("3.13.0", Version(item)),
            item => Assert.Equal("3.12.1", Version(item)),
            item => Assert.Equal("3.9.18", Version(item)),
            item => Assert.Equal((ToolKind.Uv, 1), Header(item)),
            item => Assert.Equal("0.5.4", Version(item)));
    }

    [Fact]
    public void VersionsAreComparedAsNumbersNotText()
    {
        var versions = ToolGroups.Build([Row(ToolKind.Jdk, "8.0.402"), Row(ToolKind.Jdk, "21.0.2"), Row(ToolKind.Jdk, "17.0.10")])
            .OfType<ToolRow>().Select(r => r.Toolchain.Version).ToList();
        Assert.Equal(["21.0.2", "17.0.10", "8.0.402"], versions);
    }

    [Fact]
    public void UnparseableVersionsSortLastAndTiesFallBackToPath()
    {
        var rows = ToolGroups.Build([
            Row(ToolKind.Go, "weird-build", "/b"), Row(ToolKind.Go, "1.22.0", "/z"), Row(ToolKind.Go, "1.22.0", "/a"), Row(ToolKind.Go, "also-weird", "/a")])
            .OfType<ToolRow>().Select(r => r.Toolchain.Path).ToList();
        Assert.Equal(["/a", "/z", "/a", "/b"], rows);
    }

    [Fact]
    public void NoToolchainsMeansNoRowsAndNoHeaders() => Assert.Empty(ToolGroups.Build([]));

    [Fact]
    public void HeaderCountsAreWordedForSingularAndPlural()
    {
        var grouped = ToolGroups.Build([Row(ToolKind.Go, "1.22.0"), Row(ToolKind.Node, "20.0.0"), Row(ToolKind.Node, "22.0.0")]);
        Assert.Equal("1 installed", grouped.OfType<ToolGroupHeader>().Single(h => h.Tool == ToolKind.Go).CountText);
        Assert.Equal("2 installed", grouped.OfType<ToolGroupHeader>().Single(h => h.Tool == ToolKind.Node).CountText);
    }

    [Fact]
    public void EveryToolchainKindHasAnIconOrAnHonestFallback()
    {
        AvaloniaTestHost.Ensure();
        foreach (var tool in ToolKinds.All)
        {
            Assert.NotNull(ToolIcons.Tile(tool));
            Assert.True(ToolIcons.HasLogo(tool) || Unlogoed.Contains(tool), tool + " has neither a logo nor a documented fallback");
        }
    }

    [Fact]
    public void BrandColoursThatWouldVanishOnADarkSurfaceAreLifted()
    {
        var black = ToolIcons.Visible(Avalonia.Media.Color.Parse("#000000"));
        Assert.True(black.R > 100, "pure black must be lifted to a readable grey");
        var blue = Avalonia.Media.Color.Parse("#5AA9E6");
        Assert.Equal(blue, ToolIcons.Visible(blue));
    }

    private static (ToolKind, int) Header(IRow item)
    {
        var header = Assert.IsType<ToolGroupHeader>(item);
        return (header.Tool, header.Count);
    }

    private static string Plain(IRow item) => item.Plain;

    private static string Version(IRow item) => Assert.IsType<ToolRow>(item).Toolchain.Version;
}
