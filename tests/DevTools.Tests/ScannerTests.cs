using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly string _root;

    public ScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("n"));

        Write("api/package.json", """{ "name": "acme-api", "engines": { "node": ">=20" } }""");
        Write("api/.nvmrc", "20.11.0\n");
        Write("api/node_modules/dependency/package.json", """{ "name": "dependency" }""");
        Write("api/node_modules/dependency/index.js", "module.exports = 1;");

        Write("legacy/pom.xml", """
        <project>
          <artifactId>legacy</artifactId>
          <properties><maven.compiler.release>1.8</maven.compiler.release></properties>
        </project>
        """);

        Write("test/fixtures/sample/package.json", """{ "name": "fixture" }""");
        Write("notes/README.md", "# notes");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (WalkResult Walk, List<ProjectRecord> Projects) RunScan()
    {
        var options = new ScanOptions
        {
            Roots = [_root],
            MaxDepth = 8,
            MaxParallelism = 2,
            ReadGitMetadata = false
        };

        var walk = new DirectoryWalker().Walk(options);
        var projects = new ProjectScanner().Build(walk, options);
        return (walk, projects);
    }

    [Fact]
    public void FindsProjectsAndNeverEntersNodeModules()
    {
        var (walk, projects) = RunScan();

        var paths = walk.Candidates.Select(c => c.Path.Replace('\\', '/')).ToList();
        Assert.Contains(paths, p => p.EndsWith("/api", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith("/legacy", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("node_modules", StringComparison.Ordinal));

        var api = projects.Single(p => p.DisplayName == "acme-api");
        Assert.Equal(ProjectState.Active, api.State);
        Assert.Equal(2, api.Requirements.Count(r => r.Tool == ToolKind.Node));
        Assert.Contains(api.Requirements, r => r.Raw == "20.11.0" && r.Strength == RequirementStrength.Pinned);
        Assert.Contains(api.Requirements, r => r.Raw == ">=20" && r.Strength == RequirementStrength.Advisory);

        var legacy = projects.Single(p => p.DisplayName == "legacy");
        Assert.Contains(legacy.Requirements, r => r.Tool == ToolKind.Jdk && r.Raw == "8");
    }

    [Fact]
    public void FixtureProjectsAreSuppressedNotDropped()
    {
        var (_, projects) = RunScan();

        var fixture = Assert.Single(projects, p => p.Path.Replace('\\', '/').Contains("test/fixtures/sample"));
        Assert.Equal(ProjectState.Suppressed, fixture.State);
        Assert.NotNull(fixture.SuppressedReason);
    }

    [Fact]
    public void WeakMarkerOnlyDirectoriesAreNotProjects()
    {
        var (walk, _) = RunScan();
        Assert.DoesNotContain(walk.Candidates, c => c.Path.Replace('\\', '/').EndsWith("/notes", StringComparison.Ordinal));
    }

    [Fact]
    public void PrunedDirectoriesAreCountedSoTheReportCanExplainItself()
    {
        var (walk, _) = RunScan();

        Assert.True(walk.Stats.DirsPruned > 0);
        Assert.True(walk.Stats.PrunedByReason.ContainsKey("cache-or-output"));
        Assert.True(walk.Stats.DirsVisited > 0);
    }

    [Fact]
    public void ConfiguredExclusionsAreAppliedByTheWalker()
    {
        var walk = new DirectoryWalker().Walk(new ScanOptions
        {
            Roots = [_root], ExcludeDirectoryNames = ["api"]
        });
        Assert.DoesNotContain(walk.Candidates, c => c.Path == Path.Combine(_root, "api"));
        Assert.True(walk.Stats.PrunedByReason.ContainsKey("user-exclude"));
    }

    [Fact]
    public void OversizedManifestMakesProjectIncompleteInsteadOfParsingTruncatedData()
    {
        Write("oversized/package.json", new string(' ', 1_000_001));
        var (_, projects) = RunScan();
        var project = Assert.Single(projects, p => p.Path == Path.Combine(_root, "oversized"));
        Assert.True(project.Incomplete);
        Assert.Contains(project.Diagnostics, d => d.Code == "input-unreadable");
    }

    [Fact]
    public async Task MissingRootRetainsOldProjectsAsStaleButSuccessfulScanRemovesDeletedProjects()
    {
        var service = new ScanService();
        var request = new ScanRequest { Roots = [_root], IncludeInventory = false };
        var first = await service.ScanAsync(request);
        Assert.Contains(first.Projects, p => p.DisplayName == "acme-api");

        var unavailable = await service.ScanAsync(new ScanRequest
        {
            Roots = [Path.Combine(_root, "api") + "-missing"], IncludeInventory = false
        }, first);
        Assert.True(unavailable.Stats.Partial);
        Assert.Empty(unavailable.Projects); // Unrelated roots are not retained.

        var missingRoot = Path.Combine(_root, "api");
        var scoped = await service.ScanAsync(new ScanRequest { Roots = [missingRoot], IncludeInventory = false });
        Directory.Move(missingRoot, missingRoot + "-moved");
        var partial = await service.ScanAsync(new ScanRequest { Roots = [missingRoot], IncludeInventory = false }, scoped);
        Assert.True(partial.Stats.Partial);
        Assert.True(Assert.Single(partial.Projects).Stale);

        Directory.Move(missingRoot + "-moved", missingRoot);
        var recovered = await service.ScanAsync(new ScanRequest { Roots = [missingRoot], IncludeInventory = false }, partial);
        Assert.False(recovered.Stats.Partial);
        Assert.False(Assert.Single(recovered.Projects).Stale);
        File.Delete(Path.Combine(missingRoot, "package.json"));
        File.Delete(Path.Combine(missingRoot, ".nvmrc"));
        var deleted = await service.ScanAsync(new ScanRequest { Roots = [missingRoot], IncludeInventory = false }, recovered);
        Assert.Empty(deleted.Projects);
    }

    [Fact]
    public async Task RefusesToReplaceAnIndexFromAnotherEnvironment()
    {
        var old = new IndexSnapshot { EnvironmentName = "another environment" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ScanService().ScanAsync(new ScanRequest { Roots = [_root], IncludeInventory = false }, old));
        Assert.Contains("separate DEVTOOLS_HOME", error.Message);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}

public class GitInfoTests
{
    [Fact]
    public void ReadsOriginUrlFromConfig()
    {
        var config = """
        [core]
        	repositoryformatversion = 0
        [remote "origin"]
        	url = git@github.com:acme/api.git
        	fetch = +refs/heads/*:refs/remotes/origin/*
        """;

        Assert.Equal("github.com/acme/api", GitInfo.ParseOriginUrl(config));
    }

    [Fact]
    public void NormalisesHttpsRemotes()
    {
        Assert.Equal("github.com/acme/api", GitInfo.Normalize("https://github.com/acme/api.git"));
        Assert.Equal("gitlab.com/acme/api", GitInfo.Normalize("git@gitlab.com:acme/api.git"));
        Assert.Equal("gitlab.com/acme/api", GitInfo.Normalize("ssh://git@gitlab.com:2222/acme/api.git"));
    }

    [Fact]
    public void ReadsTheLastCommitTimestamp()
    {
        var log = "0000000 1111111 Alexander <a@example.com> 1700000000 +0100\tcommit (initial): hello\n";

        var date = GitInfo.ParseLastCommitDate(log);

        Assert.NotNull(date);
        Assert.Equal(2023, date!.Value.Year);
    }

    [Fact]
    public void EmptyConfigYieldsNoRemote()
    {
        Assert.Null(GitInfo.ParseOriginUrl(null));
        Assert.Null(GitInfo.ParseLastCommitDate(null));
    }
}
