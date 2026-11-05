using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public sealed class BuildSystemTests
{
    private static ProjectContext Run(IProjectParser parser, string file, string text)
    {
        var context = ProjectContextFactory.Create(new InMemoryProjectFileSystem().File(file, text));
        parser.Extract(context);
        return context;
    }

    [Theory]
    [InlineData("7.2.1\n", "7.2.1")]
    [InlineData("  8.0.0rc3  \r\n", "8.0.0rc3")]
    [InlineData("7.1.0-pre.20240101.1\n", "7.1.0-pre.20240101.1")]
    public void BazelVersionFileGivesAnAdvisoryRequirement(string content, string expected)
    {
        var context = Run(new BazelParser(), ".bazelversion", content);
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Bazel);
        Assert.Equal(expected, requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
        Assert.Equal(".bazelversion", requirement.Provenance.FilePath);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("last_green")]
    [InlineData("rolling")]
    [InlineData("4.x")]
    [InlineData("4.*")]
    [InlineData("bazelbuild/7.0.0")]
    [InlineData("")]
    public void FloatingBazelVersionsAreNotInventedIntoAnExactRequirement(string content)
    {
        var context = Run(new BazelParser(), ".bazelversion", content);
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Bazel);
        if (content.Length > 0) Assert.Contains(context.Diagnostics, d => d.Code == "bazel-floating-version");
    }

    [Fact]
    public void BazelWorkspaceWithoutVersionFileTagsTheEcosystemOnly()
    {
        var context = Run(new BazelParser(), "MODULE.bazel", "module(name = \"demo\")\n");
        Assert.Empty(context.Requirements);
        Assert.Contains(Ecosystem.Bazel, context.Ecosystems);
    }

    [Theory]
    [InlineData("[GLOBAL]\npants_version = \"2.18.0\"\nbackend_packages = [\"pants.backend.python\"]\n", "2.18.0")]
    [InlineData("[GLOBAL]\r\npants_version='2.20.0rc1'\r\n", "2.20.0rc1")]
    public void PantsVersionIsReadFromPantsToml(string toml, string expected)
    {
        var context = Run(new PantsParser(), "pants.toml", toml);
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Pants);
        Assert.Equal(expected, requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
        Assert.Equal(2, requirement.Provenance.Line);
    }

    [Fact]
    public void CommentedOutPantsVersionIsIgnored()
    {
        var context = Run(new PantsParser(), "pants.toml", "[GLOBAL]\n# pants_version = \"2.18.0\"\n");
        Assert.Empty(context.Requirements);
    }

    [Fact]
    public void XmakeMinimumVersionIsReadAsALowerBound()
    {
        var context = Run(new CppBuildParser(), "xmake.lua", "set_xmakever(\"2.8.5\")\ntarget(\"app\")\n  set_kind(\"binary\")\n");
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Xmake);
        Assert.Equal(">= 2.8.5", requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
    }

    [Theory]
    [InlineData(".bazelversion", Ecosystem.Bazel)]
    [InlineData("MODULE.bazel", Ecosystem.Bazel)]
    [InlineData("WORKSPACE", Ecosystem.Bazel)]
    [InlineData("WORKSPACE.bazel", Ecosystem.Bazel)]
    [InlineData("pants.toml", Ecosystem.Pants)]
    [InlineData("xmake.lua", Ecosystem.Cpp)]
    public void BuildSystemMarkersClassify(string name, Ecosystem expected)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(expected, ecosystem);
    }

    [Theory]
    [InlineData(ToolKind.Bazel, Ecosystem.Bazel)]
    [InlineData(ToolKind.Pants, Ecosystem.Pants)]
    [InlineData(ToolKind.Xmake, Ecosystem.Cpp)]
    public void NewToolsBelongToTheirEcosystemAndParseByName(ToolKind tool, Ecosystem ecosystem)
    {
        Assert.Equal(ecosystem, ToolKinds.EcosystemOf(tool));
        Assert.True(ToolKinds.TryParse(ToolKinds.Display(tool), out var parsed));
        Assert.Equal(tool, parsed);
    }

    [Fact]
    public void MillVersionIsReadFromTheBuildHeader()
    {
        var context = Run(new MillParser(), "build.mill", "//| mill-version: 1.0.4\n//| mill-jvm-version: 21\npackage build\nimport mill._\n");
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Mill);
        Assert.Equal("1.0.4", requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
        Assert.Equal("build.mill", requirement.Provenance.FilePath);
        Assert.Equal(1, requirement.Provenance.Line);
    }

    [Fact]
    public void MillHeaderBeyondTheTopCommentBlockIsIgnored()
    {
        var context = Run(new MillParser(), "build.mill", "package build\n//| mill-version: 1.0.4\n");
        Assert.Empty(context.Requirements);
    }

    [Theory]
    [InlineData(".mill-version", "0.12.3\n", "0.12.3")]
    [InlineData(".config/mill-version", "  0.11.12  \r\n", "0.11.12")]
    public void MillVersionFilesAreRead(string file, string content, string expected)
    {
        var context = Run(new MillParser(), file, content);
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Mill);
        Assert.Equal(expected, requirement.Raw);
        Assert.Equal(file, requirement.Provenance.FilePath);
    }

    [Fact]
    public void MillYamlHeaderIsRead()
    {
        var context = Run(new MillParser(), "build.mill.yaml", "mill-version: \"1.0.2\"\n");
        Assert.Equal("1.0.2", Assert.Single(context.Requirements, r => r.Tool == ToolKind.Mill).Raw);
    }

    [Fact]
    public void ConflictingMillDeclarationsAreAllKeptNotArbitrated()
    {
        var fs = new InMemoryProjectFileSystem().File(".mill-version", "0.11.0\n").File("build.mill", "//| mill-version: 1.0.0\n");
        var context = ProjectContextFactory.Create(fs);
        new MillParser().Extract(context);
        Assert.Equal(["0.11.0", "1.0.0"], context.Requirements.Where(r => r.Tool == ToolKind.Mill).Select(r => r.Raw).Order());
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("0.12.x")]
    public void FloatingMillVersionIsReportedNotGuessed(string content)
    {
        var context = Run(new MillParser(), ".mill-version", content);
        Assert.Empty(context.Requirements);
        Assert.Contains(context.Diagnostics, d => d.Code == "mill-floating-version");
    }

    [Theory]
    [InlineData("build.mill")]
    [InlineData("build.mill.scala")]
    [InlineData("build.mill.yaml")]
    [InlineData(".mill-version")]
    public void MillMarkersClassify(string name)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(Ecosystem.Mill, ecosystem);
    }

    [Fact]
    public void MillToolBelongsToItsEcosystemAndParsesByName()
    {
        Assert.Equal(Ecosystem.Mill, ToolKinds.EcosystemOf(ToolKind.Mill));
        Assert.True(ToolKinds.TryParse("mill", out var tool));
        Assert.Equal(ToolKind.Mill, tool);
    }

    [Fact]
    public void SbtPinIsAnAdvisoryRequirement()
    {
        var context = Run(new ScalaParser(), "project/build.properties", "sbt.version=1.9.9\n");
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Sbt);
        Assert.Equal("1.9.9", requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
        Assert.Equal("project/build.properties", requirement.Provenance.FilePath);
    }

    [Fact]
    public void LeiningenMinimumVersionIsReadAsAFloor()
    {
        var context = Run(new ClojureParser(), "project.clj",
            "(defproject demo \"0.1.0\"\n  :min-lein-version \"2.9.1\"\n  :dependencies [[org.clojure/clojure \"1.11.1\"]])\n");
        var requirement = Assert.Single(context.Requirements, r => r.Tool == ToolKind.Leiningen);
        Assert.Equal(">= 2.9.1", requirement.Raw);
        Assert.Equal(RequirementStrength.Advisory, requirement.Strength);
        Assert.Equal(2, requirement.Provenance.Line);
    }

    [Fact]
    public void CommentedLeiningenVersionAndClojureLibraryAreNotRead()
    {
        var context = Run(new ClojureParser(), "project.clj",
            "(defproject demo \"0.1.0\"\n  ;; :min-lein-version \"2.0.0\"\n  :dependencies [[org.clojure/clojure \"1.11.1\"]])\n");
        Assert.Empty(context.Requirements);
        Assert.Contains(Ecosystem.Clojure, context.Ecosystems);
    }

    [Theory]
    [InlineData("project.clj")]
    [InlineData("deps.edn")]
    public void ClojureMarkersClassify(string name)
    {
        Assert.True(MarkerCatalog.TryClassify(name, out var ecosystem, out var strong));
        Assert.True(strong);
        Assert.Equal(Ecosystem.Clojure, ecosystem);
    }

    [Theory]
    [InlineData(ToolKind.Sbt, Ecosystem.Scala, "sbt")]
    [InlineData(ToolKind.Leiningen, Ecosystem.Clojure, "leiningen")]
    public void JvmBuildToolsBelongToTheirEcosystemAndParseByName(ToolKind tool, Ecosystem ecosystem, string name)
    {
        Assert.Equal(ecosystem, ToolKinds.EcosystemOf(tool));
        Assert.True(ToolKinds.TryParse(name, out var parsed));
        Assert.Equal(tool, parsed);
    }
}
