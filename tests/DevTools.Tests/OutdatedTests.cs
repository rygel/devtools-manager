using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class OutdatedTests
{
    private static readonly IReadOnlyList<EolCycle> Node =
    [
        new("26", null, false, "26.13.0"),
        new("24", null, false, "24.22.2"),
        new("20", null, true, "20.19.4")
    ];

    private static Toolchain Installed(ToolKind tool, string version, string manager) => new()
    {
        Tool = tool, Version = version, Path = "/" + manager + "/" + tool + "/" + version,
        Manager = manager, Health = ToolchainHealth.Ok, VerifiedAt = DateTimeOffset.Now
    };

    private static Dictionary<ToolKind, IReadOnlyList<EolCycle>> Data() =>
        new Dictionary<ToolKind, IReadOnlyList<EolCycle>> { [ToolKind.Node] = Node };

    [Fact]
    public void ParserKeepsTheLatestReleaseOfEachLine()
    {
        var cycles = EndOfLifeParser.Parse("""[{"cycle":"3.12","eol":"2028-10-31","latest":"3.12.12"},{"cycle":"1","eol":false,"latest":"1.4.4"},{"cycle":"2.0","eol":"2020-01-01"}]""");
        Assert.Equal("3.12.12", cycles[0].Latest);
        Assert.Equal("1.4.4", cycles[1].Latest);
        Assert.Null(cycles[2].Latest);
    }

    [Fact]
    public void ACacheWrittenBeforeLatestWasKeptStillLoads() =>
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<EolCycle>("""{"Cycle":"20","EndDate":null,"Ended":true}""")!.Latest);

    [Fact]
    public void AnOlderPatchOfALineIsOutdatedAndNamesTheNewerOne()
    {
        var result = FreshnessAssessor.Assess(Node, ToolKind.Node, "20.11.0");
        Assert.Equal(FreshnessState.Outdated, result.State);
        Assert.Equal("20.19.4", result.LatestInLine);
        Assert.Equal("26.13.0", result.NewestLine);
        Assert.Contains("20.11.0 → 20.19.4", result.Describe());
    }

    [Fact]
    public void TheLatestPatchIsCurrentEvenWhenANewerLineExists()
    {
        var result = FreshnessAssessor.Assess(Node, ToolKind.Node, "24.22.2");
        Assert.Equal(FreshnessState.UpToDate, result.State);
        Assert.Equal("26.13.0", result.NewestLine);
    }

    [Fact]
    public void TheNewestLineHasNoNewerLineToMention()
    {
        var result = FreshnessAssessor.Assess(Node, ToolKind.Node, "26.13.0");
        Assert.Equal(FreshnessState.UpToDate, result.State);
        Assert.Null(result.NewestLine);
    }

    [Theory]
    [InlineData("19.0.0")]
    [InlineData("not-a-version")]
    public void AVersionWithNoMatchingLineIsUnknownNotGuessed(string version) =>
        Assert.Equal(FreshnessState.Unknown, FreshnessAssessor.Assess(Node, ToolKind.Node, version).State);

    [Fact]
    public void NoCachedDataIsUnknown() =>
        Assert.Equal(FreshnessState.Unknown, FreshnessAssessor.Assess(null, ToolKind.Node, "20.1.0").State);

    [Theory]
    [InlineData(ToolKind.Jdk)]
    [InlineData(ToolKind.DotNetSdk)]
    public void ToolsWhoseNumberingDiffersFromTheSourceAreNeverCompared(ToolKind tool)
    {
        Assert.False(FreshnessAssessor.CanCompare(tool));
        Assert.Equal(FreshnessState.Unknown, FreshnessAssessor.Assess([new("21", null, false, "21.0.9")], tool, "21.0.1").State);
    }

    [Fact]
    public void UpdateStepsRecordWhetherANewerReleaseExists()
    {
        var plan = UpdatePlanner.Build([Installed(ToolKind.Node, "20.11.0", "fnm")], null, _ => false, Data());
        var step = Assert.Single(plan.Steps);
        Assert.True(step.Outdated);
        Assert.Contains("20.19.4", step.Available);
    }

    [Fact]
    public void AStepWithoutReleaseDataIsUncheckedNotCurrent()
    {
        var step = Assert.Single(UpdatePlanner.Build([Installed(ToolKind.Node, "20.11.0", "fnm")], null, _ => false).Steps);
        Assert.Null(step.Outdated);
        Assert.Null(step.Available);
    }

    [Fact]
    public void OutdatedOnlyDropsCurrentAndUnknownToolchains()
    {
        var installed = new[]
        {
            Installed(ToolKind.Node, "20.11.0", "fnm"),   // outdated
            Installed(ToolKind.Node, "26.13.0", "volta"), // current
            Installed(ToolKind.Rustc, "1.80.0", "rustup") // no data
        };
        var plan = UpdatePlanner.Build(installed, null, _ => false, Data(), outdatedOnly: true);
        Assert.Equal(["fnm"], plan.Steps.Select(step => step.FileName));
    }

    [Fact]
    public void OneCommandCoveringSeveralInstallsIsOutdatedWhenAnyOfThemIs()
    {
        // The newest install is current; an older one behind the same `rustup update` is not.
        var data = new Dictionary<ToolKind, IReadOnlyList<EolCycle>>
        {
            [ToolKind.Rustc] = [new("1.99", null, false, "1.99.1"), new("1.96", null, false, "1.96.1")]
        };
        var installed = new[] { Installed(ToolKind.Rustc, "1.99.1", "rustup"), Installed(ToolKind.Rustc, "1.96.0", "rustup") };
        var all = Assert.Single(UpdatePlanner.Build(installed, null, _ => false, data).Steps);
        var only = Assert.Single(UpdatePlanner.Build(installed, null, _ => false, data, outdatedOnly: true).Steps);
        Assert.True(all.Outdated);
        Assert.True(only.Outdated);
        Assert.Contains("1.96.0 → 1.96.1", only.Available);
    }

    [Fact]
    public void ReleaseDataDoesNotChangeThePlanTokenWithoutTheFilter()
    {
        var installed = new[] { Installed(ToolKind.Node, "20.11.0", "fnm") };
        Assert.Equal(UpdatePlanner.Build(installed, null, _ => false).Plan,
            UpdatePlanner.Build(installed, null, _ => false, Data()).Plan);
    }

    [Theory]
    [InlineData(ToolKind.Swift, "swiftly", "swiftly", "update")]
    [InlineData(ToolKind.R, "rig", "rig", "add")]
    [InlineData(ToolKind.Nim, "choosenim", "choosenim", "update")]
    public void DetectedManagersGetTheirDocumentedUpdateCommand(ToolKind tool, string manager, string file, string first)
    {
        var step = Assert.Single(UpdatePlanner.Build([Installed(tool, "1.0.0", "system")], null, name => name == manager).Steps);
        Assert.Equal(file, step.FileName);
        Assert.Equal(first, step.Arguments[0]);
    }

    [Fact]
    public void WithoutTheManagerOnPathThereIsNoStep() =>
        Assert.Empty(UpdatePlanner.Build([Installed(ToolKind.Swift, "6.0.0", "system")], null, _ => false).Steps);

    [Fact]
    public void OpamUpdatesThePackageListThenUpgradesAndLeavesTheCompilerManual()
    {
        var steps = UpdatePlanner.Build([Installed(ToolKind.Ocaml, "5.2.0", "system")], null, name => name == "opam").Steps;
        Assert.Equal([["update"], ["upgrade", "--yes"]], steps.Where(step => !step.Manual).Select(step => step.Arguments.ToArray()));
        Assert.Single(steps, step => step.Manual);
    }

    [Fact]
    public void GhcupIsManualBecauseItsNonInteractiveFlagsAreNotDocumented()
    {
        var step = Assert.Single(UpdatePlanner.Build([Installed(ToolKind.Ghc, "9.6.6", "system")], null, name => name == "ghcup").Steps);
        Assert.True(step.Manual);
    }
}
