using DevTools.Core.Model;

namespace DevTools.Providers;

public sealed class PythonProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Python;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        foreach (var (root, manager) in new (string, string)[]
                 {
                     (Path.Combine(home, ".pyenv", "versions"), "pyenv"),
                     (Path.Combine(home, ".asdf", "installs", "python"), "asdf"),
                     (Path.Combine(home, ".local", "share", "mise", "installs", "python"), "mise"),
                     (Path.Combine(home, ".local", "share", "uv", "python"), "uv"),
                     (Path.Combine(home, ".local", "share", "uv", "pythons"), "uv")
                 })
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                var version = InventoryHelpers.VersionFromName(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Python, version, directory, manager,
                    Path.GetFileName(directory));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var localPrograms = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");

            foreach (var directory in InventoryHelpers.SafeDirectories(localPrograms))
            {
                // python.org folders are named "Python311" (3.11), never "3.11": a bare integer would read as 311.
                var name = Path.GetFileName(directory);
                var executable = Path.Combine(directory, "python.exe");
                if (!File.Exists(executable)) continue;

                string? version = null;
                if (context.AllowProbing)
                {
                    var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout, ProcessRunner.NeutralWorkingDirectory);
                    version = InventoryHelpers.VersionTokenIn(result.Combined);
                }

                version ??= PythonInstallFolders.Parse(name);
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Python, version, directory, "system", name);
            }
        }

        if (!context.AllowProbing) yield break;

        foreach (var name in new[] { "python3", "python" })
        {
            var executable = PathSolver.Which(name);
            if (executable is null) continue;

            // The Windows Store alias is a stub that opens the store: never a real interpreter.
            if (executable.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                yield return InventoryHelpers.Create(ToolKind.Python, "0.0.0", executable, "stub",
                    health: ToolchainHealth.Stub,
                    healthDetail: "Windows Store alias, not a real installation");
                break;
            }

            var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
                ProcessRunner.NeutralWorkingDirectory);
            var version = InventoryHelpers.VersionTokenIn(result.Combined);
            if (version is null) continue;

            yield return InventoryHelpers.Create(ToolKind.Python, version,
                Path.GetDirectoryName(executable) ?? executable, "system", version, isGlobalDefault: true);
            break;
        }
    }
}

public sealed partial class PythonInstallFolders
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^Python(\d)(\d+)$")]
    private static partial System.Text.RegularExpressions.Regex Folder();

    /// <summary>"Python311" → "3.11". Null for any other name (for example "Launcher").</summary>
    public static string? Parse(string name)
    {
        var match = Folder().Match(name);
        return match.Success ? match.Groups[1].Value + "." + match.Groups[2].Value : null;
    }
}

public sealed class GoProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Go;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (context.AllowProbing)
        {
            var executable = PathSolver.Which("go");
            if (executable is not null)
            {
                var versionResult = context.Runner.Run(executable, ["version"], context.ProbeTimeout,
                    ProcessRunner.NeutralWorkingDirectory);
                var version = InventoryHelpers.VersionTokenIn(versionResult.Combined);

                var rootResult = context.Runner.Run(executable, ["env", "GOROOT"], context.ProbeTimeout,
                    ProcessRunner.NeutralWorkingDirectory);
                var root = rootResult.StdOut.Trim();

                if (version is not null)
                {
                    seen.Add(version);
                    yield return InventoryHelpers.Create(ToolKind.Go, version,
                        Directory.Exists(root) ? root : Path.GetDirectoryName(executable) ?? executable,
                        "system", version, isGlobalDefault: true);
                }
            }
        }

        // GOTOOLCHAIN-managed SDKs live under ~/sdk/go1.22.3.
        foreach (var directory in InventoryHelpers.SafeDirectories(Path.Combine(home, "sdk")))
        {
            var name = Path.GetFileName(directory);
            var version = InventoryHelpers.VersionTokenIn(name);
            if (version is null || !seen.Add(version)) continue;

            yield return InventoryHelpers.Create(ToolKind.Go, version, directory, "gotoolchain", name);
        }
    }
}

/// <summary>Rust toolchains from rustup, plus a system install.</summary>
public sealed class RustProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Rustc;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        foreach (var directory in InventoryHelpers.SafeDirectories(Path.Combine(home, ".rustup", "toolchains")))
        {
            var name = Path.GetFileName(directory);
            string? version = InventoryHelpers.VersionFromName(name);
            var channel = name.Split('-')[0];

            // "stable-x86_64-..." is a channel, not a version: ask the toolchain itself.
            if (version is null && context.AllowProbing)
            {
                var rustc = InventoryHelpers.HasBinary(directory, "bin/rustc.exe", "bin/rustc")
                    ? Path.Combine(directory, "bin", OperatingSystem.IsWindows() ? "rustc.exe" : "rustc")
                    : null;

                if (rustc is not null)
                {
                    var result = context.Runner.Run(rustc, ["--version"], context.ProbeTimeout,
                        ProcessRunner.NeutralWorkingDirectory);
                    version = InventoryHelpers.VersionTokenIn(result.Combined);
                }
            }

            if (version is null) continue;

            yield return InventoryHelpers.Create(ToolKind.Rustc, version, directory, "rustup", channel,
                isGlobalDefault: channel is "stable" or "default");
        }

        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which("rustc");
        if (executable is null) yield break;

        var probed = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);
        var systemVersion = InventoryHelpers.VersionTokenIn(probed.Combined);
        if (systemVersion is null) yield break;

        yield return InventoryHelpers.Create(ToolKind.Rustc, systemVersion,
            Path.GetDirectoryName(executable) ?? executable, "system", systemVersion);
    }
}

public sealed class UvProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Uv;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which("uv");
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);

        var version = InventoryHelpers.VersionTokenIn(result.Combined);
        if (version is null) yield break;

        yield return InventoryHelpers.Create(ToolKind.Uv, version,
            Path.GetDirectoryName(executable) ?? executable, "system", version, isGlobalDefault: true);
    }
}

/// <summary>
/// A tool with no version-manager ecosystem worth modelling: find it on PATH, ask it its
/// version. Without this, a PHP or Dart requirement could never be satisfied even when the
/// interpreter is installed.
/// </summary>
public sealed class PathProbeProvider : IToolchainProvider
{
    private readonly string _executable;
    private readonly string[] _arguments;

    public PathProbeProvider(ToolKind tool, string executable, params string[] arguments)
    {
        Tool = tool;
        _executable = executable;
        _arguments = arguments.Length == 0 ? ["--version"] : arguments;
    }

    public ToolKind Tool { get; }

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which(_executable);
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, _arguments, context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);

        var version = InventoryHelpers.VersionTokenIn(result.Combined);
        if (version is null) yield break;

        yield return InventoryHelpers.Create(Tool, version,
            Path.GetDirectoryName(executable) ?? executable, "system", version, isGlobalDefault: true);
    }
}

public sealed class RubyProvider : IToolchainProvider{
    public ToolKind Tool => ToolKind.Ruby;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        foreach (var (root, manager) in new (string, string)[]
                 {
                     (Path.Combine(home, ".rbenv", "versions"), "rbenv"),
                     (Path.Combine(home, ".rvm", "rubies"), "rvm"),
                     (Path.Combine(home, ".asdf", "installs", "ruby"), "asdf"),
                     (Path.Combine(home, ".local", "share", "mise", "installs", "ruby"), "mise")
                 })
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                var version = InventoryHelpers.VersionFromName(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Ruby, version, directory, manager,
                    Path.GetFileName(directory));
            }
        }

        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which("ruby");
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);
        var probed = InventoryHelpers.VersionTokenIn(result.Combined);
        if (probed is null) yield break;

        yield return InventoryHelpers.Create(ToolKind.Ruby, probed,
            Path.GetDirectoryName(executable) ?? executable, "system", probed, isGlobalDefault: true);
    }
}
