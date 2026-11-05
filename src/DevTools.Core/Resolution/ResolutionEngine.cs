using DevTools.Core.Constraints;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Core.Resolution;

public enum ResolutionStatus
{
    /// <summary>An installed toolchain satisfies the requirement.</summary>
    Satisfied,
    /// <summary>Nothing installed for this tool at all.</summary>
    Missing,
    /// <summary>The tool is installed, but no installed version is inside the required range.</summary>
    UnsatisfiedRange,
    /// <summary>The requirement describes a container/remote toolchain we cannot manage.</summary>
    NotManaged,
    /// <summary>The declaration could not be interpreted. Always carries a reason.</summary>
    Unknown,
    /// <summary>The user muted this requirement.</summary>
    Ignored,
    /// <summary>Satisfied, but another project demands an incompatible exact version.</summary>
    Conflict
}

public sealed class ToolResolution
{
    public ToolKind Tool { get; set; }

    /// <summary>The winning requirement, after precedence. Null when the tool is only known globally.</summary>
    public Requirement? Requirement { get; set; }

    public ResolutionStatus Status { get; set; }

    public string ConstraintDisplay { get; set; } = "";

    public Toolchain? Toolchain { get; set; }

    /// <summary>Closest installed version when nothing satisfies — what to suggest instead.</summary>
    public Toolchain? Nearest { get; set; }

    public string? Detail { get; set; }

    public List<Requirement> Superseded { get; set; } = [];

    public List<Diagnostic> Diagnostics { get; set; } = [];

    public bool IsProblem => Status is ResolutionStatus.Missing
        or ResolutionStatus.UnsatisfiedRange
        or ResolutionStatus.Conflict
        or ResolutionStatus.Unknown;
}

public sealed class ProjectResolution
{
    public string ProjectKey { get; set; } = "";

    public List<ToolResolution> Tools { get; set; } = [];

    public List<Diagnostic> Diagnostics { get; set; } = [];

    public int ProblemCount => Tools.Count(t => t.IsProblem);

    public bool HasProblems => ProblemCount > 0;
}

/// <summary>
/// Two groups of projects demanding incompatible exact versions of the same tool.
/// Reported per version pair, not per project pair — 34 projects pinning JDK 8 against
/// 20 pinning JDK 17 is one finding, not 680.
/// </summary>
public sealed class ConflictFinding
{
    public ToolKind Tool { get; set; }
    public string VersionA { get; set; } = "";
    public string VersionB { get; set; } = "";
    public List<string> ProjectsA { get; set; } = [];
    public List<string> ProjectsB { get; set; } = [];
    public bool ManagerCanHoldBoth { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectA => ProjectsA.FirstOrDefault() ?? "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectB => ProjectsB.FirstOrDefault() ?? "";

    public string Describe()
    {
        var a = ProjectsA.Count == 1 ? ProjectsA[0] : $"{ProjectsA.Count} projects";
        var b = ProjectsB.Count == 1 ? ProjectsB[0] : $"{ProjectsB.Count} projects";

        return $"{ToolKinds.Display(Tool)} {VersionA} ({a}) vs {VersionB} ({b})" +
               (ManagerCanHoldBoth
                   ? " — both can be installed side by side"
                   : " — needs a version manager");
    }
}

/// <summary>
/// Matches project requirements against installed toolchains. Pure: takes the inventory
/// and a project, returns the verdict. No filesystem, no process, no clock.
/// </summary>
public sealed class ResolutionEngine
{
    private readonly Dictionary<ToolKind, List<Toolchain>> _byTool;

    public ResolutionEngine(IEnumerable<Toolchain> inventory)
    {
        _byTool = inventory
            .Where(t => !t.Stale && t.Health == ToolchainHealth.Ok && t.Parsed is not null)
            .GroupBy(t => t.Tool)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.Parsed!).ToList());
    }

    public IReadOnlyList<Toolchain> Installed(ToolKind tool) =>
        _byTool.TryGetValue(tool, out var list) ? list : [];

    public ProjectResolution Resolve(ProjectRecord project)
    {
        var resolution = new ProjectResolution { ProjectKey = project.Key };

        foreach (var group in project.Requirements.GroupBy(r => r.Tool))
        {
            var resolutionForTool = ResolveTool(group.Key, group.ToList());
            resolution.Tools.Add(resolutionForTool);

            foreach (var diagnostic in resolutionForTool.Diagnostics)
            {
                diagnostic.ProjectKey ??= project.Key;
                resolution.Diagnostics.Add(diagnostic);
            }
        }

        return resolution;
    }

    public ToolResolution ResolveTool(ToolKind tool, IReadOnlyList<Requirement> requirements)
    {
        var ordered = requirements
            .OrderBy(r => r.Precedence)
            .ThenByDescending(r => r.Strength)
            .ToList();

        // Precedence: a pin beats a declaration, which beats an advisory engines field.
        var effective = ordered.FirstOrDefault(r => r.Strength <= RequirementStrength.Declared)
                        ?? ordered[0];

        var result = new ToolResolution
        {
            Tool = tool,
            Requirement = effective,
            ConstraintDisplay = effective.Constraint.Display(),
            Superseded = ordered.Where(r => !ReferenceEquals(r, effective)).ToList()
        };

        // A pin contradicts a declaration: report it, never silently pick one.
        foreach (var other in result.Superseded)
        {
            if (Conflicts(effective, other))
            {
                result.Diagnostics.Add(Diagnostic.Warn(
                    "conflicting-declaration",
                    $"{ToolKinds.Display(tool)}: {effective.Display} from {effective.Provenance.Describe()} " +
                    $"contradicts {other.Display} from {other.Provenance.Describe()}",
                    file: other.Provenance.FilePath,
                    line: other.Provenance.Line));
            }
        }

        if (effective.Constraint.Kind == ConstraintKind.Unknown)
        {
            result.Status = ResolutionStatus.Unknown;
            result.Detail = effective.Constraint.Reason ?? "unparseable version";
            return result;
        }

        var installed = Installed(tool);
        if (installed.Count == 0)
        {
            result.Status = ResolutionStatus.Missing;
            result.Detail = $"no {ToolKinds.Display(tool)} installation found";
            return result;
        }

        // "latest" is resolved against the disk, not the network.
        if (effective.Constraint.Kind == ConstraintKind.Latest)
        {
            result.Status = ResolutionStatus.Satisfied;
            result.Toolchain = installed[0];
            return result;
        }

        if (effective.Constraint.Kind == ConstraintKind.Toolchain &&
            effective.Constraint.ToolchainId is { } candidateId)
        {
            var byId = installed.FirstOrDefault(t =>
                string.Equals(t.Version, candidateId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.RawVersion, candidateId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                result.Status = ResolutionStatus.Satisfied;
                result.Toolchain = byId;
                return result;
            }
        }

        var match = installed.FirstOrDefault(t => Satisfies(effective, t.Parsed!, tool));
        if (match is not null)
        {
            result.Status = ResolutionStatus.Satisfied;
            result.Toolchain = match;
            result.Detail = tool == ToolKind.DotNetSdk
                ? DotNetRollForward.Explain(effective.Constraint, match.Parsed!, effective.RollForward)
                : null;
            return result;
        }

        var nearest = Nearest(installed, effective, tool);
        result.Nearest = nearest;

        if (effective.Constraint.Kind == ConstraintKind.Exact)
        {
            result.Status = ResolutionStatus.Missing;
            result.Detail = nearest is null
                ? "required version is not installed"
                : $"required {effective.Constraint.Display}, installed versions: " +
                  string.Join(", ", installed.Take(4).Select(t => t.Version));
        }
        else
        {
            result.Status = ResolutionStatus.UnsatisfiedRange;
            result.Detail = nearest is null
                ? "no installed version matches the required range"
                : $"no installed version matches {effective.Constraint.Display} (nearest {nearest.Version})";
        }

        return result;
    }

    private static bool Satisfies(Requirement requirement, VersionNumber version, ToolKind tool)
    {
        if (tool == ToolKind.DotNetSdk)
            return DotNetRollForward.Satisfies(requirement.Constraint, version, requirement.RollForward);

        return requirement.Constraint.Satisfies(version, tool);
    }

    /// <summary>
    /// The closest installed version to what was asked for: same major first, then the
    /// highest version below the requirement, then the lowest above it.
    /// </summary>
    private static Toolchain? Nearest(IReadOnlyList<Toolchain> installed, Requirement requirement, ToolKind tool)
    {
        var target = requirement.Constraint.Exact
                     ?? requirement.Constraint.Min
                     ?? requirement.Constraint.Max;
        if (target is null) return installed.Count == 0 ? null : installed[0];

        var ordered = installed
            .OrderBy(t => Math.Abs(t.Parsed!.Major - target.Major))
            .ThenBy(t => t.Parsed!.CompareTo(target))
            .ToList();

        return ordered.FirstOrDefault();
    }

    private static bool Conflicts(Requirement a, Requirement b)
    {
        if (a.Tool != b.Tool) return false;

        var ca = a.Constraint;
        var cb = b.Constraint;

        if (ca.Kind == ConstraintKind.Unknown || cb.Kind == ConstraintKind.Unknown) return false;
        if (ca.Exact is null || cb.Kind == ConstraintKind.Any) return false;

        // The interesting question is never "do two strings differ", it is
        // "does the version we will actually use violate the other declaration".
        // That catches ".nvmrc: 18" against "engines: >=20", which is a real bug
        // in the repository and must be surfaced rather than silently resolved.
        if (ca.Kind == ConstraintKind.Exact)
            return !cb.Satisfies(ca.Exact, a.Tool);

        return false;
    }

    /// <summary>Cross-project conflicts, aggregated per version pair rather than per project pair.</summary>
    public static IReadOnlyList<ConflictFinding> FindConflicts(
        IEnumerable<ProjectRecord> projects,
        IReadOnlyList<Toolchain>? inventory = null)
    {
        var findings = new List<ConflictFinding>();

        var pins = projects
            .Where(p => p.State == ProjectState.Active)
            .SelectMany(p => p.Requirements
                .Where(r => r.Strength == RequirementStrength.Pinned)
                .Where(r => r.Provenance.Kind != ProvenanceKind.Inferred)   // inherited pins are not independent opinions
                .Where(r => r.Constraint.Kind is ConstraintKind.Exact or ConstraintKind.Toolchain)
                .Where(r => r.Constraint.Exact is not null)
                // A Gradle or Maven wrapper downloads its own version per project, so two
                // projects wanting different versions is normal, not a conflict.
                .Where(r => r.Tool is not (ToolKind.Gradle or ToolKind.Maven))
                .Select(r => (Project: p, Requirement: r)))
            .ToList();

        foreach (var toolGroup in pins.GroupBy(x => x.Requirement.Tool))
        {
            var byVersion = toolGroup
                .GroupBy(x => x.Requirement.Constraint.Exact!.ToString())
                .ToDictionary(g => g.Key, g => g.ToList());

            var versions = byVersion.Keys
                .Select(key => (Key: key, Version: DevTools.Core.Versioning.VersionNumber.Parse(key)))
                .OrderBy(pair => pair.Version)
                .ToList();

            for (var i = 0; i < versions.Count; i++)
            {
                for (var j = i + 1; j < versions.Count; j++)
                {
                    var a = versions[i];
                    var b = versions[j];

                    // A roll-forward policy can reconcile two different .NET pins.
                    if (toolGroup.Key == ToolKind.DotNetSdk)
                    {
                        var policyA = byVersion[a.Key][0].Requirement.RollForward;
                        var policyB = byVersion[b.Key][0].Requirement.RollForward;

                        var constraintA = DevTools.Core.Constraints.ConstraintParser.Parse(ToolKind.DotNetSdk, a.Key);
                        var constraintB = DevTools.Core.Constraints.ConstraintParser.Parse(ToolKind.DotNetSdk, b.Key);

                        if (DotNetRollForward.Satisfies(constraintA, b.Version, policyA) ||
                            DotNetRollForward.Satisfies(constraintB, a.Version, policyB))
                            continue;
                    }

                    findings.Add(new ConflictFinding
                    {
                        Tool = toolGroup.Key,
                        VersionA = a.Key,
                        VersionB = b.Key,
                        ProjectsA = byVersion[a.Key].Select(x => x.Project.DisplayName).Distinct().Take(5).ToList(),
                        ProjectsB = byVersion[b.Key].Select(x => x.Project.DisplayName).Distinct().Take(5).ToList(),
                        ManagerCanHoldBoth = CanHoldBoth(toolGroup.Key, inventory)
                    });
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Whether two versions can coexist. Evidence-based: the .NET SDK is side-by-side by
    /// design, everything else needs either a version manager or two installs already.
    /// </summary>
    private static bool CanHoldBoth(ToolKind tool, IReadOnlyList<Toolchain>? inventory)
    {
        if (tool == ToolKind.DotNetSdk) return true;
        if (inventory is null) return false;

        var distinct = inventory
            .Where(t => t.Tool == tool)
            .Select(t => t.Version)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return distinct >= 2;
    }
}
