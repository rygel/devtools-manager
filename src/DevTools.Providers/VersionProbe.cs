namespace DevTools.Providers;

/// <summary>Runs a fixed version argument, including safe Windows batch launchers.</summary>
internal static class VersionProbe
{
    public static ProcessResult Run(string executable, string argument, string? workingDirectory, IProcessRunner? runner)
    {
        var processRunner = runner ?? ProcessRunner.Shared;
        if (OperatingSystem.IsWindows() && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                                            executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            if (executable.IndexOfAny(['"', '%', '!', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
                return new ProcessResult { ExitCode = -1, Error = "The batch launcher path contains characters that cannot be safely passed to cmd.exe." };
            var shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            return processRunner.Run(shell, ["/d", "/v:off", "/c", executable, argument], TimeSpan.FromSeconds(10), workingDirectory);
        }
        return processRunner.Run(executable, [argument], TimeSpan.FromSeconds(10), workingDirectory);
    }
}
