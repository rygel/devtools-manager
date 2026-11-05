using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

public sealed class ArtifactCleanupTests
{
    [Fact]
    public void JsonApplyRejectsAnUnreviewedPlan()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        var target = Path.Combine(project, "node_modules");
        try
        {
            Directory.CreateDirectory(target);
            var store = new SqliteStore(Path.Combine(root, "store"));
            store.Save(new IndexSnapshot { Projects = [new ProjectRecord { Path = project }] });
            var output = Path.GetDirectoryName(typeof(ArtifactCleanupTests).Assembly.Location)!;
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? PathSolver.Which("dotnet") ?? "dotnet";
            var result = ProcessRunner.Shared.Run(host,
                ["exec", "--runtimeconfig", Path.Combine(output, "DevTools.Tests.runtimeconfig.json"),
                    "--depsfile", Path.Combine(output, "DevTools.Tests.deps.json"), typeof(DevTools.Cli.Program).Assembly.Location,
                    "prune", "--project", project, "--apply", "--yes", "--plan", "wrong", "--json"],
                TimeSpan.FromSeconds(15), environment: new Dictionary<string, string> { ["DEVTOOLS_HOME"] = store.Root });
            Assert.False(result.Succeeded);
            Assert.Contains("plan", result.Combined, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(target));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ChangedPlanCannotMoveThePreviouslyReviewedTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(root, "project");
        var targetPath = Path.Combine(projectPath, "node_modules");
        try
        {
            Directory.CreateDirectory(targetPath);
            var project = new ProjectRecord { Path = projectPath };
            var store = new SqliteStore(Path.Combine(root, "store"));
            store.Save(new IndexSnapshot { Projects = [project] });
            var plan = ArtifactCleanup.Plan([project], projectPath);
            var fingerprint = ArtifactCleanup.Fingerprint(plan);
            File.WriteAllText(Path.Combine(targetPath, "new-file"), "new");
            var result = CleanupExecution.Apply(store, projectPath, null, null, fingerprint);
            Assert.False(result.Succeeded);
            Assert.Contains("changed", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(targetPath, "new-file")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PlanFiltersProjectsAndMeasuresOnlySelectedTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        try
        {
            Directory.CreateDirectory(Path.Combine(first, "node_modules", "nested"));
            Directory.CreateDirectory(Path.Combine(second, "target"));
            File.WriteAllBytes(Path.Combine(first, "node_modules", "nested", "file"), new byte[13]);
            File.WriteAllBytes(Path.Combine(second, "target", "file"), new byte[40]);
            var projects = new[]
            {
                new ProjectRecord { Path = first, LastActivityAt = DateTimeOffset.UtcNow.AddDays(-30) },
                new ProjectRecord { Path = second, LastActivityAt = DateTimeOffset.UtcNow }
            };
            var target = Assert.Single(ArtifactCleanup.Plan(projects, first, DateTimeOffset.UtcNow.AddDays(-10), measure: true));
            Assert.Equal(Path.Combine(first, "node_modules"), target.Directory);
            Assert.Equal(13, target.SizeBytes);
            Assert.NotEqual(ArtifactCleanup.Fingerprint([target]), ArtifactCleanup.Fingerprint([]));
            var originalPlan = ArtifactCleanup.Fingerprint([target]);
            File.WriteAllText(Path.Combine(target.Directory, "new-file"), "new");
            Assert.True(ArtifactCleanup.HasChanged(target));
            Assert.NotEqual(originalPlan, ArtifactCleanup.Fingerprint(ArtifactCleanup.Plan(projects, first, DateTimeOffset.UtcNow.AddDays(-10))));
            Assert.Empty(ArtifactCleanup.Plan(projects, second, DateTimeOffset.UtcNow.AddDays(-10)));
            Assert.True(Directory.Exists(target.Directory));
        }
        finally { Directory.Delete(root, true); }
    }
}
