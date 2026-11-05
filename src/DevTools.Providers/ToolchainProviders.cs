using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Providers;

public sealed record ProviderContext
{
    /// <summary>Running <c>--version</c> is the only way to learn some versions. Never required.</summary>
    public bool AllowProbing { get; init; } = true;

    public bool MeasureSizes { get; init; }

    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public IProcessRunner Runner { get; init; } = ProcessRunner.Shared;
}

public interface IToolchainProvider
{
    ToolKind Tool { get; }

    IEnumerable<Toolchain> Discover(ProviderContext context);
}

internal static partial class InventoryHelpers
{
    /// <summary>A version with at least one dot: "16.0.1", "8.5".</summary>
    [GeneratedRegex(@"(?<![\d.])(\d+(?:\.\d+){1,3})(?![\d])")]
    private static partial Regex DottedVersion();

    /// <summary>A bare integer, only used when nothing dotted exists.</summary>
    [GeneratedRegex(@"(?<!\d)(\d+)(?!\d)")]
    private static partial Regex IntegerVersion();

    /// <summary>An integer that follows a dash: "jdk-21" → 21.</summary>
    [GeneratedRegex(@"-(?<v>\d+)(?![\d])")]
    private static partial Regex DashedInteger();

    /// <summary>
    /// Extracts the first plausible version from tool output. Dotted versions win over
    /// bare integers, because output like "adopt-openj9-16.0.1" contains a misleading 9.
    /// </summary>
    public static string? VersionTokenIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var dotted = DottedVersion().Match(text);
        if (dotted.Success) return dotted.Groups[1].Value;

        var integer = IntegerVersion().Match(text);
        return integer.Success ? integer.Groups[1].Value : null;
    }

    /// <summary>
    /// Normalises an install directory name to a version. Names are hostile: "v20.11.0",
    /// "graalvm-ce-25.0.2", "adopt-openj9-16.0.1", "stable-x86_64-pc-windows-msvc".
    /// Returns null when the name carries no version at all — the caller then probes.
    /// </summary>
    public static string? VersionFromName(string name)
    {
        var trimmed = name.TrimStart('v', 'V');
        if (trimmed.Length == 0) return null;

        if (char.IsAsciiDigit(trimmed[0]))
        {
            // "1.95.0-x86_64-pc-windows-msvc" is a toolchain *and* a target triple:
            // take only the leading dotted version.
            var leading = DottedVersion().Match(trimmed);
            if (leading.Success && leading.Index == 0) return leading.Groups[1].Value;

            // A bare major ("20", "21").
            if (VersionNumber.TryParse(trimmed, out var direct) && direct is not null)
                return VersionNumber.FromParts(direct.Parts.ToArray()).ToString();
        }

        var dotted = DottedVersion().Match(trimmed);
        if (dotted.Success) return dotted.Groups[1].Value;

        var dashed = DashedInteger().Match(trimmed);
        return dashed.Success ? dashed.Groups["v"].Value : null;
    }

    /// <summary>True when at least one of the relative binary candidates exists.</summary>
    public static bool HasBinary(string directory, params string[] relativeCandidates)
    {
        foreach (var relative in relativeCandidates)
        {
            try
            {
                if (File.Exists(Path.Combine(directory, relative))) return true;
            }
            catch
            {
                // ignore malformed path
            }
        }

        return false;
    }

    public static IEnumerable<string> SafeDirectories(string? root)
    {
        if (root is null) return [];

        try
        {
            return Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];
        }
        catch
        {
            return [];
        }
    }

    public static long? Measure(string directory, bool enabled)
    {
        if (!enabled) return null;

        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* skip */ }
            }

            return total;
        }
        catch
        {
            return null;
        }
    }

    public static Toolchain Create(
        ToolKind tool,
        string version,
        string path,
        string manager,
        string? rawVersion = null,
        bool isGlobalDefault = false,
        bool isProxy = false,
        ToolchainHealth health = ToolchainHealth.Ok,
        string? healthDetail = null) =>
        new()
        {
            Tool = tool,
            Version = version,
            RawVersion = rawVersion,
            Path = path,
            Manager = manager,
            IsGlobalDefault = isGlobalDefault,
            IsProxy = isProxy,
            Health = health,
            HealthDetail = healthDetail,
            VerifiedAt = DateTimeOffset.Now
        };

    public static IEnumerable<string> CandidateRoots(params string[] candidates) =>
        candidates.Select(PlatformPaths.Expand).Where(Directory.Exists);

    public static string[] WindowsBinaryNames(string relative) =>
        [$"{relative}.exe", relative];
}

/// <summary>
/// Node installations held by version managers. One pass over each manager's known root;
/// no manager needs to be on PATH for its installs to be visible.
/// </summary>
public sealed class NodeVersionManagerProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Node;

    private sealed record Layout(string Manager, string[] Roots, string[] BinaryCandidates, string? DirectoryFilter);

    private static IEnumerable<Layout> Layouts()
    {
        var home = PlatformPaths.Home;

        var nvmRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm")
        };
        if (Environment.GetEnvironmentVariable("NVM_HOME") is { Length: > 0 } nvmHome) nvmRoots.Add(nvmHome);

        yield return new Layout("nvm-windows", nvmRoots.ToArray(), ["node.exe"], null);

        yield return new Layout("nvm",
            [Path.Combine(home, ".nvm", "versions", "node")],
            ["bin/node"],
            null);

        yield return new Layout("fnm",
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fnm", "node-versions"),
                Path.Combine(home, ".local", "share", "fnm", "node-versions"),
                Path.Combine(home, ".fnm", "node-versions")
            ],
            ["installation/bin/node", "installation/node.exe"],
            null);

        yield return new Layout("volta",
            [Path.Combine(home, ".volta", "tools", "image", "node")],
            ["bin/node", "node.exe"],
            null);

        yield return new Layout("asdf",
            [Path.Combine(home, ".asdf", "installs", "nodejs")],
            ["bin/node", "node.exe"],
            null);

        yield return new Layout("mise",
            [
                Path.Combine(home, ".local", "share", "mise", "installs", "node"),
                Path.Combine(home, ".local", "share", "mise", "installs", "nodejs")
            ],
            ["bin/node", "node.exe"],
            null);
    }

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        foreach (var layout in Layouts())
        {
            foreach (var root in InventoryHelpers.CandidateRoots(layout.Roots))
            {
                foreach (var directory in InventoryHelpers.SafeDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (string.Equals(name, "current", StringComparison.OrdinalIgnoreCase)) continue;

                    var version = InventoryHelpers.VersionFromName(name);
                    if (version is null) continue;

                    var hasBinary = InventoryHelpers.HasBinary(directory, layout.BinaryCandidates);

                    yield return InventoryHelpers.Create(
                        ToolKind.Node, version, directory, layout.Manager, name,
                        health: hasBinary ? ToolchainHealth.Ok : ToolchainHealth.MissingBinary,
                        healthDetail: hasBinary ? null : "node binary not found in install directory");
                }
            }
        }
    }
}

/// <summary>Deno installs from dvm, asdf or mise, plus the binary on PATH.</summary>
public sealed class DenoProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Deno;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        foreach (var (root, manager) in new (string, string)[]
                 {
                     (Path.Combine(home, ".dvm", "versions"), "dvm"),
                     (Path.Combine(home, ".asdf", "installs", "deno"), "asdf"),
                     (Path.Combine(home, ".local", "share", "mise", "installs", "deno"), "mise")
                 })
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                var version = InventoryHelpers.VersionFromName(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Deno, version, directory, manager,
                    Path.GetFileName(directory));
            }
        }

        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which("deno");
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);

        var probed = InventoryHelpers.VersionTokenIn(result.Combined);
        if (probed is null) yield break;

        yield return InventoryHelpers.Create(ToolKind.Deno, probed,
            Path.GetDirectoryName(executable) ?? executable, "system", probed, isGlobalDefault: true);
    }
}

/// <summary>
/// Elixir's <c>--version</c> prints the OTP and ERTS versions before the language version,
/// so it needs a targeted pattern rather than "the first version-looking token".
/// </summary>
public sealed class ElixirProvider : IToolchainProvider
{
    private static readonly System.Text.RegularExpressions.Regex VersionPattern =
        new(@"Elixir\s+(\d[\d.]*)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ToolKind Tool => ToolKind.Elixir;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var home = PlatformPaths.Home;

        foreach (var (root, manager) in new (string, string)[]
                 {
                     (Path.Combine(home, ".asdf", "installs", "elixir"), "asdf"),
                     (Path.Combine(home, ".local", "share", "mise", "installs", "elixir"), "mise")
                 })
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(root))
            {
                var version = InventoryHelpers.VersionFromName(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(ToolKind.Elixir, version, directory, manager,
                    Path.GetFileName(directory));
            }
        }

        if (!context.AllowProbing) yield break;

        var executable = PathSolver.Which("elixir");
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);

        var match = VersionPattern.Match(result.Combined);
        if (!match.Success) yield break;

        yield return InventoryHelpers.Create(ToolKind.Elixir, match.Groups[1].Value,
            Path.GetDirectoryName(executable) ?? executable, "system", match.Groups[1].Value,
            isGlobalDefault: true);
    }
}

/// <summary>Toolchains installed as plain version directories by a manager.</summary>
public sealed class VersionDirectoryProvider : IToolchainProvider
{
    private readonly IReadOnlyList<(string Root, string Manager)> _roots;

    public VersionDirectoryProvider(ToolKind tool, params (string Root, string Manager)[] roots)
    {
        Tool = tool;
        _roots = roots;
    }

    public ToolKind Tool { get; }

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        foreach (var (root, manager) in _roots)
        {
            foreach (var directory in InventoryHelpers.SafeDirectories(PlatformPaths.Expand(root)))
            {
                var version = InventoryHelpers.VersionFromName(Path.GetFileName(directory));
                if (version is null) continue;

                yield return InventoryHelpers.Create(Tool, version, directory, manager,
                    Path.GetFileName(directory));
            }
        }
    }
}

/// <summary>System Node: whatever <c>node</c> resolves to on PATH, plus common install roots.</summary>
public sealed class NodeRuntimeProvider : IToolchainProvider
{
    public ToolKind Tool => ToolKind.Node;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        var node = PathSolver.Which("node");
        if (node is not null)
        {
            var version = context.AllowProbing
                ? InventoryHelpers.VersionTokenIn(context.Runner.Run(node, ["--version"], context.ProbeTimeout,
                    ProcessRunner.NeutralWorkingDirectory).Combined)
                : null;

            var directory = Path.GetDirectoryName(node) ?? node;

            if (version is not null)
            {
                // nvm-windows points C:\Program Files\nodejs at whichever version is active.
                var isProxy = OperatingSystem.IsWindows() &&
                              node.Contains("Program Files", StringComparison.OrdinalIgnoreCase);

                yield return InventoryHelpers.Create(
                    ToolKind.Node, version, directory, "system", version,
                    isGlobalDefault: true, isProxy: isProxy);
            }
        }

        string[] roots =
        [
            @"C:\Program Files\nodejs",
            "/usr/local/bin",
            "/opt/homebrew/bin",
            "/usr/local/opt/node"
        ];

        foreach (var root in roots.Where(Directory.Exists))
        {
            // A system install is only meaningful if it exposes a version we can read.
            var packageJson = Path.Combine(root, "package.json");
            if (!File.Exists(packageJson)) continue;

            var version = InventoryHelpers.VersionTokenIn(File.ReadAllText(packageJson));
            if (version is null) continue;

            yield return InventoryHelpers.Create(ToolKind.Node, version, root, "system", version);
        }
    }
}

/// <summary>
/// JavaScript package managers. These change often and have no stable install layout,
/// so the only reliable source is the executable itself answering <c>--version</c>.
/// </summary>
public sealed class JsPackageManagerProvider(ToolKind tool) : IToolchainProvider
{
    public ToolKind Tool => tool;

    public IEnumerable<Toolchain> Discover(ProviderContext context)
    {
        if (!context.AllowProbing) yield break;

        var name = ToolKinds.Display(tool);
        var executable = PathSolver.Which(name);
        if (executable is null) yield break;

        var result = context.Runner.Run(executable, ["--version"], context.ProbeTimeout,
            ProcessRunner.NeutralWorkingDirectory);

        var version = InventoryHelpers.VersionTokenIn(result.Combined);
        if (version is null) yield break;

        var manager = executable.Contains("corepack", StringComparison.OrdinalIgnoreCase) ? "corepack" : "system";

        yield return InventoryHelpers.Create(tool, version, Path.GetDirectoryName(executable) ?? executable,
            manager, version, isGlobalDefault: true);
    }
}
