using System.Text.RegularExpressions;
using DevTools.Core.Model;

namespace DevTools.Providers;

/// <summary>
/// JDK discovery across SDKMAN, IntelliJ/GraalVM's <c>~/.jdks</c>, the platform install
/// roots, and <c>JAVA_HOME</c>. Vendor differences never change the version we report,
/// only the path we report it from.
/// </summary>
public sealed partial class JdkProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Jdk;

    [GeneratedRegex(@"^(\d+(?:\.\d+)*)\s+", RegexOptions.Multiline)]
    private static partial Regex VersionPrefix();

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");

        // SDKMAN: the candidate directory name *is* the identifier ("21.0.2-tem").
        foreach (var directory in InventoryHelpers.SafeDirectories(PlatformPaths.Expand("~/.sdkman/candidates/java")))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, "current", StringComparison.OrdinalIgnoreCase)) continue;

            var version = InventoryHelpers.VersionTokenIn(name);
            if (version is null) continue;

            yield return InventoryHelpers.Create(ToolKind.Jdk, version, directory, "sdkman", name,
                isGlobalDefault: IsSameAsJavaHome(directory, javaHome));
        }

        // IntelliJ-managed JDKs and GraalVM installers.
        foreach (var directory in InventoryHelpers.SafeDirectories(PlatformPaths.Expand("~/.jdks")))
        {
            var version = InventoryHelpers.VersionTokenIn(Path.GetFileName(directory));
            if (version is null) continue;

            yield return InventoryHelpers.Create(ToolKind.Jdk, version, directory, "jdks", Path.GetFileName(directory),
                isGlobalDefault: IsSameAsJavaHome(directory, javaHome));
        }

        // Platform install roots: each immediate child is a JDK home.
        string[] roots = OperatingSystem.IsWindows()
            ?
            [
                @"C:\Program Files\Java",
                @"C:\Program Files\Eclipse Adoptium",
                @"C:\Program Files\Microsoft",
                @"C:\Program Files\Zulu",
                @"C:\Program Files\Amazon Corretto",
                @"C:\Program Files\BellSoft",
                @"C:\Program Files\Semeru"
            ]
            : OperatingSystem.IsMacOS()
                ?
                [
                    "/Library/Java/JavaVirtualMachines",
                    Path.Combine(home, "Library", "Java", "JavaVirtualMachines")
                ]
                : ["/usr/lib/jvm", "/opt/java", "/usr/java"];

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                // macOS bundles nest the real home one level down.
                var home_ = Path.Combine(directory, "Contents", "Home");
                var candidate = Directory.Exists(home_) ? home_ : directory;

                var version = InventoryHelpers.VersionTokenIn(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Jdk, version, candidate, "system",
                    Path.GetFileName(directory), isGlobalDefault: IsSameAsJavaHome(candidate, javaHome));
            }
        }

        // JAVA_HOME itself, when it points somewhere we have not already covered.
        if (javaHome is not null && Directory.Exists(javaHome))
        {
            var version = InventoryHelpers.VersionTokenIn(javaHome);
            if (version is not null)
            {
                yield return InventoryHelpers.Create(ToolKind.Jdk, version, javaHome, "system",
                    Path.GetFileName(javaHome.TrimEnd(Path.DirectorySeparatorChar)), isGlobalDefault: true);
            }
        }
    }

    private static bool IsSameAsJavaHome(string directory, string? javaHome)
    {
        if (string.IsNullOrEmpty(javaHome)) return false;

        try
        {
            return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(Path.GetFullPath(javaHome).TrimEnd(Path.DirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Maven and Gradle, from the two places they actually live: a wrapper distribution
/// unpacked into a cache, or a manager install.
/// </summary>
public sealed class BuildToolProvider(ToolKind tool) : IToolchainProvider
{
    public ToolKind Tool => tool;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        if (tool == ToolKind.Gradle)
        {
            foreach (var install in WrapperDistributions(Path.Combine(home, ".gradle", "wrapper", "dists"), "gradle-"))
            {
                var version = VersionFromDistribution(Path.GetFileName(install), "gradle-");
                if (version is null) continue;
                yield return InventoryHelpers.Create(ToolKind.Gradle, version, install, "wrapper", Path.GetFileName(install));
            }

            foreach (var directory in InventoryHelpers.SafeDirectories(PlatformPaths.Expand("~/.sdkman/candidates/gradle")))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals("current", StringComparison.OrdinalIgnoreCase)) continue;
                var version = InventoryHelpers.VersionTokenIn(name);
                if (version is null) continue;
                yield return InventoryHelpers.Create(ToolKind.Gradle, version, directory, "sdkman", name);
            }
        }
        else
        {
            foreach (var install in WrapperDistributions(Path.Combine(home, ".m2", "wrapper", "dists"), "apache-maven-"))
            {
                var version = VersionFromDistribution(Path.GetFileName(install), "apache-maven-");
                if (version is null) continue;
                yield return InventoryHelpers.Create(ToolKind.Maven, version, install, "wrapper", Path.GetFileName(install));
            }

            foreach (var directory in InventoryHelpers.SafeDirectories(PlatformPaths.Expand("~/.sdkman/candidates/maven")))
            {
                var name = Path.GetFileName(directory);
                if (name.Equals("current", StringComparison.OrdinalIgnoreCase)) continue;
                var version = InventoryHelpers.VersionTokenIn(name);
                if (version is null) continue;
                yield return InventoryHelpers.Create(ToolKind.Maven, version, directory, "sdkman", name);
            }
        }

        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which(ToolKinds.Display(tool));
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["-version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);
        var probed = InventoryHelpers.VersionTokenIn(result.Combined);
        if (probed is null) yield break;

        yield return InventoryHelpers.Create(tool, probed, Path.GetDirectoryName(executable) ?? executable, "system",
            probed, isGlobalDefault: true);
    }

    private static IEnumerable<string> WrapperDistributions(string distsRoot, string prefix)
    {
        foreach (var distribution in InventoryHelpers.SafeDirectories(distsRoot))
        {
            foreach (var hashDirectory in InventoryHelpers.SafeDirectories(distribution))
            {
                foreach (var install in InventoryHelpers.SafeDirectories(hashDirectory))
                {
                    if (Path.GetFileName(install).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        yield return install;
                }
            }
        }
    }

    /// <summary>"gradle-8.5-bin" → "8.5"; "apache-maven-3.9.6-bin" → "3.9.6".</summary>
    private static string? VersionFromDistribution(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = name[prefix.Length..];
        return InventoryHelpers.VersionTokenIn(rest);
    }
}

/// <summary>.NET SDKs. <c>dotnet --list-sdks</c> is authoritative when it is available.</summary>
public sealed partial class DotNetSdkProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.DotNetSdk;

    [GeneratedRegex(@"^(\d[\w.\-]+)\s+\[(.+)\]$", RegexOptions.Multiline)]
    private static partial Regex SdkListing();

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var executable = PathSolver.Which("dotnet");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? defaultSdk = null;

        if (executable is not null && context.AllowProbing)
        {
            var listing = context.Runner.Run(executable, ["--list-sdks"], context.ProbeTimeout,
                ProcessRunner.NeutralWorkingDirectory);

            var current = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
                ProcessRunner.NeutralWorkingDirectory);
            defaultSdk = InventoryHelpers.VersionTokenIn(current.Combined);

            foreach (Match match in SdkListing().Matches(listing.StdOut))
            {
                var version = match.Groups[1].Value.Trim();
                var root = match.Groups[2].Value.Trim();
                var installPath = Path.Combine(root, version);
                reported.Add(version);

                yield return InventoryHelpers.Create(ToolKind.DotNetSdk, version,
                    Directory.Exists(installPath) ? installPath : root, "system", version,
                    isGlobalDefault: defaultSdk is not null && version == defaultSdk);
            }
        }

        // Fallback (and a second opinion) from the filesystem.
        string[] roots = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\dotnet\sdk"]
            : OperatingSystem.IsMacOS()
                ? ["/usr/local/share/dotnet/sdk", "/opt/homebrew/share/dotnet/sdk"]
                : ["/usr/share/dotnet/sdk", "/usr/lib/dotnet/sdk", "/opt/dotnet/sdk"];

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                var name = Path.GetFileName(directory);
                var version = InventoryHelpers.VersionTokenIn(name);
                if (version is null || !reported.Add(version)) continue;

                yield return InventoryHelpers.Create(ToolKind.DotNetSdk, version, directory, "system", name,
                    isGlobalDefault: defaultSdk is not null && version == defaultSdk);
            }
        }
    }
}
