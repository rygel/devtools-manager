using System.Text;
using System.Text.Json;
using DevTools.Core.Model;
using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class WslSnapshotsTests
{
    [Fact]
    public void ListsDistributionsAsUnicodeWithoutStartingThem()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0, StdOut = "Debian\r\n开发\r\n" } };
        var (names, error) = new WslSnapshots(runner).List();
        Assert.Null(error);
        Assert.Collection(names, name => Assert.Equal("Debian", name), name => Assert.Equal("开发", name));
        Assert.Equal(Encoding.Unicode, runner.Encoding);
    }

    [Fact]
    public void LoadsOnlyAnIndexMatchingTheSelectedDistribution()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0, StdOut = "{\"environmentName\":\"WSL: Debian\",\"projects\":[],\"toolchains\":[]}" } };
        var service = new WslSnapshots(runner);
        Assert.NotNull(service.Load("Debian").Snapshot);
        Assert.Null(service.Load("Other").Snapshot);
        Assert.Equal("Other", runner.Arguments![1]);
    }

    [Fact]
    public void EnvironmentPassesProjectPathAsAnArgumentRatherThanShellCode()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0, StdOut = "export X=1" } };
        var (script, error) = new WslSnapshots(runner).Environment("Debian", "/home/user/project $(touch unsafe)");
        Assert.Null(error);
        Assert.Equal("export X=1", script);
        Assert.Contains("/home/user/project $(touch unsafe)", runner.Arguments!);
        Assert.DoesNotContain("touch unsafe", runner.Arguments![5]);
    }

    [Fact]
    public void CleanupUsesTheSelectedTargetsReviewedToken()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0,
            StdOut = "{\"targets\":[{\"path\":\"/project/node_modules\",\"sizeBytes\":7,\"plan\":\"ABC\",\"available\":true}]}" } };
        var service = new WslSnapshots(runner);
        var (targets, error) = service.CleanupPlan("Debian", "/project");
        Assert.Null(error);
        var target = Assert.Single(targets);
        Assert.Equal(7, target.SizeBytes);
        var result = service.Cleanup("Debian", "/project", target);
        Assert.True(result.Succeeded);
        Assert.Contains("--artifact", runner.Arguments!);
        Assert.Contains("node_modules", runner.Arguments!);
        Assert.Contains("ABC", runner.Arguments!);
    }

    [Fact]
    public void WslScanUsesConfiguredRootsAndPassesMeasurementOption()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0, StdOut = "{}" } };
        var result = new WslSnapshots(runner).Scan("Debian", measure: true);
        Assert.True(result.Succeeded);
        Assert.Contains("scan", runner.Arguments!);
        Assert.Contains("--measure", runner.Arguments!);
        Assert.Contains("--json", runner.Arguments!);
    }

    [Fact]
    public void VerificationReturnsTheVersionProbeResult()
    {
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0, StdOut = "Verified jdk 21.0.3" } };
        var result = new WslSnapshots(runner).Verify("Debian", "/jdk");
        Assert.True(result.Succeeded);
        Assert.Equal("Verified jdk 21.0.3", result.Message);
    }

    [Fact]
    public void WslInstallUsesTheReviewedSingleOptionToken()
    {
        var original = new PlannedStep { Tool = "node", Version = "20", FileName = "fnm",
            Arguments = ["install", "20"], RequiresNetwork = true, Projects = ["example"] };
        var runner = new FakeRunner { Result = new ProcessResult { ExitCode = 0,
            StdOut = JsonSerializer.Serialize(new InstallPlan { Steps = [original] }) } };
        var service = new WslSnapshots(runner);
        var plan = service.InstallationPlan("Debian").Plan;
        var step = Assert.Single(Assert.IsType<InstallPlan>(plan).Steps);
        Assert.Equal(original.Plan, step.Plan);
        Assert.True(service.Install("Debian", step).Succeeded);
        Assert.Contains(original.Plan, runner.Arguments!);
        Assert.Contains("--yes", runner.Arguments!);
    }

    [Fact]
    public void WslMigrationDisplaysChangesAndAppliesReviewedToken()
    {
        var options = new MigrationOptions { Tool = ToolKind.Node, Target = "24", IncludeRanges = true };
        var planningRunner = new FakeRunner { Result = new ProcessResult { ExitCode = 0,
            StdOut = "{\"plan\":\"TOKEN\",\"edits\":[{\"project\":\"app\",\"path\":\"/app/.nvmrc\",\"change\":\"20 to 24\",\"before\":\"20\\n\",\"after\":\"24\\n\"}],\"skipped\":[]}" } };
        var plan = new WslSnapshots(planningRunner).MigrationPlan("Debian", options).Plan;
        var edit = Assert.Single(Assert.IsType<WslMigrationPlan>(plan).Edits);
        Assert.Equal("20\n", edit.Before);
        Assert.Equal("24\n", edit.After);
        var applyingRunner = new FakeRunner { Result = new ProcessResult { ExitCode = 0,
            StdOut = "{\"items\":[{}],\"backupDirectory\":\"/backups/id\"}" } };
        var result = new WslSnapshots(applyingRunner).Migrate("Debian", options, plan.Token);
        Assert.True(result.Succeeded);
        Assert.Contains("/backups/id", result.Message);
        Assert.Contains("TOKEN", applyingRunner.Arguments!);
        Assert.Contains("--include-ranges", applyingRunner.Arguments!);
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessResult Result { get; init; } = new();
        public Encoding? Encoding { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Encoding? outputEncoding = null,
            CancellationToken cancellationToken = default)
        {
            Encoding = outputEncoding;
            Arguments = arguments;
            return Result;
        }
    }
}
