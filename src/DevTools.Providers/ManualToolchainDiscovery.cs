using DevTools.Core.Model;

namespace DevTools.Providers;

/// <summary>Finds named unpacked distributions without executing files found during a scan.</summary>
public static class ManualToolchainDiscovery
{
    private static readonly ToolKind[] SupportedTools = [ToolKind.Maven, ToolKind.Gradle, ToolKind.Jdk, ToolKind.Node];
    public static IReadOnlyList<Toolchain> Discover(IEnumerable<string> paths, IEnumerable<Toolchain>? previous = null)
    {
        var results = new List<Toolchain>();
        var old = previous?.ToList() ?? [];
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in paths.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            ToolKind? tool = name.StartsWith("apache-maven-", StringComparison.OrdinalIgnoreCase) ? ToolKind.Maven :
                name.StartsWith("gradle-", StringComparison.OrdinalIgnoreCase) ? ToolKind.Gradle :
                name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase) ? ToolKind.Jdk :
                name.StartsWith("node-v", StringComparison.OrdinalIgnoreCase) ? ToolKind.Node : null;
            foreach (var kind in SupportedTools)
            {
                var version = tool == kind ? InventoryHelpers.VersionFromName(name) ?? "unknown" : "unknown";
                var executable = FindExecutable(kind, path);
                if (executable is null) continue;
                var record = new Toolchain
                {
                    Tool = kind, Version = version, Path = path, Manager = "manual",
                    Health = ToolchainHealth.Unverified,
                    HealthDetail = version == "unknown" ? "Version unknown; executable has not been run." :
                        "Version inferred from the directory name; executable has not been run."
                };
                var prior = old.FirstOrDefault(t => t.Tool == kind && t.Manager == "manual" && t.Health == ToolchainHealth.Ok &&
                    string.Equals(t.Path, path, comparison));
                if (prior?.VerifiedExecutableSize is not null && prior.VerifiedExecutableModifiedUtc is not null)
                {
                    try
                    {
                        var info = new FileInfo(executable);
                        if (info.Length == prior.VerifiedExecutableSize && info.LastWriteTimeUtc == prior.VerifiedExecutableModifiedUtc.Value.UtcDateTime)
                        {
                            record = prior;
                            record.Stale = false;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                results.Add(record);
            }
        }
        return results;
    }

    public static string? FindExecutable(ToolKind tool, string root)
    {
        string[] names = tool switch
        {
            ToolKind.Maven => OperatingSystem.IsWindows() ? ["bin/mvn.cmd"] : ["bin/mvn"],
            ToolKind.Gradle => OperatingSystem.IsWindows() ? ["bin/gradle.bat"] : ["bin/gradle"],
            ToolKind.Jdk => OperatingSystem.IsWindows() ? ["bin/java.exe"] : ["bin/java"],
            ToolKind.Node => OperatingSystem.IsWindows() ? ["node.exe", "bin/node.exe"] : ["bin/node", "node"],
            _ => []
        };
        return names.Select(name => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar))).FirstOrDefault(File.Exists);
    }

    public static (string? Version, string? Error) Verify(Toolchain toolchain, IProcessRunner? runner = null)
    {
        var executable = FindExecutable(toolchain.Tool, toolchain.Path);
        if (executable is null) return (null, "The expected executable is missing.");
        var argument = toolchain.Tool is ToolKind.Jdk or ToolKind.Maven ? "-version" : "--version";
        var result = VersionProbe.Run(executable, argument, null, runner);
        if (!result.Succeeded) return (null, result.Error ?? result.Combined.Trim());
        var version = InventoryHelpers.VersionTokenIn(result.Combined);
        return version is null ? (null, "The executable did not report a recognizable version.") : (version, null);
    }
}
