using System.Diagnostics;
using System.Text;

namespace DevTools.Providers;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool TimedOut { get; init; }
    public string? Error { get; init; }

    public bool Succeeded => Error is null && !TimedOut && ExitCode == 0;

    public string Combined => StdOut + "\n" + StdErr;
}

public interface IProcessRunner
{
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, Encoding? outputEncoding = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a probe command with stdin closed, no shell, no window, and a hard timeout.
/// A broken toolchain install must never hang a scan: <c>java -version</c> on a missing
/// DLL pops a dialog and waits forever otherwise.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static ProcessRunner Shared { get; } = new();

    public ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        Encoding? outputEncoding = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return new ProcessResult { ExitCode = -1, Error = "cancelled" };
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = outputEncoding ?? Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? NeutralWorkingDirectory
            };

            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            if (environment is not null)
            {
                foreach (var (key, value) in environment) startInfo.Environment[key] = value;
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new ProcessResult { ExitCode = -1, Error = "process did not start" };

            process.StandardInput.Close();

            // Drain both pipes even if cancellation occurs, so the child cannot block on full output buffers.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try { process.WaitForExitAsync(timeoutSource.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* already gone */ }
                try { process.WaitForExit(2000); }
                catch { /* process already exited */ }
                var cancelled = cancellationToken.IsCancellationRequested;
                return new ProcessResult { ExitCode = -1, TimedOut = !cancelled, Error = cancelled ? "cancelled" : "timeout" };
            }

            // Both streams may still be draining; give them a moment but never block forever.
            var drained = System.Threading.Tasks.Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(2));

            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StdOut = drained ? stdoutTask.Result : "",
                StdErr = drained ? stderrTask.Result : ""
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, Error = ex.GetType().Name + ": " + ex.Message };
        }
    }

    /// <summary>
    /// Tools such as <c>mvn</c> and <c>dotnet</c> read project-local configuration.
    /// Probing from a project directory would report the project's version, not the machine's.
    /// </summary>
    public static string NeutralWorkingDirectory { get; } = Path.GetTempPath();
}

/// <summary>Resolves an executable name against PATH, including Windows' PATHEXT.</summary>
public static class PathSolver
{
    public static string? Which(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (Path.IsPathRooted(command))
            return File.Exists(command) ? command : null;

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains('/'))
        {
            var candidate = Path.GetFullPath(command);
            return File.Exists(candidate) ? candidate : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(trimmed, command + extension.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;

                    if (extension.Length > 0)
                    {
                        var upper = Path.Combine(trimmed, command + extension.ToUpperInvariant());
                        if (File.Exists(upper)) return upper;
                    }
                }
                catch
                {
                    // Malformed PATH entry: keep looking.
                }
            }
        }

        return null;
    }
}

/// <summary>Where this application keeps its own state, per platform.</summary>
public static class PlatformPaths
{
    public static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ApplicationData
    {
        get
        {
            // A portable/CI override: keeps a demo or a test run away from the real index.
            if (Environment.GetEnvironmentVariable("DEVTOOLS_HOME") is { Length: > 0 } overridden)
                return overridden;

            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "devtools");
            }

            if (OperatingSystem.IsMacOS())
            {
                return Path.Combine(Home, "Library", "Application Support", "devtools");
            }

            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            return Path.Combine(string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home, ".local", "share") : xdg, "devtools");
        }
    }

    public static void EnsureCreated()
    {
        try { Directory.CreateDirectory(ApplicationData); }
        catch { /* read-only home: callers degrade to in-memory */ }
    }

    /// <summary>Expands <c>~</c> and environment variables in a user-supplied path.</summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var expanded = Environment.ExpandEnvironmentVariables(path);

        if (expanded == "~" )
            return Home;

        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Home, expanded[2..]);

        return expanded;
    }

    /// <summary>Resolves a candidate that may contain <c>~</c> or use forward slashes on Windows.</summary>
    public static string? ExistingDirectory(string candidate)
    {
        try
        {
            var path = Expand(candidate);
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    public static string? ExistingFile(string candidate)
    {
        try
        {
            var path = Expand(candidate);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }
}
