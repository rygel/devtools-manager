using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public sealed class PascalTests
{
    [Theory]
    [InlineData("demo.lpi")]
    [InlineData("DEMO.LPI")]
    [InlineData("demo.lpr")]
    [InlineData("components.lpk")]
    [InlineData("fpmake.pp")]
    public void LazarusAndFreePascalFilesClassifyAsStrongPascalMarkers(string name)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(Ecosystem.Pascal, ecosystem);
    }

    [Theory]
    [InlineData("readme.lpi.txt")]
    [InlineData("lpi")]
    public void OtherNamesAreNotPascalMarkers(string name)
    {
        var classified = MarkerCatalog.TryClassify(name, out var ecosystem, out _);
        Assert.False(classified && ecosystem == Ecosystem.Pascal);
    }

    [Fact]
    public void ParserTagsThePascalEcosystemWithoutInventingARequirement()
    {
        var context = ProjectContextFactory.Create(new InMemoryProjectFileSystem().File("demo.lpi", "<?xml version=\"1.0\"?>"));
        new EcosystemOnlyParser(Ecosystem.Pascal, "pascal").Extract(context);
        Assert.Contains(Ecosystem.Pascal, context.Ecosystems);
        Assert.Empty(context.Requirements);
    }

    [Theory]
    [InlineData(ToolKind.Fpc, "fpc")]
    [InlineData(ToolKind.Lazarus, "lazarus")]
    public void ToolsBelongToThePascalEcosystemAndParseByName(ToolKind tool, string name)
    {
        Assert.Equal(Ecosystem.Pascal, ToolKinds.EcosystemOf(tool));
        Assert.Equal(name, ToolKinds.Display(tool));
        Assert.True(ToolKinds.TryParse(name, out var parsed));
        Assert.Equal(tool, parsed);
        Assert.Equal("pascal", ToolKinds.DisplayEcosystem(Ecosystem.Pascal));
    }

    [Fact]
    public void DefaultInventoryLooksForTheCompilerAndLazbuild()
    {
        var tools = ToolchainInventory.CreateDefault(allowProbing: false).Tools;
        Assert.Contains(ToolKind.Fpc, tools);
        Assert.Contains(ToolKind.Lazarus, tools);
    }

    // `fpc -iV` prints the short version; lazbuild --version prints the quoted version.inc constant as a bare number.
    [Theory]
    [InlineData("3.2.2\n", "3.2.2")]
    [InlineData("3.2.2\r\n", "3.2.2")]
    [InlineData("3.6\n", "3.6")]
    [InlineData("3.6.0\n", "3.6.0")]
    public void VersionOutputIsReadAsADottedVersion(string output, string expected) =>
        Assert.Equal(expected, InventoryHelpers.VersionTokenIn(output));
}
