using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Scan;

namespace DevTools.Providers;

public sealed class ScanRequest
{
    public List<string> Roots { get; init; } = [];

    public int MaxDepth { get; init; } = 12;

    public bool IncludeInventory { get; init; } = true;

    public bool AllowProbing { get; init; } = true;

    public bool MeasureSizes { get; init; }

    public bool MeasureToolchains { get; init; }

    public List<string> Excludes { get; init; } = [];

    public bool FollowSymlinks { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// The one place a scan is composed: walk, extract, inventory, snapshot. Both the CLI and
/// the desktop app call this, so they can never disagree about what a scan produced.
/// </summary>
public sealed class ScanService
{
    private readonly Func<bool, IToolchainInventory> _inventoryFactory;

    public ScanService(Func<bool, IToolchainInventory>? inventoryFactory = null)
    {
        _inventoryFactory = inventoryFactory ?? (allowProbing => ToolchainInventory.CreateDefault(allowProbing));
    }

    public async Task<IndexSnapshot> ScanAsync(ScanRequest request, IndexSnapshot? previous = null)
    {
        if (previous is not null && previous.EnvironmentName != "Unknown" &&
            !string.Equals(previous.EnvironmentName, IndexSnapshot.CurrentEnvironmentName, StringComparison.Ordinal))
            throw new InvalidOperationException("This index belongs to " + previous.EnvironmentName +
                "; use a separate DEVTOOLS_HOME for " + IndexSnapshot.CurrentEnvironmentName + ".");

        var token = request.CancellationToken;
        var started = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var walkOptions = new ScanOptions
        {
            Roots = request.Roots.Select(PlatformPaths.Expand).ToList(),
            MaxDepth = request.MaxDepth,
            ExcludeDirectoryNames = request.Excludes,
            FollowSymlinks = request.FollowSymlinks,
            MeasureSizes = request.MeasureSizes
        };

        var walk = await Task.Run(() => new DirectoryWalker().Walk(walkOptions, token), token).ConfigureAwait(false);

        var scanner = new ProjectScanner();
        var projects = await Task.Run(
            () => scanner.Build(walk, walkOptions, previous?.Projects, token), token).ConfigureAwait(false);

        if (previous is not null && walk.UnavailablePaths.Count > 0)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var seen = projects.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var project in previous.Projects)
            {
                if (seen.Contains(project.Key)) continue;
                if (!walk.UnavailablePaths.Any(path => IsWithin(project.Path, path, comparison))) continue;
                project.Stale = true;
                projects.Add(project);
            }
        }

        IReadOnlyList<Toolchain> toolchains = [];
        if (request.IncludeInventory)
        {
            toolchains = await Task.Run(() =>
            {
                var inventory = _inventoryFactory(request.AllowProbing);
                if (inventory is ToolchainInventory concrete)
                {
                    concrete.Context = concrete.Context with { MeasureSizes = request.MeasureToolchains };
                }

                return inventory.Discover(token);
            }, token).ConfigureAwait(false);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var manual = ManualToolchainDiscovery.Discover(walk.ToolchainCandidates, previous?.Toolchains)
                .Where(candidate => !toolchains.Any(existing => existing.Tool == candidate.Tool && existing.Health == ToolchainHealth.Ok &&
                    string.Equals(Path.TrimEndingDirectorySeparator(existing.Path), Path.TrimEndingDirectorySeparator(candidate.Path), pathComparison)));
            toolchains = ToolchainInventory.Deduplicate(toolchains.Concat(manual).ToList());
            if (previous is not null && walk.UnavailablePaths.Count > 0)
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var current = toolchains.ToList();
                foreach (var prior in previous.Toolchains)
                {
                    if (!walk.UnavailablePaths.Any(path => IsWithin(prior.Path, path, comparison))) continue;
                    if (current.Any(t => t.Tool == prior.Tool && string.Equals(t.Path, prior.Path, comparison))) continue;
                    prior.Stale = true;
                    current.Add(prior);
                }
                toolchains = current;
            }
        }
        else if (previous is not null)
        {
            // "--no-inventory" means "do not spend time probing", not "forget what I know".
            toolchains = previous.Toolchains;
        }

        stopwatch.Stop();

        return new IndexSnapshot
        {
            ScannedAt = DateTimeOffset.Now,
            EnvironmentName = IndexSnapshot.CurrentEnvironmentName,
            Roots = walk.RootsScanned,
            ConfiguredRoots = walkOptions.Roots,
            Projects = projects.OrderBy(p => p.Path).ToList(),
            Toolchains = toolchains.ToList(),
            Diagnostics = walk.Diagnostics.Concat(projects.SelectMany(p => p.Diagnostics)).ToList(),
            Stats = new ScanStats
            {
                DirsVisited = walk.Stats.DirsVisited,
                DirsPruned = walk.Stats.DirsPruned,
                Errors = walk.Stats.Errors,
                PrunedByReason = walk.Stats.PrunedByReason,
                ProjectsFound = projects.Count(p => p.State == ProjectState.Active),
                ProjectsSuppressed = projects.Count(p => p.State == ProjectState.Suppressed),
                Mode = walk.Stats.Mode,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Partial = walk.Stats.Partial
            }
        };
    }

    private static bool IsWithin(string path, string root, StringComparison comparison)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(parent, comparison) ||
               candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>Per-project resolution plus the machine-wide rollups the dashboard needs.</summary>
public sealed class ProjectView
{
    public required ProjectRecord Project { get; init; }
    public required ProjectResolution Resolution { get; init; }

    public bool HasProblems => Resolution.HasProblems || Project.Incomplete || Project.HasCiDrift || Project.Stale;
}

public sealed class MissingToolchain
{
    public ToolKind Tool { get; init; }
    public string Constraint { get; init; } = "";
    public string SuggestedVersion { get; init; } = "";
    public bool RequiresVersionChoice { get; init; }
    public List<string> Projects { get; init; } = [];

    public string Key => $"{Tool}|{Constraint}";
}

public sealed class MachineStats
{
    public int Projects { get; set; }
    public int ActiveProjects { get; set; }
    public int ProjectsWithProblems { get; set; }
    public int SuppressedProjects { get; set; }
    public int ToolchainsInstalled { get; set; }
    public int UnverifiedToolchains { get; set; }
    public int UnusedToolchains { get; set; }
    public long? ReclaimableBytes { get; set; }
    public Dictionary<string, int> ProjectsByEcosystem { get; set; } = [];
    public Dictionary<string, int> ToolchainsByTool { get; set; } = [];
    public List<MissingToolchain> Missing { get; set; } = [];
}

/// <summary>A resolved view of the index: every project matched against the inventory.</summary>
public sealed class MachineView
{
    public required IndexSnapshot Snapshot { get; init; }
    public required List<ProjectView> Projects { get; init; }
    public required List<ConflictFinding> Conflicts { get; init; }
    public required MachineStats Stats { get; init; }

    public static MachineView Build(IndexSnapshot snapshot)
    {
        var engine = new ResolutionEngine(snapshot.Toolchains);
        var active = snapshot.Projects.Where(p => p.State == ProjectState.Active).ToList();

        var views = snapshot.Projects
            .Select(p => new ProjectView { Project = p, Resolution = engine.Resolve(p) })
            .ToList();

        var conflicts = ResolutionEngine.FindConflicts(active.Where(p => !p.Stale), snapshot.Toolchains).ToList();

        var missing = new Dictionary<string, MissingToolchain>(StringComparer.Ordinal);
        foreach (var view in views.Where(v => v.Project.State == ProjectState.Active && !v.Project.Stale))
        {
            foreach (var tool in view.Resolution.Tools.Where(t => t.Status is ResolutionStatus.Missing or ResolutionStatus.UnsatisfiedRange))
            {
                var key = $"{tool.Tool}|{tool.ConstraintDisplay}";
                if (!missing.TryGetValue(key, out var entry))
                {
                    entry = new MissingToolchain
                    {
                        Tool = tool.Tool,
                        Constraint = tool.ConstraintDisplay,
                        SuggestedVersion = SuggestVersion(tool),
                        RequiresVersionChoice = tool.Requirement?.Constraint.Kind is not
                            (DevTools.Core.Constraints.ConstraintKind.Exact or DevTools.Core.Constraints.ConstraintKind.Toolchain)
                    };
                    missing[key] = entry;
                }

                if (!entry.Projects.Contains(view.Project.DisplayName))
                    entry.Projects.Add(view.Project.DisplayName);
            }
        }

        var usedToolchainPaths = views
            .SelectMany(v => v.Resolution.Tools)
            .Where(t => t.Toolchain is not null)
            .Select(t => t.Toolchain!.Path)
            .ToHashSet(OperatingSystem.IsWindows() && !snapshot.EnvironmentName.StartsWith("WSL: ", StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var artifactTotal = snapshot.Projects.Sum(p => p.ArtifactBytes ?? 0);

        var stats = new MachineStats
        {
            Projects = snapshot.Projects.Count,
            ActiveProjects = active.Count,
            ProjectsWithProblems = views.Count(v => v.Project.State == ProjectState.Active && v.HasProblems),
            SuppressedProjects = snapshot.Projects.Count(p => p.State == ProjectState.Suppressed),
            ToolchainsInstalled = snapshot.Toolchains.Count(t => !t.Stale && t.Health == ToolchainHealth.Ok),
            UnverifiedToolchains = snapshot.Toolchains.Count(t => !t.Stale && t.Health == ToolchainHealth.Unverified),
            UnusedToolchains = snapshot.Toolchains.Count(t => !t.IsGlobalDefault && !usedToolchainPaths.Contains(t.Path)),
            ReclaimableBytes = artifactTotal > 0 ? artifactTotal : null,
            ProjectsByEcosystem = active
                .SelectMany(p => p.Ecosystems)
                .GroupBy(e => ToolKinds.DisplayEcosystem(e))
                .ToDictionary(g => g.Key, g => g.Count()),
            ToolchainsByTool = snapshot.Toolchains
                .GroupBy(t => ToolKinds.Display(t.Tool))
                .ToDictionary(g => g.Key, g => g.Count()),
            Missing = missing.Values.OrderByDescending(m => m.Projects.Count).ToList()
        };

        return new MachineView
        {
            Snapshot = snapshot,
            Projects = views.OrderByDescending(v => v.Project.LastActivityAt ?? DateTimeOffset.MinValue).ToList(),
            Conflicts = conflicts,
            Stats = stats
        };
    }

    private static string SuggestVersion(ToolResolution resolution)
    {
        var constraint = resolution.Requirement?.Constraint;
        if (constraint is null) return "";

        return constraint.Kind switch
        {
            DevTools.Core.Constraints.ConstraintKind.Exact => constraint.Exact?.ToString() ?? constraint.Raw,
            DevTools.Core.Constraints.ConstraintKind.Range => constraint.Min?.ToString() ?? "latest",
            DevTools.Core.Constraints.ConstraintKind.Lts =>
                resolution.Tool == ToolKind.Node
                    ? "lts (node " + DevTools.Core.Constraints.LtsRules.NewestKnownNodeLts + ")"
                    : "lts",
            DevTools.Core.Constraints.ConstraintKind.Toolchain => constraint.ToolchainId ?? constraint.Raw,
            _ => "latest"
        };
    }
}
