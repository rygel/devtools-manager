using System.Text.RegularExpressions;

namespace DevTools.Providers;

/// <summary>Which package manager installed an executable, and the package name that manager uses.</summary>
public sealed record PackageOwner(string Manager, string Package);

/// <summary>
/// Works out who owns an executable found on PATH. Homebrew and Scoop are read from the real path
/// (Cellar and apps folders), Chocolatey from its lib folder, and apt, rpm and pacman from their
/// package databases. Anything not recognised returns null, and the planner keeps the tool's own command.
/// </summary>
public static partial class PackageOwnership
{
    [GeneratedRegex("""^\s*path\s*=\s*"?(?<path>[^"\r\n]+?)"?\s*$""", RegexOptions.Multiline)]
    private static partial Regex ShimPath();

    /// <summary>Resolves symlinks and shims, then asks the owning manager. Probes Linux databases only when needed.</summary>
    public static PackageOwner? Detect(string executable, IProcessRunner? runner = null)
    {
        var real = Resolve(executable);
        var fromPath = FromPath(real, File.Exists);
        if (fromPath is not null) return fromPath;

        if (OperatingSystem.IsWindows()) return null;
        return FromDatabases(real, runner ?? ProcessRunner.Shared);
    }

    /// <summary>Reads Cellar and Scoop/Chocolatey folders. Pure: the file check is injected for tests.</summary>
    public static PackageOwner? FromPath(string path, Func<string, bool> fileExists)
    {
        var normalised = path.Replace('\\', '/');

        var cellar = Regex.Match(normalised, @"/Cellar/(?<formula>[^/]+)/");
        if (cellar.Success) return new PackageOwner("brew", cellar.Groups["formula"].Value);

        var scoopApp = Regex.Match(normalised, @"/scoop/apps/(?<app>[^/]+)/", RegexOptions.IgnoreCase);
        if (scoopApp.Success) return new PackageOwner("scoop", scoopApp.Groups["app"].Value);

        var choco = Regex.Match(normalised, @"/chocolatey/lib/(?<pkg>[^/]+)/", RegexOptions.IgnoreCase);
        if (choco.Success) return new PackageOwner("choco", choco.Groups["pkg"].Value);

        // A Scoop shim is a launcher; the .shim file beside it names the real executable.
        var shim = Regex.Match(normalised, @"/scoop/shims/(?<name>[^/]+?)(\.exe)?$", RegexOptions.IgnoreCase);
        if (shim.Success)
        {
            var shimFile = Path.Combine(Path.GetDirectoryName(path)!, shim.Groups["name"].Value + ".shim");
            if (fileExists(shimFile) && TryReadShimTarget(shimFile) is { } target && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                return FromPath(target, fileExists);
        }

        return null;
    }

    /// <summary>The target a Scoop shim launches, read from its <c>path = "..."</c> line.</summary>
    public static string? ParseShimTarget(string contents)
    {
        var match = ShimPath().Match(contents);
        return match.Success ? match.Groups["path"].Value.Trim() : null;
    }

    private static string? TryReadShimTarget(string shimFile)
    {
        try { return ParseShimTarget(File.ReadAllText(shimFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>dpkg -S prints "package:amd64: /usr/bin/node". The package is the part before the first colon.</summary>
    public static string? ParseDpkg(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith("dpkg-query:", StringComparison.Ordinal)) return null;
        var name = line.Split(':')[0].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>rpm -qf prints the package name and version, for example "nodejs-22.1.0-1.fc41.x86_64".</summary>
    public static string? ParseRpm(string output)
    {
        var line = output.Trim();
        if (line.Length == 0 || line.Contains("not owned", StringComparison.OrdinalIgnoreCase)) return null;
        return line.Split('\n')[0].Trim();
    }

    /// <summary>pacman -Qo prints "/usr/bin/node is owned by nodejs 22.1.0-1".</summary>
    public static string? ParsePacman(string output)
    {
        var match = Regex.Match(output, @"is owned by (?<pkg>\S+)");
        return match.Success ? match.Groups["pkg"].Value : null;
    }

    private static PackageOwner? FromDatabases(string real, IProcessRunner runner)
    {
        var dpkg = PathSolver.Which("dpkg");
        if (dpkg is not null)
        {
            var result = runner.Run(dpkg, ["-S", real], TimeSpan.FromSeconds(5), ProcessRunner.NeutralWorkingDirectory);
            if (result.Succeeded && ParseDpkg(result.StdOut) is { } package) return new PackageOwner("apt", package);
        }

        var rpm = PathSolver.Which("rpm");
        if (rpm is not null)
        {
            var result = runner.Run(rpm, ["-qf", real], TimeSpan.FromSeconds(5), ProcessRunner.NeutralWorkingDirectory);
            if (result.Succeeded && ParseRpm(result.StdOut) is { } package) return new PackageOwner("dnf", package);
        }

        var pacman = PathSolver.Which("pacman");
        if (pacman is not null)
        {
            var result = runner.Run(pacman, ["-Qo", real], TimeSpan.FromSeconds(5), ProcessRunner.NeutralWorkingDirectory);
            if (result.Succeeded && ParsePacman(result.StdOut) is { } package) return new PackageOwner("pacman", package);
        }

        return null;
    }

    /// <summary>Follows symlinks (Homebrew's bin entries point into Cellar) to the file the manager installed.</summary>
    private static string Resolve(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }
}
