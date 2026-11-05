using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Scan.Parsers;
using Xunit;

namespace DevTools.Tests;

public sealed class CiParserTests
{
    private const string NodeWorkflow = """
        name: ci
        on: [push]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - name: Setup node
                uses: actions/setup-node@v4
                with:
                  node-version: 20   # LTS
                  cache: npm
              - run: npm ci
        """;

    private static ProjectContext Run(string workflowName, string workflow, params Requirement[] earlier)
    {
        var context = ProjectContextFactory.Create(new InMemoryProjectFileSystem().File(".github/workflows/" + workflowName, workflow));
        foreach (var requirement in earlier)
        {
            context.Requirements.Add(requirement);
            context.Tag(ToolKinds.EcosystemOf(requirement.Tool));
        }
        new CiParser().Extract(context);
        return context;
    }

    private static Requirement Pin(ToolKind tool, string raw) => Requirement.Pinned(tool, raw, Provenance.FileOf(".nvmrc", 1));

    [Fact]
    public void SetupNodeVersionBecomesAnAdvisoryRequirementWithItsLine()
    {
        var context = Run("ci.yml", NodeWorkflow, Pin(ToolKind.Node, "20"));
        var ci = Assert.Single(context.Requirements, r => r.Strength == RequirementStrength.Advisory && r.Tool == ToolKind.Node);
        Assert.Equal("20", ci.Raw);
        Assert.Equal(".github/workflows/ci.yml", ci.Provenance.FilePath);
        Assert.Equal(11, ci.Provenance.Line);
        Assert.Equal("node-version", ci.Provenance.Key);
    }

    [Theory]
    [InlineData("actions/setup-java@v4", "java-version", "21", ToolKind.Jdk)]
    [InlineData("actions/setup-python@v5", "python-version", "'3.12'", ToolKind.Python)]
    [InlineData("actions/setup-go@v5", "go-version", "\"1.22\"", ToolKind.Go)]
    [InlineData("actions/setup-dotnet@v4", "dotnet-version", "8.0.x", ToolKind.DotNetSdk)]
    public void EachSetupActionMapsToItsToolchain(string action, string input, string value, ToolKind tool)
    {
        var workflow = $"jobs:\n  b:\n    steps:\n      - uses: {action}\n        with:\n          {input}: {value}\n";
        var seed = Pin(tool, "1");
        var context = Run("ci.yaml", workflow, seed);
        var ci = Assert.Single(context.Requirements, r => r.Strength == RequirementStrength.Advisory);
        Assert.Equal(tool, ci.Tool);
        Assert.Equal(value.Trim('\'', '"'), ci.Raw);
    }

    [Fact]
    public void DifferentPinnedVersionRaisesADriftWarning()
    {
        var context = Run("ci.yml", NodeWorkflow, Pin(ToolKind.Node, "18"));
        var drift = Assert.Single(context.Diagnostics, d => d.Code == "ci-drift");
        Assert.Equal(DiagnosticSeverity.Warning, drift.Severity);
        Assert.Contains("18", drift.Message);
        Assert.Contains("20", drift.Message);
        Assert.Equal(".github/workflows/ci.yml", drift.FilePath);
    }

    [Fact]
    public void MatchingOrCompatibleVersionsDoNotWarn()
    {
        Assert.DoesNotContain(Run("ci.yml", NodeWorkflow, Pin(ToolKind.Node, "20")).Diagnostics, d => d.Code == "ci-drift");
        var floor = Requirement.Declared(ToolKind.Node, ">=18", Provenance.FileOf("package.json", 1));
        Assert.DoesNotContain(Run("ci.yml", NodeWorkflow, floor).Diagnostics, d => d.Code == "ci-drift");
    }

    [Fact]
    public void NoLocalDeclarationMeansNothingToCompareAgainst()
    {
        var context = Run("ci.yml", NodeWorkflow);
        Assert.DoesNotContain(context.Diagnostics, d => d.Code == "ci-drift");
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Node);
    }

    [Fact]
    public void ToolsOutsideTheProjectsEcosystemsAreIgnored()
    {
        // A Python project whose workflow also sets up Node for a docs build must not become a Node project.
        var context = Run("ci.yml", NodeWorkflow, Pin(ToolKind.Python, "3.12"));
        Assert.DoesNotContain(context.Requirements, r => r.Tool == ToolKind.Node);
        Assert.DoesNotContain(context.Ecosystems, e => e == Ecosystem.Node);
    }

    [Theory]
    [InlineData("${{ matrix.node }}")]
    [InlineData("[18, 20]")]
    [InlineData("|")]
    public void ExpressionsListsAndBlockScalarsAreNotGuessed(string value)
    {
        var workflow = $"jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v4\n        with:\n          node-version: {value}\n";
        var context = Run("ci.yml", workflow, Pin(ToolKind.Node, "18"));
        Assert.DoesNotContain(context.Requirements, r => r.Strength == RequirementStrength.Advisory);
        Assert.DoesNotContain(context.Diagnostics, d => d.Code == "ci-drift");
    }

    [Fact]
    public void VersionFileInputsAreIgnoredBecauseTheyReadTheSameFileAsTheProject()
    {
        var workflow = "jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v4\n        with:\n          node-version-file: .nvmrc\n";
        var context = Run("ci.yml", workflow, Pin(ToolKind.Node, "18"));
        Assert.DoesNotContain(context.Requirements, r => r.Strength == RequirementStrength.Advisory);
    }

    [Fact]
    public void AVersionBelongingToADifferentStepIsNotAttributed()
    {
        var workflow = "jobs:\n  b:\n    steps:\n      - uses: actions/setup-node@v4\n      - uses: actions/cache@v4\n        with:\n          node-version: 99\n";
        var context = Run("ci.yml", workflow, Pin(ToolKind.Node, "18"));
        Assert.DoesNotContain(context.Requirements, r => r.Strength == RequirementStrength.Advisory);
    }

    [Fact]
    public void ProjectWithoutWorkflowsIsUntouched()
    {
        var context = ProjectContextFactory.Create(new InMemoryProjectFileSystem().File("package.json", "{}"));
        new CiParser().Extract(context);
        Assert.Empty(context.Requirements);
        Assert.Empty(context.Diagnostics);
    }

    [Fact]
    public void TheSameVersionRepeatedAcrossWorkflowsIsRecordedOnce()
    {
        var fs = new InMemoryProjectFileSystem()
            .File(".github/workflows/a.yml", NodeWorkflow)
            .File(".github/workflows/b.yml", NodeWorkflow);
        var context = ProjectContextFactory.Create(fs);
        context.Requirements.Add(Pin(ToolKind.Node, "18"));
        context.Tag(Ecosystem.Node);
        new CiParser().Extract(context);
        Assert.Single(context.Requirements, r => r.Strength == RequirementStrength.Advisory);
        Assert.Single(context.Diagnostics, d => d.Code == "ci-drift");
    }

    [Fact]
    public void ShaPinnedActionsWithTrailingVersionCommentsAreRecognised()
    {
        var workflow = """
            jobs:
              b:
                steps:
                  - uses: actions/setup-java@be666c2fcd27ec809703dec50e508c2fdc7f6654 # v5.2.0
                    with:
                      distribution: temurin
                      java-version: '21'
            """;
        var context = Run("ci.yml", workflow, Pin(ToolKind.Jdk, "21"));
        Assert.Equal("21", Assert.Single(context.Requirements, r => r.Strength == RequirementStrength.Advisory).Raw);
    }
}

public sealed class CiDriftRecordTests
{
    [Fact]
    public void DriftIsAProblemButDoesNotMakeTheProjectIncomplete()
    {
        var record = new ProjectRecord { Diagnostics = [Diagnostic.Warn(ProjectRecord.DriftCode, "CI installs node 20")] };
        Assert.True(record.HasCiDrift);
        Assert.False(record.Incomplete);
    }

    [Fact]
    public void OtherWarningsStillMeanIncomplete()
    {
        var record = new ProjectRecord { Diagnostics = [Diagnostic.Warn("parser-failed", "x")] };
        Assert.False(record.HasCiDrift);
        Assert.True(record.Incomplete);
    }
}
