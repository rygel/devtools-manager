using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public sealed class MoreEcosystemTests
{
    private static ProjectContext Run(IProjectParser parser, string file, string text)
    {
        var context = ProjectContextFactory.Create(new InMemoryProjectFileSystem().File(file, text));
        parser.Extract(context);
        return context;
    }

    [Theory]
    [InlineData("// swift-tools-version:5.9\nimport PackageDescription\n", ">= 5.9")]
    [InlineData("// swift-tools-version: 5.10.1\n", ">= 5.10.1")]
    public void SwiftToolsVersionIsAMinimum(string text, string expected)
    {
        var context = Run(new SwiftParser(), "Package.swift", text);
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Swift);
        Assert.Equal(expected, requirement.Raw);
        Assert.Equal(1, requirement.Provenance.Line);
    }

    [Fact]
    public void SwiftManifestWithoutTheHeaderGivesNoRequirement() =>
        Assert.Empty(Run(new SwiftParser(), "Package.swift", "import PackageDescription\n").Requirements);

    [Theory]
    [InlineData("name: demo\ncrystal: \">= 1.10, < 2.0\"\n", ">= 1.10, < 2.0")]
    [InlineData("name: demo\ncrystal: '~> 1.9'\n", "~> 1.9")]
    [InlineData("name: demo\ncrystal: 1.9.0 # tested\n", "1.9.0")]
    public void CrystalShardKeyIsAdvisory(string text, string expected)
    {
        var context = Run(new CrystalParser(), "shard.yml", text);
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Crystal);
        Assert.Equal(expected, requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
    }

    [Theory]
    [InlineData("name: demo\ncrystal: \"*\"\n")]
    [InlineData("name: demo\n")]
    public void CrystalWildcardOrMissingKeyGivesNothing(string text) =>
        Assert.Empty(Run(new CrystalParser(), "shard.yml", text).Requirements);

    [Fact]
    public void GleamCompilerVersionIsRead()
    {
        var context = Run(new GleamParser(), "gleam.toml", "name = \"app\"\ngleam = \">= 1.15.0\"\n");
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Gleam);
        Assert.Equal(">= 1.15.0", requirement.Raw);
        Assert.Equal("app", context.Name);
    }

    [Theory]
    [InlineData("requires \"nim >= 1.6.0\"\n", ">= 1.6.0")]
    [InlineData("requires \"jester\", \"nim >= 2.0.0\"\n", ">= 2.0.0")]
    [InlineData("requires \"nim >= 0.10.0, jester\"\n", ">= 0.10.0")]
    public void NimbleNimRequirementIsRead(string text, string expected)
    {
        var context = Run(new NimParser(), "app.nimble", text);
        Assert.Equal(expected, Assert.Single(context.Requirements, r => r.Tool == ToolKind.Nim).Raw);
    }

    [Fact]
    public void NimbleRequirementsOnOtherPackagesAreIgnored() =>
        Assert.Empty(Run(new NimParser(), "app.nimble", "requires \"nimble >= 1.0\", \"jester\"\n").Requirements);

    [Theory]
    [InlineData("Package: demo\nDepends: R (>= 4.1.0), stats\nImports: R6\n", ">= 4.1.0")]
    [InlineData("Package: demo\nDepends:\n    methods,\n    R (>= 3.5.0)\nImports: R6\n", ">= 3.5.0")]
    public void RDescriptionDependsGivesTheMinimumR(string text, string expected)
    {
        var context = Run(new RParser(), "DESCRIPTION", text);
        Assert.Equal(expected, Assert.Single(context.Requirements, r => r.Tool == ToolKind.R).Raw);
    }

    [Fact]
    public void ARPackageNamedLikeRIsNotTheInterpreter() =>
        Assert.Empty(Run(new RParser(), "DESCRIPTION", "Package: demo\nDepends: BiocR (>= 1.0)\n").Requirements);

    [Theory]
    [InlineData("Package.swift", Ecosystem.Swift)]
    [InlineData("shard.yml", Ecosystem.Crystal)]
    [InlineData("gleam.toml", Ecosystem.Gleam)]
    [InlineData("app.nimble", Ecosystem.Nim)]
    [InlineData("nim.cfg", Ecosystem.Nim)]
    [InlineData("renv.lock", Ecosystem.R)]
    [InlineData("analysis.Rproj", Ecosystem.R)]
    [InlineData("stack.yaml", Ecosystem.Haskell)]
    [InlineData("cabal.project", Ecosystem.Haskell)]
    [InlineData("demo.cabal", Ecosystem.Haskell)]
    [InlineData("dune-project", Ecosystem.OCaml)]
    [InlineData("demo.opam", Ecosystem.OCaml)]
    [InlineData("rebar.config", Ecosystem.Erlang)]
    [InlineData("dub.json", Ecosystem.D)]
    [InlineData("dub.sdl", Ecosystem.D)]
    [InlineData("cpanfile", Ecosystem.Perl)]
    [InlineData("Makefile.PL", Ecosystem.Perl)]
    [InlineData("demo-1.0-1.rockspec", Ecosystem.Lua)]
    public void MarkersClassify(string name, Ecosystem expected)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(expected, ecosystem);
    }

    [Theory]
    [InlineData(ToolKind.Swift, Ecosystem.Swift, "swift")]
    [InlineData(ToolKind.Crystal, Ecosystem.Crystal, "crystal")]
    [InlineData(ToolKind.Gleam, Ecosystem.Gleam, "gleam")]
    [InlineData(ToolKind.Nim, Ecosystem.Nim, "nim")]
    [InlineData(ToolKind.R, Ecosystem.R, "r")]
    [InlineData(ToolKind.Ghc, Ecosystem.Haskell, "ghc")]
    [InlineData(ToolKind.Cabal, Ecosystem.Haskell, "cabal")]
    [InlineData(ToolKind.Stack, Ecosystem.Haskell, "stack")]
    [InlineData(ToolKind.Ocaml, Ecosystem.OCaml, "ocaml")]
    [InlineData(ToolKind.Dmd, Ecosystem.D, "dmd")]
    [InlineData(ToolKind.Perl, Ecosystem.Perl, "perl")]
    [InlineData(ToolKind.Lua, Ecosystem.Lua, "lua")]
    public void ToolsBelongToTheirEcosystemAndParseByName(ToolKind tool, Ecosystem ecosystem, string name)
    {
        Assert.Equal(ecosystem, ToolKinds.EcosystemOf(tool));
        Assert.Equal(name, ToolKinds.Display(tool));
        Assert.True(ToolKinds.TryParse(name, out var parsed));
        Assert.Equal(tool, parsed);
    }

    [Fact]
    public void DefaultInventoryProbesEveryNewTool()
    {
        var tools = ToolchainInventory.CreateDefault(allowProbing: false).Tools;
        foreach (var tool in new[] { ToolKind.Swift, ToolKind.Crystal, ToolKind.Gleam, ToolKind.Nim, ToolKind.R, ToolKind.Ghc,
                     ToolKind.Cabal, ToolKind.Stack, ToolKind.Ocaml, ToolKind.Dmd, ToolKind.Perl, ToolKind.Lua })
            Assert.Contains(tool, tools);
    }
}
