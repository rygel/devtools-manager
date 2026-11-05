using System.Text;
using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class ManualToolchainTests
{
    [Theory]
    [InlineData(ToolKind.Maven, "apache-maven-3.9.12", "mvn.cmd", "Apache Maven 3.9.12", "3.9.12")]
    [InlineData(ToolKind.Gradle, "gradle-8.7", "gradle.bat", "Gradle 8.7", "8.7")]
    public void WindowsBatchLauncherCanBeProbedWithoutOpeningAWindow(ToolKind tool, string folder, string launcher, string output, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"), "path with spaces", folder);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", launcher), "@echo off\r\necho " + output + "\r\n");
            var (version, error) = ManualToolchainDiscovery.Verify(new Toolchain { Tool = tool, Path = root });
            Assert.Null(error);
            Assert.Equal(expected, version);
        }
        finally { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, true); }
    }

    [Fact]
    public void WindowsBatchPathsWithShellMetacharactersAreNeverExecuted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new RecordingRunner();
        var result = VersionProbe.Run(@"C:\unsafe&name\mvn.cmd", "-version", null, runner);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Null(runner.FileName);
    }

    [Fact]
    public async Task UnavailableRootRetainsItsToolchainAsStale()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var previous = new IndexSnapshot { Toolchains = [new Toolchain
        {
            Tool = ToolKind.Jdk, Version = "21", Path = Path.Combine(root, "jdk-21"), Health = ToolchainHealth.Ok
        }] };
        var snapshot = await new ScanService(_ => new EmptyInventory()).ScanAsync(new ScanRequest { Roots = [root] }, previous);
        Assert.True(snapshot.Stats.Partial);
        Assert.True(Assert.Single(snapshot.Toolchains).Stale);
    }

    [Fact]
    public async Task ScanFindsUnpackedToolchainWithoutExecutingOrTrustingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "jdk-21.0.2");
        var executable = Path.Combine(install, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "not an executable");
            var snapshot = await new ScanService(_ => new EmptyInventory()).ScanAsync(new ScanRequest { Roots = [root] });
            var found = Assert.Single(snapshot.Toolchains);
            Assert.Equal(ToolchainHealth.Unverified, found.Health);
            var project = new ProjectRecord { Requirements = [Requirement.Pinned(ToolKind.Jdk, "21.0.2", Provenance.FileOf(".java-version"))] };
            Assert.Equal(ResolutionStatus.Missing, Assert.Single(new ResolutionEngine(snapshot.Toolchains).Resolve(project).Tools).Status);
            var (version, error) = ManualToolchainDiscovery.Verify(found, new FakeRunner());
            Assert.Null(error);
            Assert.Equal("21.0.3", version);
            var info = new FileInfo(executable);
            var verified = new Toolchain { Tool = ToolKind.Jdk, Version = version!, Path = install, Manager = "manual",
                Health = ToolchainHealth.Ok, VerifiedExecutableSize = info.Length, VerifiedExecutableModifiedUtc = info.LastWriteTimeUtc };
            Assert.Equal(ToolchainHealth.Ok, Assert.Single(ManualToolchainDiscovery.Discover([install], [verified])).Health);
            File.AppendAllText(executable, " changed");
            Assert.Equal(ToolchainHealth.Unverified, Assert.Single(ManualToolchainDiscovery.Discover([install], [verified])).Health);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScanFindsArbitrarilyNamedInstallationWithoutGuessingItsVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "custom-tools");
        try
        {
            var bin = Path.Combine(install, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "mvn.cmd" : "mvn"), "not executable");
            var snapshot = await new ScanService(_ => new EmptyInventory()).ScanAsync(new ScanRequest { Roots = [root] });
            var candidate = Assert.Single(snapshot.Toolchains);
            Assert.Equal(ToolKind.Maven, candidate.Tool);
            Assert.Equal("unknown", candidate.Version);
            Assert.Equal(ToolchainHealth.Unverified, candidate.Health);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ScanningABinRootDoesNotAddItsParentOutsideTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), "fixture");
            var snapshot = await new ScanService(_ => new EmptyInventory()).ScanAsync(new ScanRequest { Roots = [bin] });
            Assert.Empty(snapshot.Toolchains);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class EmptyInventory : IToolchainInventory
    {
        public IReadOnlyList<Toolchain> Discover(CancellationToken cancellationToken = default) => [];
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Encoding? outputEncoding = null,
            CancellationToken cancellationToken = default) =>
            new() { ExitCode = 0, StdErr = "openjdk version \"21.0.3\"" };
    }

    [Fact]
    public void EffectiveProbeRunsTheSelectedExecutableInTheProjectDirectory()
    {
        var runner = new RecordingRunner();
        var result = EffectiveToolchainProbe.Run(ToolKind.Node, "/tools/node", "/project", runner);
        Assert.Equal("20.11.0", result.Version);
        Assert.Null(result.Error);
        Assert.Equal("/project", runner.WorkingDirectory);
        Assert.Equal("/tools/node", runner.FileName);
        Assert.Equal("--version", Assert.Single(runner.Arguments!));
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public string? WorkingDirectory { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Encoding? outputEncoding = null,
            CancellationToken cancellationToken = default)
        {
            FileName = fileName;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            return new ProcessResult { ExitCode = 0, StdOut = "v20.11.0" };
        }
    }
}
