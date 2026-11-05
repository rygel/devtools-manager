using DevTools.Core.Model;
using DevTools.Scan;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

public sealed class ScannerFrameworkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("n"));

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private List<ProjectRecord> Scan()
    {
        var options = new ScanOptions { Roots = [_root], MaxDepth = 6, MaxParallelism = 2, ReadGitMetadata = false };
        return new ProjectScanner().Build(new DirectoryWalker().Walk(options), options);
    }

    [Fact]
    public void ScanRecordsFrameworksApartFromToolchainRequirements()
    {
        Write("web/package.json", """{ "name": "web", "engines": { "node": ">=20" }, "dependencies": { "react": "^18.2.0" } }""");
        Write("web/.nvmrc", "20.11.0\n");
        var web = Assert.Single(Scan(), p => p.DisplayName == "web");
        var react = Assert.Single(web.Frameworks);
        Assert.Equal("react", react.Id);
        Assert.Equal("^18.2.0", react.Version);
        Assert.All(web.Requirements, requirement => Assert.Equal(ToolKind.Node, requirement.Tool));
    }

    [Fact]
    public void FrameworksSurviveTheIndexRoundTrip()
    {
        var store = new SqliteStore(Path.Combine(_root, "store"));
        store.Save(new IndexSnapshot { Projects =
        [
            new ProjectRecord
            {
                Path = "/p", Frameworks =
                [
                    new FrameworkUse { Id = "spring-boot", Name = "Spring Boot", Ecosystem = Ecosystem.Java, Version = "3.2.1",
                        Provenance = Provenance.FileOf("pom.xml", 7, "org.springframework.boot:spring-boot-starter-parent") }
                ]
            }
        ] });
        var loaded = Assert.Single(store.Load()!.Projects);
        var framework = Assert.Single(loaded.Frameworks);
        Assert.Equal("spring-boot", framework.Id);
        Assert.Equal("3.2.1", framework.Version);
        Assert.Equal(Ecosystem.Java, framework.Ecosystem);
        Assert.Equal("pom.xml:7", framework.Provenance.Describe().Split(' ')[0]);
    }

    [Fact]
    public void IndexesWrittenBeforeFrameworksExistStillLoad()
    {
        var store = new SqliteStore(Path.Combine(_root, "store"));
        File.WriteAllText(store.LegacyIndexPath,"""{ "schemaVersion": 1, "projects": [ { "key": "k", "path": "/p", "state": "active" } ] }""");
        var project = Assert.Single(store.Load()!.Projects);
        Assert.Equal("/p", project.Path);
        Assert.Empty(project.Frameworks);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
