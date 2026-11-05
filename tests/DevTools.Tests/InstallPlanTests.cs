using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

public sealed class InstallPlanTests
{
    [Theory]
    [InlineData("mise", "install", "node@20")]
    [InlineData("volta", "fetch", "node@20")]
    public void VersionManagersDoNotChangeGlobalDefault(string manager, string command, string argument)
    {
        var step = InstallPlanner.BuildStep(new MissingToolchain { Tool = ToolKind.Node, SuggestedVersion = "20" }, manager);
        Assert.False(step.Manual);
        Assert.Equal(new[] { command, argument }, step.Arguments);
    }

    [Fact]
    public void GlobalNpmChangeRequiresManualAction()
    {
        var step = InstallPlanner.BuildStep(new MissingToolchain { Tool = ToolKind.Npm, SuggestedVersion = "10" }, "npm");
        Assert.True(step.Manual);
        Assert.Null(step.FileName);
    }

    [Fact]
    public void AsdfUsesTheNodejsPluginName()
    {
        var step = InstallPlanner.BuildStep(new MissingToolchain { Tool = ToolKind.Node, SuggestedVersion = "20" }, "asdf");
        Assert.Collection(step.Arguments, command => Assert.Equal("install", command),
            plugin => Assert.Equal("nodejs", plugin), version => Assert.Equal("20", version));
    }

    [Fact]
    public void UnknownRequirementIsNotTurnedIntoLatestInstall()
    {
        var project = new ProjectRecord
        {
            Requirements = [Requirement.Declared(ToolKind.Node, "not-a-version", Provenance.FileOf("package.json"))]
        };
        var view = MachineView.Build(new IndexSnapshot { Projects = [project] });
        Assert.True(view.Projects[0].HasProblems);
        Assert.Empty(InstallPlanner.Build(view).Steps);
    }

    [Fact]
    public void RangeRequirementDoesNotAutomaticallyInstallItsLowerBound()
    {
        var project = new ProjectRecord
        {
            Requirements = [Requirement.Declared(ToolKind.Node, ">20.0.0 <22", Provenance.FileOf("package.json"))]
        };
        var view = MachineView.Build(new IndexSnapshot { Projects = [project] });
        var step = Assert.Single(InstallPlanner.Build(view).Steps);
        Assert.True(step.Manual);
        Assert.Null(step.FileName);
    }

    [Fact]
    public void ChangedInstallPlanIsRejectedBeforeAuditOrExecution()
    {
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.Save(new IndexSnapshot { Projects = [new ProjectRecord
            {
                Requirements = [Requirement.Declared(ToolKind.Node, ">20.0.0 <22", Provenance.FileOf("package.json"))]
            }] });
            var result = InstallExecution.Apply(store, null, null, "stale-token");
            Assert.False(result.Succeeded);
            Assert.Contains("changed", result.Error);
            Assert.Empty(store.ReadAudit());
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void PlanFingerprintIncludesAffectedProjects()
    {
        var step = new PlannedStep { Tool = "node", Version = "20", Projects = ["one"] };
        var original = step.Plan;
        step.Projects.Add("two");
        Assert.NotEqual(original, step.Plan);
    }
}
