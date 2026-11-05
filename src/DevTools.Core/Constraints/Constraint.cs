using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Core.Constraints;

public enum ConstraintKind
{
    /// <summary>A single version that must match. Mismatch is an error.</summary>
    Exact,
    /// <summary>An interval, possibly open on either end.</summary>
    Range,
    /// <summary>Declared, but unconstrained ("*", empty, "latest").</summary>
    Any,
    /// <summary>"An LTS line of this tool" — <c>lts/*</c>, <c>lts/iron</c>.</summary>
    Lts,
    /// <summary>"Whatever is newest" — resolved against the catalog, not the disk.</summary>
    Latest,
    /// <summary>A named distribution such as SDKMAN's <c>21.0.2-tem</c>.</summary>
    Toolchain,
    /// <summary>Alternatives ("^8.0 || ^7.4"). Satisfied when any alternative is.</summary>
    Union,
    /// <summary>The file said something we could not interpret. Always carries a reason.</summary>
    Unknown
}

/// <summary>
/// The normalised, comparable form of a project's version requirement.
/// Every ecosystem's syntax funnels into this one type so the resolution engine
/// never needs to know about <c>.nvmrc</c> or PEP 440.
/// </summary>
public sealed class Constraint
{
    public ConstraintKind Kind { get; init; } = ConstraintKind.Any;

    public VersionNumber? Exact { get; init; }

    public VersionNumber? Min { get; init; }
    public bool MinInclusive { get; init; } = true;

    public VersionNumber? Max { get; init; }
    public bool MaxInclusive { get; init; }

    /// <summary>LTS codename ("iron") or null for "any LTS line".</summary>
    public string? LtsName { get; init; }

    /// <summary>Named distribution, e.g. "21.0.2-tem".</summary>
    public string? ToolchainId { get; init; }

    /// <summary>Alternatives, when the declaration was a union ("^8.0 || ^7.4").</summary>
    public IReadOnlyList<Constraint> Alternatives { get; init; } = [];

    /// <summary>The text this constraint was built from, kept for display and re-parsing.</summary>
    public string Raw { get; init; } = "";

    /// <summary>Human explanation when <see cref="Kind"/> is <see cref="ConstraintKind.Unknown"/>.</summary>
    public string? Reason { get; init; }

    public static Constraint Any(string raw = "*") => new() { Kind = ConstraintKind.Any, Raw = raw };

    public static Constraint ExactOf(VersionNumber version, string? raw = null) =>
        new() { Kind = ConstraintKind.Exact, Exact = version, Raw = raw ?? version.ToString() };

    public static Constraint Between(VersionNumber? min, bool minInclusive, VersionNumber? max, bool maxInclusive, string raw) =>
        new()
        {
            Kind = ConstraintKind.Range,
            Min = min, MinInclusive = minInclusive,
            Max = max, MaxInclusive = maxInclusive,
            Raw = raw
        };

    public static Constraint AtLeast(VersionNumber min, string raw) =>
        Between(min, true, null, false, raw);

    public static Constraint LtsOf(string? name, string raw = "lts/*") =>
        new() { Kind = ConstraintKind.Lts, LtsName = name, Raw = raw };

    public static Constraint LatestOf(string raw = "latest") =>
        new() { Kind = ConstraintKind.Latest, Raw = raw };

    public static Constraint ToolchainOf(string id, string? versionText, string raw) =>
        new()
        {
            Kind = ConstraintKind.Toolchain,
            ToolchainId = id,
            Exact = VersionNumber.TryParseOrNull(versionText),
            Raw = raw
        };

    public static Constraint UnionOf(IReadOnlyList<Constraint> alternatives, string raw) =>
        new() { Kind = ConstraintKind.Union, Alternatives = alternatives, Raw = raw };

    public static Constraint UnknownOf(string raw, string reason) =>
        new() { Kind = ConstraintKind.Unknown, Raw = raw, Reason = reason };

    /// <summary>
    /// Does <paramref name="version"/> satisfy this constraint? Ignore <see cref="ConstraintKind.Latest"/>:
    /// that is resolved by the engine against the installed set, never by this predicate.
    /// </summary>
    public bool Satisfies(VersionNumber version, ToolKind tool)
    {
        switch (Kind)
        {
            case ConstraintKind.Exact:
                return Exact is not null && SameSeries(version, Exact, tool);

            case ConstraintKind.Range:
            {
                if (Min is not null)
                {
                    var cmp = Compare(version, Min, tool);
                    if (cmp < 0 || (cmp == 0 && !MinInclusive)) return false;
                }

                if (Max is not null)
                {
                    var cmp = Compare(version, Max, tool);
                    if (cmp > 0 || (cmp == 0 && !MaxInclusive)) return false;
                }

                return true;
            }

            case ConstraintKind.Any:
                return true;

            case ConstraintKind.Lts:
                return LtsRules.Matches(tool, LtsName, version);

            case ConstraintKind.Toolchain:
                return Exact is null || SameSeries(version, Exact, tool);

            case ConstraintKind.Union:
                return Alternatives.Any(alternative => alternative.Satisfies(version, tool));

            default:
                return false;
        }
    }

    /// <summary>
    /// Exactness is applied at the granularity the ecosystem actually means.
    /// <c>.nvmrc: "18"</c> means the 18.x line; <c>global.json: "8.0.100"</c> means that exact SDK;
    /// <c>TargetFramework: net10.0</c> means any 10.0.x feature band.
    /// </summary>
    private static bool SameSeries(VersionNumber candidate, VersionNumber target, ToolKind tool)
    {
        var c = tool == ToolKind.Jdk ? JavaVersions.Normalize(candidate) : candidate;
        var t = tool == ToolKind.Jdk ? JavaVersions.Normalize(target) : target;

        // A pre-release target is only satisfied by that exact pre-release.
        if (t.PreRelease is not null) return c.CompareTo(t) == 0;

        // Compare exactly the parts the requirement actually specified.
        for (var i = 0; i < t.Parts.Count; i++)
        {
            if (c.PartAt(i) != t.PartAt(i)) return false;
        }

        return true;
    }

    private static int Compare(VersionNumber a, VersionNumber b, ToolKind tool) =>
        tool == ToolKind.Jdk
            ? JavaVersions.Normalize(a).CompareTo(JavaVersions.Normalize(b))
            : a.CompareTo(b);

    /// <summary>Compact human form, e.g. ">= 20 < 21", "18.20.2", "any LTS".</summary>
    public string Display()
    {
        switch (Kind)
        {
            case ConstraintKind.Exact:
                return Exact?.ToString() ?? Raw;
            case ConstraintKind.Range:
            {
                var parts = new List<string>();
                if (Min is not null) parts.Add((MinInclusive ? ">= " : "> ") + Min);
                if (Max is not null) parts.Add((MaxInclusive ? "<= " : "< ") + Max);
                return parts.Count == 0 ? "*" : string.Join(" ", parts);
            }
            case ConstraintKind.Lts:
                return LtsName is null ? "any LTS" : "LTS " + LtsName;
            case ConstraintKind.Latest:
                return "latest";
            case ConstraintKind.Toolchain:
                return ToolchainId ?? Raw;
            case ConstraintKind.Union:
                return Alternatives.Count == 0 ? Raw : string.Join(" | ", Alternatives.Select(a => a.Display()));
            case ConstraintKind.Unknown:
                return Raw.Length > 0 ? Raw : "?";
            default:
                return "any";
        }
    }

    /// <summary>Upper bound implied by a caret/tilde range; used when building ranges in the parser.</summary>
    public override string ToString() => $"{Kind}:{Display()}";
}

/// <summary>
/// LTS membership. Node is the only ecosystem in scope with a codename scheme, and
/// the codename→major table is bundled rather than fetched so resolution never needs
/// the network.
/// </summary>
public static class LtsRules
{
    private static readonly Dictionary<string, int> NodeCodenameToMajor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = 4,
        ["boron"] = 6,
        ["carbon"] = 8,
        ["dubnium"] = 10,
        ["erbium"] = 12,
        ["fermium"] = 14,
        ["gallium"] = 16,
        ["hydrogen"] = 18,
        ["iron"] = 20,
        ["jod"] = 22,
        ["krypton"] = 24
    };

    /// <summary>The newest Node LTS major this build knows about.</summary>
    public const int NewestKnownNodeLts = 24;

    public static int? NodeMajorForCodename(string? codename) =>
        codename is not null && NodeCodenameToMajor.TryGetValue(codename, out var major) ? major : null;

    public static bool Matches(ToolKind tool, string? ltsName, VersionNumber version)
    {
        if (tool != ToolKind.Node) return false;

        if (ltsName is not null)
        {
            var major = NodeMajorForCodename(ltsName);
            return major is not null && version.Major == major;
        }

        // "lts/*": any even-numbered, currently-supported LTS line.
        return version.Major % 2 == 0 && version.Major >= 12 && version.Major <= NewestKnownNodeLts + 2;
    }
}
