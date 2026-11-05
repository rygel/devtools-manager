using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class UpdatePlannerTests
{
    private static Toolchain Installed(ToolKind tool, string version, string manager, ToolchainHealth health = ToolchainHealth.Ok) => new()
    {
        Tool = tool,
        Version = version,
        Path = "/" + manager + "/" + tool + "/" + version,
        Manager = manager,
        Health = health,
        VerifiedAt = DateTimeOffset.Now
    };

    private static InstallPlan Plan(string? only, Func<string, bool>? onPath, params Toolchain[] installed) =>
        UpdatePlanner.Build(installed, only, onPath ?? (_ => false));

    [Fact]
    public void FnmUpdatesToTheLatestPatchOfTheInstalledMajor()
    {
        var step = Assert.Single(Plan(null, null, Installed(ToolKind.Node, "20.11.0", "fnm")).Steps);
        Assert.False(step.Manual);
        Assert.Equal("fnm", step.FileName);
        Assert.Equal(["install", "20"], step.Arguments);
    }

    [Fact]
    public void SeveralInstallsOfOneMajorNeedTheCommandOnce() =>
        Assert.Single(Plan(null, null,
            Installed(ToolKind.Node, "20.11.0", "fnm"), Installed(ToolKind.Node, "20.9.0", "fnm")).Steps);

    [Fact]
    public void DifferentMajorsEachGetTheirOwnStep() =>
        Assert.Equal(2, Plan(null, null,
            Installed(ToolKind.Node, "20.11.0", "fnm"), Installed(ToolKind.Node, "22.1.0", "fnm")).Steps.Count);

    [Fact]
    public void AsdfRefreshesThePluginThenInstallsTheLatestOfTheMinor()
    {
        var steps = Plan(null, null, Installed(ToolKind.Python, "3.12.4", "asdf")).Steps;
        Assert.Equal(["plugin", "update", "python"], steps[0].Arguments);
        Assert.Equal(["install", "python", "latest:3.12"], steps[1].Arguments);
    }

    [Fact]
    public void AsdfUsesTheNodejsPluginName() =>
        Assert.Contains(Plan(null, null, Installed(ToolKind.Node, "20.1.0", "asdf")).Steps,
            step => step.Arguments.SequenceEqual(["install", "nodejs", "latest:20"]));

    [Fact]
    public void MiseUpgradeUsesMisesNameForTheJdk() =>
        Assert.Equal(["upgrade", "java"], Assert.Single(Plan(null, null, Installed(ToolKind.Jdk, "21.0.2", "mise")).Steps).Arguments);

    [Fact]
    public void UvUpgradesThePythonMinorLine() =>
        Assert.Equal(["python", "upgrade", "3.12"], Assert.Single(Plan(null, null, Installed(ToolKind.Python, "3.12.4", "uv")).Steps).Arguments);

    [Fact]
    public void RustupUpdateRunsOncePerMachine() =>
        Assert.Single(Plan(null, null,
            Installed(ToolKind.Rustc, "1.80.0", "rustup"), Installed(ToolKind.Rustc, "1.75.0", "rustup")).Steps);

    [Theory]
    [InlineData(ToolKind.Node, "nvm")]
    [InlineData(ToolKind.Node, "nvm-windows")]
    [InlineData(ToolKind.Jdk, "sdkman")]
    [InlineData(ToolKind.Python, "pyenv")]
    [InlineData(ToolKind.Ruby, "rbenv")]
    public void ShellFunctionsAndUnverifiedRoutesAreManualNotGuessed(ToolKind tool, string manager)
    {
        var step = Assert.Single(Plan(null, null, Installed(tool, "20.1.0", manager)).Steps);
        Assert.True(step.Manual);
        Assert.Null(step.FileName);
        Assert.False(string.IsNullOrWhiteSpace(step.Hint));
    }

    [Theory]
    [InlineData(ToolKind.Pnpm, "pnpm", "self-update")]
    [InlineData(ToolKind.Bun, "bun", "upgrade")]
    [InlineData(ToolKind.Deno, "deno", "upgrade")]
    public void SystemToolsWithASelfUpdateCommandUseIt(ToolKind tool, string file, string first)
    {
        var step = Assert.Single(Plan(null, null, Installed(tool, "1.2.3", "system")).Steps);
        Assert.Equal(file, step.FileName);
        Assert.Equal(first, step.Arguments[0]);
    }

    [Fact]
    public void ASystemToolWithNoDocumentedRouteProducesNoStep() =>
        Assert.Empty(Plan(null, null, Installed(ToolKind.Node, "20.1.0", "system")).Steps);

    [Fact]
    public void JuliaNeedsJuliaupOnPath()
    {
        var julia = Installed(ToolKind.Julia, "1.11.0", "system");
        Assert.Empty(Plan(null, name => false, julia).Steps);
        Assert.Equal(["update"], Assert.Single(Plan(null, name => name == "juliaup", julia).Steps).Arguments);
    }

    [Fact]
    public void TheToolFilterKeepsOnlyThatTool()
    {
        var plan = Plan("node", null, Installed(ToolKind.Node, "20.1.0", "fnm"), Installed(ToolKind.Rustc, "1.80.0", "rustup"));
        Assert.Equal("node", Assert.Single(plan.Steps).Tool);
    }

    [Fact]
    public void BrokenInstallationsAreNotUpdated() =>
        Assert.Empty(Plan(null, null, Installed(ToolKind.Node, "20.1.0", "fnm", ToolchainHealth.MissingBinary)).Steps);

    [Fact]
    public void TheFingerprintChangesWhenThePlanChanges()
    {
        var one = Plan(null, null, Installed(ToolKind.Node, "20.1.0", "fnm"));
        var two = Plan(null, null, Installed(ToolKind.Node, "20.1.0", "fnm"), Installed(ToolKind.Node, "22.1.0", "fnm"));
        Assert.NotEqual(one.Plan, two.Plan);
        Assert.Equal(one.Plan, Plan(null, null, Installed(ToolKind.Node, "20.9.9", "fnm")).Plan);
    }

    [Fact]
    public void AnEmptyPlanSaysSo() => Assert.Equal("Nothing to update", Plan(null, null).Title);

    [Fact]
    public void ACommandThatIsNotInstalledIsPassedThroughSoTheFailureNamesIt()
    {
        var (file, arguments) = InstallExecutor.Resolve("definitely-not-a-real-command-xyz", ["a", "b"]);
        Assert.Equal("definitely-not-a-real-command-xyz", file);
        Assert.Equal(["a", "b"], arguments);
    }
}
