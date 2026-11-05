using DevTools.Providers;
using Xunit;

namespace DevTools.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public void CancellationStopsABoundedChildProcess()
    {
        var executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "ping.exe") : "/bin/sleep";
        string[] arguments = OperatingSystem.IsWindows() ? ["-n", "30", "127.0.0.1"] : ["30"];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = ProcessRunner.Shared.Run(executable, arguments, TimeSpan.FromSeconds(10), cancellationToken: cancellation.Token);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal("cancelled", result.Error);
    }
}
