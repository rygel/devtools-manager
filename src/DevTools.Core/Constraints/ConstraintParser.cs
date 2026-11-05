using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Core.Constraints;

/// <summary>
/// Turns every ecosystem's version syntax into a <see cref="Constraint"/>.
/// The parser is intentionally permissive: an unparseable string becomes
/// <see cref="ConstraintKind.Unknown"/> with a reason, never an exception, because
/// one malformed file must not abort a project.
/// </summary>
public static partial class ConstraintParser
{
    [GeneratedRegex(@"(?<op>\^|~=|~>|~|>=|<=|>|<|=)?\s*(?<ver>\d[0-9A-Za-z.*+\-_]*)", RegexOptions.Compiled)]
    private static partial Regex ComparatorRegex();

    public static Constraint Parse(ToolKind tool, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Constraint.Any();

        var s = raw.Trim().Trim('"', '\'').Trim();
        if (s.Length == 0) return Constraint.Any();

        var lower = s.ToLowerInvariant();

        // Alternatives: Composer's "^7.3|^8.0" (either form of pipe), occasional npm and
        // Maven usage. Intersecting them would produce an empty range, so they stay a union.
        if (s.Contains('|'))
        {
            var alternatives = s
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => Parse(tool, part))
                .Where(constraint => constraint.Kind != ConstraintKind.Unknown)
                .ToList();

            if (alternatives.Count > 0) return Constraint.UnionOf(alternatives, s);
        }

        if (lower is "*" or "x" or "any" or "latest" or "current" or "stable")
            return lower == "latest" ? Constraint.LatestOf(s) : Constraint.Any(s);

        if (lower == "lts") return Constraint.LtsOf(null, s);
        if (lower.StartsWith("lts/", StringComparison.Ordinal))
        {
            var name = s[4..].Trim();
            return name is "*" or "" ? Constraint.LtsOf(null, s) : Constraint.LtsOf(name, s);
        }

        // Node's ".nvmrc" also accepts the bare word "node" meaning "any installed".
        if (lower == "node" && tool == ToolKind.Node) return Constraint.Any(s);

        // Nightly / stable rust channels.
        if (tool == ToolKind.Rustc && (lower == "nightly" || lower == "beta"))
            return Constraint.ToolchainOf(s, null, s);
        if (tool == ToolKind.Rustc && lower.StartsWith("nightly-", StringComparison.Ordinal))
            return Constraint.Any(s); // dated nightly: any installed nightly satisfies it loosely

        // SDKMAN candidate identifiers: "21.0.2-tem", "17.0.10+7".
        if (tool == ToolKind.Jdk && VendorSuffixRegex().IsMatch(s))
        {
            var versionPart = JavaVersions.StripVendor(s);
            return Constraint.ToolchainOf(s, versionPart, s);
        }

        // Maven-style interval: [1.0,2.0) or (,1.0].
        if (s[0] is '[' or '(') return ParseInterval(s);

        // Comparator lists: "^20.11.0", ">=18 <21", ">=3.9,<4", "~=3.10".
        if (s[0] is '^' or '~' or '>' or '<' or '=' || s.Contains(',') || s.Contains(' '))
            return ParseComparators(tool, s);

        return ParseLoose(tool, s);
    }

    [GeneratedRegex(@"^[0-9][0-9A-Za-z.*+_\-]*-(tem|open|zulu|amzn|amzn2|graal|graalce|ms|oracle|corretto|semeru|liberica|mandrel|nik|jetbrains|kona)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VendorSuffixRegex();

    /// <summary>Handles "18", "18.20.2", "20.x", "v20", "1.8", "3.12".</summary>
    private static Constraint ParseLoose(ToolKind tool, string s)
    {
        // Wildcard series: "20.x", "20.*", "1.8.x"
        if (s.Contains('x', StringComparison.OrdinalIgnoreCase) || s.Contains('*'))
        {
            var prefixText = s.Split('x', '*')[0].TrimEnd('.');
            if (VersionNumber.TryParse(prefixText, out var prefix) && prefix is not null)
            {
                var upperParts = prefix.Parts.ToArray();
                var bumpIndex = upperParts.Length - 1;
                upperParts[bumpIndex]++;
                var upper = VersionNumber.FromParts(upperParts);

                if (tool == ToolKind.Jdk) prefix = JavaVersions.Normalize(prefix);
                return Constraint.Between(prefix, true, upper, false, s);
            }
        }

        if (!VersionNumber.TryParse(s, out var version) || version is null)
            return Constraint.UnknownOf(s, "not a recognisable version");

        if (tool == ToolKind.Jdk) version = JavaVersions.Normalize(version);

        // A trailing ".*" style already handled; everything else is exact-at-its-granularity.
        return Constraint.ExactOf(version, s);
    }

    /// <summary>Handles "^20.11.0", "~1.2", ">=18 &lt;21", ">=3.9,<4", "~=3.10".</summary>
    private static Constraint ParseComparators(ToolKind tool, string s)
    {
        VersionNumber? min = null, max = null;
        var minInclusive = true;
        var maxInclusive = false;
        var matched = false;

        foreach (Match match in ComparatorRegex().Matches(s))
        {
            var op = match.Groups["op"].Success ? match.Groups["op"].Value : "";
            var verText = match.Groups["ver"].Value;

            if (!VersionNumber.TryParse(verText, out var v) || v is null) continue;
            if (tool == ToolKind.Jdk) v = JavaVersions.Normalize(v);
            matched = true;

            switch (op)
            {
                case "^":
                    SetLower(ref min, ref minInclusive, v, true);
                    SetUpper(ref max, ref maxInclusive, CaretUpper(v), false);
                    break;

                case "~":
                    SetLower(ref min, ref minInclusive, v, true);
                    SetUpper(ref max, ref maxInclusive, TildeUpper(v), false);
                    break;

                case "~=":
                case "~>":
                    // PEP 440 "compatible release" and Elixir/Ruby "pessimistic" both mean
                    // "same series": ~> 1.15 → >=1.15,<2.0 ; ~> 1.15.0 → >=1.15.0,<1.16.0
                    SetLower(ref min, ref minInclusive, v, true);
                    SetUpper(ref max, ref maxInclusive, Pep440CompatibleUpper(v), false);
                    break;

                case ">=":
                case "=":
                    SetLower(ref min, ref minInclusive, v, true);
                    break;

                case ">":
                    SetLower(ref min, ref minInclusive, v, false);
                    break;

                case "<=":
                    SetUpper(ref max, ref maxInclusive, v, true);
                    break;

                case "<":
                    SetUpper(ref max, ref maxInclusive, v, false);
                    break;

                default:
                    // Bare token inside a comparator list, e.g. "18 20" — treat as lower bound.
                    SetLower(ref min, ref minInclusive, v, true);
                    break;
            }
        }

        if (!matched) return ParseLoose(tool, s);
        if (min is null && max is null) return Constraint.Any(s);

        // A lone ">=" is a real range; a lone exact number never reaches here.
        return Constraint.Between(min, minInclusive, max, maxInclusive, s);
    }

    /// <summary>Maven interval notation: <c>[1.0,2.0)</c>, <c>(,1.0]</c>.</summary>
    private static Constraint ParseInterval(string s)
    {
        var minInclusive = s[0] == '[';
        var last = s[^1];
        var maxInclusive = last == ']';
        if (last is not (']' or ')'))
            return Constraint.UnknownOf(s, "unterminated interval");

        var inner = s[1..^1];
        var comma = inner.IndexOf(',');
        if (comma < 0) return Constraint.UnknownOf(s, "interval without a comma");

        var minText = inner[..comma].Trim();
        var maxText = inner[(comma + 1)..].Trim();

        var min = minText.Length == 0 ? null : VersionNumber.TryParseOrNull(minText);
        var max = maxText.Length == 0 ? null : VersionNumber.TryParseOrNull(maxText);

        if (min is null && max is null) return Constraint.UnknownOf(s, "empty interval");
        return Constraint.Between(min, minInclusive, max, maxInclusive, s);
    }

    private static void SetLower(ref VersionNumber? current, ref bool inclusive, VersionNumber candidate, bool candInclusive)
    {
        if (current is null || candidate > current || (candidate.Equals(current) && !candInclusive))
        {
            current = candidate;
            inclusive = candInclusive;
        }
    }

    private static void SetUpper(ref VersionNumber? current, ref bool inclusive, VersionNumber candidate, bool candInclusive)
    {
        if (current is null || candidate < current || (candidate.Equals(current) && !candInclusive))
        {
            current = candidate;
            inclusive = candInclusive;
        }
    }

    /// <summary>npm caret: ^1.2.3 → &gt;=1.2.3 &lt;2.0.0; ^0.2.3 → &lt;0.3.0; ^0.0.3 → &lt;0.0.4.</summary>
    internal static VersionNumber CaretUpper(VersionNumber v)
    {
        if (v.Major > 0 || v.Parts.Count == 1) return VersionNumber.FromParts(v.Major + 1, 0, 0);
        if (v.Minor > 0) return VersionNumber.FromParts(0, v.Minor + 1, 0);
        return VersionNumber.FromParts(0, 0, v.Patch + 1);
    }

    /// <summary>npm tilde: ~1.2.3 → &gt;=1.2.3 &lt;1.3.0; ~1 → &lt;2.0.0.</summary>
    internal static VersionNumber TildeUpper(VersionNumber v) =>
        v.Parts.Count >= 2
            ? VersionNumber.FromParts(v.Major, v.Minor + 1, 0)
            : VersionNumber.FromParts(v.Major + 1, 0, 0);

    /// <summary>PEP 440 ~=X.Y → &gt;=X.Y, &lt;X+1; ~=X.Y.Z → &gt;=X.Y.Z, &lt;X.(Y+1).</summary>
    internal static VersionNumber Pep440CompatibleUpper(VersionNumber v)
    {
        if (v.Parts.Count <= 1) return VersionNumber.FromParts(v.Major + 1, 0, 0);

        var parts = v.Parts.ToArray();
        var bumpIndex = parts.Length - 2;
        parts[bumpIndex]++;
        for (var i = bumpIndex + 1; i < parts.Length; i++) parts[i] = 0;
        return VersionNumber.FromParts(parts);
    }
}
