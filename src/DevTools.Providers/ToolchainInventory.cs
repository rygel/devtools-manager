using DevTools.Core.Model;

namespace DevTools.Providers;

/// <summary>
/// Aggregates every provider into one de-duplicated inventory. The same JDK reachable via
/// <c>JAVA_HOME</c>, SDKMAN's <c>current</c> symlink and PATH is one toolchain with three
/// pointers, not three toolchains.
/// </summary>
public sealed class ToolchainInventory : IToolchainInventory
{
    private readonly IReadOnlyList<IToolchainProvider> _providers;

    public ToolchainInventory(IEnumerable<IToolchainProvider> providers)
    {
        _providers = providers.ToList();
    }

    public ProviderContext Context { get; set; } = new();

    /// <summary>The tools this inventory can find on a machine.</summary>
    public IReadOnlyCollection<ToolKind> Tools => _providers.Select(provider => provider.Tool).Distinct().ToList();

    public static ToolchainInventory CreateDefault(bool allowProbing = true) =>
        new([
            new NodeVersionManagerProvider(),
            new NodeRuntimeProvider(),
            new DenoProvider(),
            new JdkProvider(),
            new BuildToolProvider(ToolKind.Maven),
            new BuildToolProvider(ToolKind.Gradle),
            new DotNetSdkProvider(),
            new PythonProvider(),
            new GoProvider(),
            new RustProvider(),
            new RubyProvider(),
            new UvProvider(),
            new PathProbeProvider(ToolKind.Php, "php"),
            new PathProbeProvider(ToolKind.Dart, "dart"),
            new ElixirProvider(),
            new PathProbeProvider(ToolKind.Scala, "scala", "-version"),
            new PathProbeProvider(ToolKind.Kotlin, "kotlinc", "-version"),
            new PathProbeProvider(ToolKind.Zig, "zig", "version"),
            new PathProbeProvider(ToolKind.Terraform, "terraform", "version"),
            new PathProbeProvider(ToolKind.Julia, "julia", "--version"),
            new PathProbeProvider(ToolKind.Cmake, "cmake", "--version"),
            new PathProbeProvider(ToolKind.Meson, "meson", "--version"),
            new PathProbeProvider(ToolKind.Conan, "conan", "--version"),
            new PathProbeProvider(ToolKind.Fpc, "fpc", "-iV"),
            new PathProbeProvider(ToolKind.Lazarus, "lazbuild", "--version"),
            new PathProbeProvider(ToolKind.Swift, "swift", "--version"),
            new PathProbeProvider(ToolKind.Crystal, "crystal", "--version"),
            new PathProbeProvider(ToolKind.Gleam, "gleam", "--version"),
            new PathProbeProvider(ToolKind.Nim, "nim", "--version"),
            new PathProbeProvider(ToolKind.R, "R", "--version"),
            new PathProbeProvider(ToolKind.Ghc, "ghc", "--numeric-version"),
            new PathProbeProvider(ToolKind.Cabal, "cabal", "--numeric-version"),
            new PathProbeProvider(ToolKind.Stack, "stack", "--numeric-version"),
            new PathProbeProvider(ToolKind.Ocaml, "ocaml", "-version"),
            new PathProbeProvider(ToolKind.Dmd, "dmd", "--version"),
            new PathProbeProvider(ToolKind.Perl, "perl", "-v"),
            new PathProbeProvider(ToolKind.Lua, "lua", "-v"),
            new VersionDirectoryProvider(ToolKind.Kotlin,
                ("~/.sdkman/candidates/kotlin", "sdkman")),
            new VersionDirectoryProvider(ToolKind.Scala,
                ("~/.asdf/installs/scala", "asdf"),
                ("~/.local/share/mise/installs/scala", "mise")),
            new VersionDirectoryProvider(ToolKind.Zig,
                ("~/.zvm", "zvm"),
                ("~/.asdf/installs/zig", "asdf"),
                ("~/.local/share/mise/installs/zig", "mise")),
            new VersionDirectoryProvider(ToolKind.Terraform,
                ("~/.tfenv/versions", "tfenv"),
                ("~/.asdf/installs/terraform", "asdf"),
                ("~/.local/share/mise/installs/terraform", "mise")),
            new VersionDirectoryProvider(ToolKind.Julia,
                ("~/.asdf/installs/julia", "asdf"),
                ("~/.local/share/mise/installs/julia", "mise")),
            new JsPackageManagerProvider(ToolKind.Pnpm),
            new JsPackageManagerProvider(ToolKind.Yarn),
            new JsPackageManagerProvider(ToolKind.Bun),
            new JsPackageManagerProvider(ToolKind.Npm)
        ])
        {
            Context = new ProviderContext { AllowProbing = allowProbing }
        };

    public IReadOnlyList<Toolchain> Discover(CancellationToken cancellationToken = default)
    {
        var all = new List<Toolchain>();

        foreach (var provider in _providers)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                all.AddRange(provider.Discover(Context));
            }
            catch (Exception)
            {
                // A single broken provider must not empty the inventory.
            }
        }

        return Deduplicate(all);
    }

    internal static List<Toolchain> Deduplicate(List<Toolchain> candidates)
    {
        var result = new List<Toolchain>();
        var index = new Dictionary<string, int>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var toolchain in candidates)
        {
            var canonical = CanonicalPath(toolchain.Path);
            var key = $"{toolchain.Tool}|{toolchain.Version}|{canonical}";

            if (!index.TryGetValue(key, out var position))
            {
                index[key] = result.Count;
                result.Add(toolchain);
                continue;
            }

            // Prefer the healthiest, most authoritative record for the same install.
            var existing = result[position];
            var better = (toolchain.Health == ToolchainHealth.Ok && existing.Health != ToolchainHealth.Ok) ||
                         (toolchain.IsGlobalDefault && !existing.IsGlobalDefault) ||
                         (existing.DiskBytes is null && toolchain.DiskBytes is not null);

            if (!better) continue;

            toolchain.IsGlobalDefault |= existing.IsGlobalDefault;
            result[position] = toolchain;
        }

        return result
            .OrderBy(t => t.Tool)
            .ThenByDescending(t => t.Parsed)
            .ToList();
    }

    private static string CanonicalPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, '/');
        }
        catch
        {
            return path;
        }
    }
}
