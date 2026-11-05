using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

public class MigrationEditorTests
{
    private static MigrationOptions Node(string target, bool ranges = false) =>
        new() { Tool = ToolKind.Node, Target = target, IncludeRanges = ranges };

    [Theory]
    [InlineData("https\\://services.gradle.org/distributions/gradle-8.8-bin.zip")]
    [InlineData("https://services.gradle.org/distributions/gradle-8.8-all.zip")]
    public void GradleWrapperChangesOnlyTheOfficialDistributionVersion(string url)
    {
        var content = "distributionUrl=" + url + "\r\nnetworkTimeout=10000\r\n";
        var result = MigrationPlanner.RewriteGradleWrapper(content, new MigrationOptions { Tool = ToolKind.Gradle, Target = "9.1.0" });
        Assert.NotNull(result.Content);
        Assert.Contains("gradle-9.1.0-", result.Content);
        Assert.Contains("networkTimeout=10000\r\n", result.Content);
    }

    [Fact]
    public void GradleWrapperWithChecksumOrCustomUrlRequiresManualHandling()
    {
        var options = new MigrationOptions { Tool = ToolKind.Gradle, Target = "9.1.0" };
        var checksum = MigrationPlanner.RewriteGradleWrapper("distributionUrl=https://services.gradle.org/distributions/gradle-8.8-bin.zip\ndistributionSha256Sum=abc\n", options);
        Assert.Null(checksum.Content);
        Assert.Contains("checksum", checksum.Reason);
        var spacedChecksum = MigrationPlanner.RewriteGradleWrapper("distributionUrl=https://services.gradle.org/distributions/gradle-8.8-bin.zip\ndistributionSha256Sum abc\n", options);
        Assert.Null(spacedChecksum.Content);
        var escapedKey = MigrationPlanner.RewriteGradleWrapper("distributionUrl=https://services.gradle.org/distributions/gradle-8.8-bin.zip\ndistributionSha256\\u0053um=abc\n", options);
        Assert.Null(escapedKey.Content);
        var custom = MigrationPlanner.RewriteGradleWrapper("distributionUrl=https://example.com/gradle-8.8-bin.zip\n", options);
        Assert.Null(custom.Content);
        var incomplete = MigrationPlanner.RewriteGradleWrapper("distributionUrl=https://services.gradle.org/distributions/gradle-8.8-bin.zip\n", new MigrationOptions { Tool = ToolKind.Gradle, Target = "9.1" });
        Assert.Null(incomplete.Content);
    }

    [Fact]
    public void SingleLinePinIsRewritten()
    {
        var result = MigrationPlanner.RewriteFirstLine("18.20.2\n", "24", ".nvmrc");

        Assert.Equal("24\n", result.Content);
        Assert.Contains("18.20.2", result.Description);
    }

    [Fact]
    public void SingleLinePinKeepsCrlfAndTrailingLines()
    {
        var result = MigrationPlanner.RewriteFirstLine("# pinned for CI\r\n18.20.2\r\n", "24", ".nvmrc");

        Assert.Equal("# pinned for CI\r\n24\r\n", result.Content);
    }

    [Fact]
    public void SingleLinePinThatIsAlreadyCorrectIsUnchanged()
    {
        var result = MigrationPlanner.RewriteFirstLine("24\n", "24", ".nvmrc");

        Assert.Equal("24\n", result.Content);
    }

    [Fact]
    public void LtsAliasCanBeReplacedWithAConcreteVersion()
    {
        var result = MigrationPlanner.RewriteFirstLine("lts/iron\n", "24", ".nvmrc");

        Assert.Equal("24\n", result.Content);
    }

    [Fact]
    public void JunkIsRefusedRatherThanOverwritten()
    {
        var result = MigrationPlanner.RewriteFirstLine("not-a-version-here\n", "24", ".nvmrc");

        Assert.Null(result.Content);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void ToolVersionsRewritesOnlyTheMatchingTool()
    {
        var result = MigrationPlanner.RewriteToolVersions(
            "nodejs 20.11.0\npython 3.11.4\nruby 3.2.2\n", Node("24"));

        Assert.Equal("nodejs 24\npython 3.11.4\nruby 3.2.2\n", result.Content);
    }

    [Fact]
    public void ToolVersionsMatchesEitherAsdfNameForNode()
    {
        var result = MigrationPlanner.RewriteToolVersions("node 18.20.2\n", Node("24"));

        Assert.Equal("node 24\n", result.Content);
    }

    [Fact]
    public void MiseRewritesOnlyTheToolsTable()
    {
        var content = """
        [tools]
        node = "20"
        python = "3.11"

        [env]
        node = "not-a-version"
        """;

        var result = MigrationPlanner.RewriteMise(content, Node("24"));

        Assert.NotNull(result.Content);
        Assert.Contains("node = \"24\"", result.Content);
        Assert.Contains("python = \"3.11\"", result.Content);
        Assert.Contains("node = \"not-a-version\"", result.Content);
    }

    [Fact]
    public void PackageJsonVoltaIsRewrittenAndFormattingSurvives()
    {
        var content = """
        {
          "name": "api",
          "volta": {
            "node": "18.20.2"
          },
          "engines": {
            "node": ">=18"
          }
        }
        """;

        var result = MigrationPlanner.RewritePackageJson(content, Node("24"));

        Assert.NotNull(result.Content);
        Assert.Equal(content.Replace("\"18.20.2\"", "\"24\""), result.Content);
    }

    [Fact]
    public void PackageJsonEnginesIsLeftAloneWithoutIncludeRanges()
    {
        var content = """
        { "engines": { "node": ">=18" } }
        """;

        var result = MigrationPlanner.RewritePackageJson(content, Node("24"));

        Assert.Equal(content, result.Content);
    }

    [Fact]
    public void PackageJsonEnginesIsRewrittenWithIncludeRanges()
    {
        var content = """
        { "engines": { "node": ">=18" } }
        """;

        var result = MigrationPlanner.RewritePackageJson(content, Node("24", ranges: true));

        Assert.NotNull(result.Content);
        Assert.Contains("\">=24\"", result.Content);
    }

    [Fact]
    public void GlobalJsonSdkVersionIsRewrittenInPlace()
    {
        var content = """
        {
          "sdk": {
            "version": "10.0.300",
            "rollForward": "latestFeature"
          }
        }
        """;

        var options = new MigrationOptions { Tool = ToolKind.DotNetSdk, Target = "10.0.403" };
        var result = MigrationPlanner.RewriteGlobalJson(content, options);

        Assert.NotNull(result.Content);
        Assert.Equal(content.Replace("10.0.300", "10.0.403"), result.Content);
        Assert.Contains("latestFeature", result.Content);
    }

    [Fact]
    public void SdkmanrcRefusesABareMajor()
    {
        var result = MigrationPlanner.RewriteSdkmanrc("java=21.0.2-tem\n", new MigrationOptions
        {
            Tool = ToolKind.Jdk,
            Target = "25"
        });

        Assert.Null(result.Content);
        Assert.Contains("distribution", result.Reason);

        var ok = MigrationPlanner.RewriteSdkmanrc("java=21.0.2-tem\n", new MigrationOptions
        {
            Tool = ToolKind.Jdk,
            Target = "25.0.2-tem"
        });

        Assert.Equal("java=25.0.2-tem\n", ok.Content);
    }

    [Fact]
    public void DenoMigrationRewritesBothPinFiles()
    {
        var options = new MigrationOptions { Tool = ToolKind.Deno, Target = "2.7.0" };

        Assert.Equal([".dvmrc", ".tool-versions", "mise.toml", ".mise.toml"],
            MigrationPlanner.FilesFor(ToolKind.Deno));

        var dvm = MigrationPlanner.RewriteFirstLine("2.6.9\n", "2.7.0", ".dvmrc");
        Assert.Equal("2.7.0\n", dvm.Content);

        var versions = MigrationPlanner.RewriteToolVersions("deno 2.6.9\nnodejs 20.0.0\n", options);
        Assert.Equal("deno 2.7.0\nnodejs 20.0.0\n", versions.Content);
    }

    [Fact]
    public void PyprojectRequiresRangesFlag()
    {
        var content = """
        [project]
        name = "thing"
        requires-python = ">=3.11"
        """;

        var without = MigrationPlanner.RewritePyproject(content, new MigrationOptions
        {
            Tool = ToolKind.Python, Target = "3.13"
        });

        Assert.Null(without.Content);

        var with = MigrationPlanner.RewritePyproject(content, new MigrationOptions
        {
            Tool = ToolKind.Python, Target = "3.13", IncludeRanges = true
        });

        Assert.Contains("requires-python = \">=3.13\"", with.Content);
    }
}

public sealed class MigrationPlanTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "devtools-migrate-tests", Guid.NewGuid().ToString("n"));

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ProjectRecord Project() => new()
    {
        Key = _root,
        Path = _root,
        Name = "api",
        State = ProjectState.Active,
        Requirements =
        [
            Requirement.Pinned(ToolKind.Node, "18.20.2", Provenance.FileOf(".nvmrc", 1))
        ]
    };

    [Fact]
    public void PlanFindsEveryNodeFileItCanRewrite()
    {
        Write("api/.nvmrc", "18.20.2\n");
        Write("api/package.json", """
        { "name": "api", "volta": { "node": "18.20.2" } }
        """);

        var project = Project();
        project.Path = Path.Combine(_root, "api");

        var plan = MigrationPlanner.Plan([project], new MigrationOptions
        {
            Tool = ToolKind.Node,
            Target = "24"
        });

        Assert.Equal(2, plan.Edits.Count);
        Assert.Equal(1, plan.ProjectsAffected);

        foreach (var edit in plan.Edits)
        {
            if (edit.RelativePath == ".nvmrc") Assert.Equal("24\n", edit.After);
            else Assert.Contains("\"node\": \"24\"", edit.After);
        }
    }

    [Fact]
    public void FromFilterSkipsProjectsOnOtherMajors()
    {
        Write("api/.nvmrc", "18.20.2\n");

        var project = Project();
        project.Path = Path.Combine(_root, "api");

        var plan = MigrationPlanner.Plan([project], new MigrationOptions
        {
            Tool = ToolKind.Node,
            Target = "24",
            From = "20"
        });

        Assert.Empty(plan.Edits);

        var matching = MigrationPlanner.Plan([project], new MigrationOptions
        {
            Tool = ToolKind.Node,
            Target = "24",
            From = "18"
        });

        Assert.Single(matching.Edits);
    }

    [Fact]
    public void ApplyWritesTheFileAndKeepsABackup()
    {
        var path = Write("api/.nvmrc", "18.20.2\n");

        var project = Project();
        project.Path = Path.Combine(_root, "api");

        var plan = MigrationPlanner.Plan([project], new MigrationOptions
        {
            Tool = ToolKind.Node,
            Target = "24"
        });

        var backupRoot = Path.Combine(_root, "backups");
        var (ok, message) = MigrationExecutor.Apply(plan.Edits[0], backupRoot, "test-plan");

        Assert.True(ok, message);
        Assert.Equal("24\n", File.ReadAllText(path));

        var backups = Directory.GetFiles(Path.Combine(backupRoot, "test-plan"));
        Assert.Single(backups);
        Assert.Equal("18.20.2\n", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void ApplyRefusesWhenTheFileChangedAfterPlanning()
    {
        var path = Write("api/.nvmrc", "18.20.2\n");

        var project = Project();
        project.Path = Path.Combine(_root, "api");

        var plan = MigrationPlanner.Plan([project], new MigrationOptions
        {
            Tool = ToolKind.Node,
            Target = "24"
        });

        File.WriteAllText(path, "20.11.0\n");

        var (ok, message) = MigrationExecutor.Apply(plan.Edits[0], Path.Combine(_root, "backups"), "p");

        Assert.False(ok);
        Assert.Contains("changed", message);
        Assert.Equal("20.11.0\n", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyRefusesPathsOutsideTheProject()    {
        var edit = new FileEdit
        {
            ProjectPath = Path.Combine(_root, "api"),
            RelativePath = "../outside.txt",
            Before = "x",
            After = "y"
        };

        Directory.CreateDirectory(Path.Combine(_root, "api"));
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "x");

        var (ok, message) = MigrationExecutor.Apply(edit, Path.Combine(_root, "backups"), "p");

        Assert.False(ok);
        Assert.Contains("escapes", message);
    }

    [Fact]
    public void ReviewedMigrationRejectsChangedContentsAndPreservesThem()
    {
        var file = Write("api/.nvmrc", "18.20.2\n");
        var project = Project();
        project.Path = Path.Combine(_root, "api");
        var store = new SqliteStore(Path.Combine(_root, "store"));
        store.Save(new IndexSnapshot { Projects = [project] });
        var options = new MigrationOptions { Tool = ToolKind.Node, Target = "24" };
        var plan = MigrationPlanner.Plan([project], options);
        File.WriteAllText(file, "20.11.0\n");
        var result = MigrationExecution.Apply(store, options, plan.Fingerprint);
        Assert.False(result.Succeeded);
        Assert.Contains("changed", result.Error);
        Assert.Equal("20.11.0\n", File.ReadAllText(file));
        Assert.Empty(store.ReadAudit());
    }

    [Fact]
    public void ReviewedMigrationWritesBackupAndAudit()
    {
        var file = Write("api/.nvmrc", "18.20.2\n");
        var project = Project();
        project.Path = Path.Combine(_root, "api");
        var store = new SqliteStore(Path.Combine(_root, "store"));
        store.Save(new IndexSnapshot { Projects = [project] });
        var options = new MigrationOptions { Tool = ToolKind.Node, Target = "24" };
        var plan = MigrationPlanner.Plan([project], options);
        var result = MigrationExecution.Apply(store, options, plan.Fingerprint);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("24\n", File.ReadAllText(file));
        Assert.Equal("18.20.2\n", File.ReadAllText(Assert.Single(Directory.GetFiles(result.BackupDirectory!))));
        Assert.Contains(store.ReadAudit(), entry => entry.Action.Contains("migrate", StringComparison.Ordinal));
    }

    [Fact]
    public void GradleWrapperIsPlannedAndBackedUpFromItsNestedPath()
    {
        var file = Write("api/gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.8-bin.zip\n");
        var project = Project();
        project.Path = Path.Combine(_root, "api");
        var store = new SqliteStore(Path.Combine(_root, "store"));
        store.Save(new IndexSnapshot { Projects = [project] });
        var options = new MigrationOptions { Tool = ToolKind.Gradle, Target = "9.1.0" };
        var plan = MigrationPlanner.Plan([project], options);
        Assert.Single(plan.Edits);
        var result = MigrationExecution.Apply(store, options, plan.Fingerprint);
        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("gradle-9.1.0-bin.zip", File.ReadAllText(file));
        Assert.Contains("gradle-8.8-bin.zip", File.ReadAllText(Assert.Single(Directory.GetFiles(result.BackupDirectory!))));
    }

    [Fact]
    public void InvalidVersionFiltersNeverExpandMigrationScope()
    {
        var project = Project();
        Assert.Throws<ArgumentException>(() => MigrationPlanner.Plan([project], new MigrationOptions
        { Tool = ToolKind.Node, Target = "24", From = "invalid" }));
        Assert.False(MigrationPlanner.IsValidTarget("24\ninjected"));
    }

    [Fact]
    public void InvalidUtf8IsSkippedWithoutChangingItsBytes()
    {
        var file = Write("api/.nvmrc", "18\n");
        byte[] bytes = [0xff, 0xfe, 0x31, 0x38];
        File.WriteAllBytes(file, bytes);
        var project = Project();
        project.Path = Path.Combine(_root, "api");
        var plan = MigrationPlanner.Plan([project], new MigrationOptions { Tool = ToolKind.Node, Target = "24" });
        Assert.Empty(plan.Edits);
        Assert.Single(plan.Skipped);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public void SymbolicLinkIsSkippedRatherThanEdited()
    {
        if (OperatingSystem.IsWindows()) return;
        var outside = Write("outside", "18.20.2\n");
        var project = Project();
        project.Path = Path.Combine(_root, "api");
        Directory.CreateDirectory(project.Path);
        File.CreateSymbolicLink(Path.Combine(project.Path, ".nvmrc"), outside);
        var plan = MigrationPlanner.Plan([project], new MigrationOptions { Tool = ToolKind.Node, Target = "24" });
        Assert.Empty(plan.Edits);
        Assert.Contains(plan.Skipped, item => item.Contains("symbolic link", StringComparison.Ordinal));
        Assert.Equal("18.20.2\n", File.ReadAllText(outside));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
