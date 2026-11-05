using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

public sealed class ProjectIgnoreTests
{
    private static (SqliteStore Store, string Root, string ProjectPath) Setup(ProjectState state = ProjectState.Active, double confidence = 1.0)
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var projectPath = Path.Combine(root, "project");
        Directory.CreateDirectory(projectPath);
        var store = new SqliteStore(Path.Combine(root, "store"));
        store.Save(new IndexSnapshot
        {
            Projects = [new ProjectRecord { Key = "k", Path = projectPath, State = state, Confidence = confidence }]
        });
        return (store, root, projectPath);
    }

    [Fact]
    public void IgnoreMarksTheProjectAndRecordsAnAuditEntry()
    {
        var (store, root, path) = Setup();
        try
        {
            var result = ProjectIgnore.Set(store, path, ignored: true);
            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(ProjectState.Ignored, store.Load()!.Projects.Single().State);
            Assert.Contains(store.ReadAudit(), e => e.Action == "ignore" && e.Target == path);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnignoreRestoresActiveWhenConfidenceIsAboveTheThreshold()
    {
        var (store, root, path) = Setup(ProjectState.Ignored, Confidence.Threshold + 0.1);
        try
        {
            Assert.True(ProjectIgnore.Set(store, path, ignored: false).Succeeded);
            Assert.Equal(ProjectState.Active, store.Load()!.Projects.Single().State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnignoreRestoresSuppressedWhenConfidenceIsBelowTheThreshold()
    {
        var (store, root, path) = Setup(ProjectState.Ignored, Confidence.Threshold - 0.1);
        try
        {
            Assert.True(ProjectIgnore.Set(store, path, ignored: false).Succeeded);
            Assert.Equal(ProjectState.Suppressed, store.Load()!.Projects.Single().State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnknownPathIsRejectedWithoutChangingTheIndex()
    {
        var (store, root, _) = Setup();
        try
        {
            var result = ProjectIgnore.Set(store, Path.Combine(root, "elsewhere"), ignored: true);
            Assert.False(result.Succeeded);
            Assert.Contains("No indexed project", result.Message);
            Assert.Equal(ProjectState.Active, store.Load()!.Projects.Single().State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void IgnoredProjectsAreExcludedFromCleanupPlans()
    {
        var (store, root, path) = Setup();
        try
        {
            Directory.CreateDirectory(Path.Combine(path, "node_modules"));
            Assert.True(ProjectIgnore.Set(store, path, ignored: true).Succeeded);
            Assert.Empty(DevTools.Providers.ArtifactCleanup.Plan(store.Load()!.Projects, path));
        }
        finally { Directory.Delete(root, true); }
    }
}
