using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Scan;
using Xunit;

namespace DevTools.Tests;

public class ResolutionTests
{
    [Fact]
    public void StaleToolchainsDoNotSatisfyRequirements()
    {
        var node = Node("20.0.0");
        node.Stale = true;
        var resolution = new ResolutionEngine([node]).Resolve(Project(Requirement.Pinned(ToolKind.Node, "20.0.0", Provenance.FileOf(".nvmrc"))));
        Assert.Equal(ResolutionStatus.Missing, Assert.Single(resolution.Tools).Status);
    }

    private static Toolchain Node(string version, string manager = "fnm", bool global = false) => new()
    {
        Tool = ToolKind.Node, Version = version, Path = "/tools/node/" + version,
        Manager = manager, IsGlobalDefault = global, Health = ToolchainHealth.Ok
    };

    private static Toolchain Jdk(string version) => new()
    {
        Tool = ToolKind.Jdk, Version = version, Path = "/tools/jdk/" + version,
        Manager = "sdkman", Health = ToolchainHealth.Ok
    };

    private static ProjectRecord Project(params Requirement[] requirements) => new()
    {
        Key = "/p", Path = "/p", Name = "p", State = ProjectState.Active,
        Requirements = requirements.ToList()
    };

    [Fact]
    public void ExactPinThatIsInstalledIsSatisfied()
    {
        var engine = new ResolutionEngine([Node("18.20.2"), Node("20.11.0")]);
        var project = Project(Requirement.Pinned(ToolKind.Node, "18.20.2", Provenance.FileOf(".nvmrc", 1)));

        var resolution = engine.Resolve(project);

        var node = Assert.Single(resolution.Tools);
        Assert.Equal(ResolutionStatus.Satisfied, node.Status);
        Assert.Equal("18.20.2", node.Toolchain!.Version);
        Assert.False(resolution.HasProblems);
    }

    [Fact]
    public void MissingVersionOffersTheNearestInstalled()
    {
        var engine = new ResolutionEngine([Node("20.11.0"), Node("22.4.0")]);
        var project = Project(Requirement.Pinned(ToolKind.Node, "18.20.2", Provenance.FileOf(".nvmrc", 1)));

        var resolution = engine.Resolve(project);

        var node = Assert.Single(resolution.Tools);
        Assert.Equal(ResolutionStatus.Missing, node.Status);
        Assert.NotNull(node.Nearest);
        Assert.Equal("20.11.0", node.Nearest!.Version);
        Assert.True(resolution.HasProblems);
    }

    [Fact]
    public void RangeIsSatisfiedByAnyVersionInsideIt()
    {
        var engine = new ResolutionEngine([Node("20.11.0")]);
        var project = Project(Requirement.Advisory(ToolKind.Node, ">=18 <21", Provenance.FileOf("package.json")));

        Assert.Equal(ResolutionStatus.Satisfied, engine.Resolve(project).Tools[0].Status);
    }

    [Fact]
    public void ToolInstalledButOutsideRangeIsUnsatisfiedRange()
    {
        var engine = new ResolutionEngine([Node("22.4.0")]);
        var project = Project(Requirement.Declared(ToolKind.Python, ">=3.10 <3.13", Provenance.FileOf("pyproject.toml")));
        var pythonEngine = new ResolutionEngine([
            new Toolchain { Tool = ToolKind.Python, Version = "3.13.0", Path = "/py", Health = ToolchainHealth.Ok }
        ]);

        var resolution = pythonEngine.Resolve(project);

        Assert.Equal(ResolutionStatus.UnsatisfiedRange, resolution.Tools[0].Status);
    }

    [Fact]
    public void PinWinsOverEnginesAndTheConflictIsReported()
    {
        var engine = new ResolutionEngine([Node("18.20.2"), Node("20.11.0")]);
        var project = Project(
            Requirement.Advisory(ToolKind.Node, ">=20", Provenance.FileOf("package.json", 5, "engines.node")),
            Requirement.Pinned(ToolKind.Node, "18.20.2", Provenance.FileOf(".nvmrc", 1)));

        var resolution = engine.Resolve(project);
        var node = Assert.Single(resolution.Tools);

        Assert.Equal(ResolutionStatus.Satisfied, node.Status);
        Assert.Equal("18.20.2", node.Toolchain!.Version);
        Assert.Contains(node.Diagnostics, d => d.Code == "conflicting-declaration");
    }

    [Fact]
    public void UnknownConstraintIsReportedNotGuessed()
    {
        var engine = new ResolutionEngine([Node("20.11.0")]);
        var project = Project(Requirement.Declared(ToolKind.Node, "not-a-version", Provenance.FileOf(".nvmrc", 1)));

        var resolution = engine.Resolve(project);

        Assert.Equal(ResolutionStatus.Unknown, resolution.Tools[0].Status);
        Assert.NotNull(resolution.Tools[0].Detail);
    }

    [Fact]
    public void NothingInstalledAtAllIsMissing()
    {
        var engine = new ResolutionEngine([]);
        var project = Project(Requirement.Declared(ToolKind.Jdk, "17", Provenance.FileOf("pom.xml")));

        Assert.Equal(ResolutionStatus.Missing, engine.Resolve(project).Tools[0].Status);
    }

    [Fact]
    public void StubToolchainsAreIgnored()
    {
        var stub = new Toolchain
        {
            Tool = ToolKind.Python, Version = "0.0.0", Path = "/store",
            Health = ToolchainHealth.Stub
        };

        var engine = new ResolutionEngine([stub]);
        Assert.Empty(engine.Installed(ToolKind.Python));
    }

    [Fact]
    public void CrossProjectConflictIsDetected()
    {
        var a = Project(Requirement.Pinned(ToolKind.Node, "18.20.2", Provenance.FileOf(".nvmrc")));
        a.Key = "/a"; a.Name = "a";

        var b = Project(Requirement.Pinned(ToolKind.Node, "20.11.0", Provenance.FileOf(".nvmrc")));
        b.Key = "/b"; b.Name = "b";

        var conflicts = ResolutionEngine.FindConflicts([a, b]);

        Assert.Single(conflicts);
        Assert.Equal(ToolKind.Node, conflicts[0].Tool);
    }

    [Fact]
    public void JdkMajorMatchingHandlesModernAndLegacySchemes()
    {
        var engine = new ResolutionEngine([Jdk("17.0.10"), Jdk("21.0.2")]);
        var project = Project(Requirement.Declared(ToolKind.Jdk, "17", Provenance.FileOf("pom.xml")));

        var resolution = engine.Resolve(project);

        Assert.Equal(ResolutionStatus.Satisfied, resolution.Tools[0].Status);
        Assert.Equal("17.0.10", resolution.Tools[0].Toolchain!.Version);
    }
}

public class ConfidenceAndPruningTests
{
    private static DiscoveredDirectory Candidate(string path, params string[] markers)
    {
        var directory = new DiscoveredDirectory { Path = path.Replace('/', Path.DirectorySeparatorChar) };
        directory.MarkerFiles.AddRange(markers);
        return directory;
    }

    [Fact]
    public void ARealProjectPassesTheThreshold()
    {
        var (score, reason) = Confidence.Evaluate(Candidate("/code/api", "package.json"));

        Assert.True(score >= Confidence.Threshold);
        Assert.Null(reason);
    }

    [Fact]
    public void FixturesAreSuppressedWithAReason()
    {
        var (score, reason) = Confidence.Evaluate(Candidate("/code/api/test/fixtures/thing", "package.json"));

        Assert.True(score < Confidence.Threshold);
        Assert.Contains("fixture", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitAndLockfileRaiseConfidence()
    {
        var bare = Candidate("/code/api", "package.json");
        var withGit = Candidate("/code/api", "package.json");
        withGit.MarkerDirectories.Add(".git");
        withGit.MarkerFiles.Add("pnpm-lock.yaml");

        Assert.True(Confidence.Evaluate(withGit).Score > Confidence.Evaluate(bare).Score);
    }

    [Fact]
    public void CacheDirectoriesArePruned()
    {
        var policy = new PruningPolicy();

        Assert.True(policy.Evaluate("/code/api/node_modules", "node_modules", 3, true).ShouldPrune);
        Assert.True(policy.Evaluate("/code/api/target", "target", 3, true).ShouldPrune);
        Assert.True(policy.Evaluate("C:\\Windows", "Windows", 1, false, parentIsFileSystemRoot: true).ShouldPrune);
        Assert.False(policy.Evaluate("C:\\Develop\\bin", "bin", 1, false, parentIsFileSystemRoot: false).ShouldPrune);
        Assert.False(policy.Evaluate("/code/api/src", "src", 3, true).ShouldPrune);
    }

    [Fact]
    public void MarkerDirectoriesSurviveTheHiddenRule()
    {
        var policy = new PruningPolicy();

        Assert.False(policy.Evaluate("/code/api/.mvn", ".mvn", 2, true).ShouldPrune);
        Assert.True(policy.Evaluate("/code/api/.git", ".git", 2, true).ShouldPrune);
    }

    [Fact]
    public void BuildOutputsArePrunedOnlyInsideProjects()
    {
        var policy = new PruningPolicy();

        Assert.True(policy.Evaluate("/code/api/bin", "bin", 2, true).ShouldPrune);
        Assert.False(policy.Evaluate("/code/tools/bin", "bin", 2, false).ShouldPrune);
    }
}
