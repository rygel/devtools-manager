using DevTools.Core.Versioning;

namespace DevTools.Core.Versioning;

/// <summary>
/// Java's version numbering is the most common source of wrong advice in tooling
/// like this, so it gets its own normaliser.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>1.8</c>, <c>1.8.0_292</c>, <c>JavaVersion.VERSION_1_8</c> all mean Java 8.</item>
/// <item><c>21</c>, <c>21.0.2</c>, <c>temurin-21.0.2+13</c> all mean Java 21.</item>
/// <item>Vendor suffixes (<c>-tem</c>, <c>-open</c>, <c>+13</c>) never change the requirement,
///   only which <em>distribution</em> satisfies it.</item>
/// </list>
/// </remarks>
public static class JavaVersions
{
    /// <summary>Collapses the legacy <c>1.x</c> scheme to the modern major-only scheme.</summary>
    public static VersionNumber Normalize(VersionNumber version)
    {
        if (version.Parts.Count < 2 || version.Parts[0] != 1) return version;

        var rest = version.Parts.Skip(1).ToArray();
        // 1.8.0_292 -> 8.0.292 ; 1.8 -> 8
        return VersionNumber.FromParts(rest).WithPreRelease(version.PreRelease);
    }

    /// <summary>Strips a vendor suffix from an SDKMAN-style identifier: <c>21.0.2-tem</c> → <c>21.0.2</c>.</summary>
    public static string StripVendor(string candidate)
    {
        var dash = candidate.IndexOf('-');
        return dash > 0 && char.IsDigit(candidate[0]) ? candidate[..dash] : candidate;
    }

    /// <summary>The Java major version, which is what almost every requirement actually means.</summary>
    public static int Major(VersionNumber version) => Normalize(version).Major;

    /// <summary>
    /// True when <paramref name="installed"/> can satisfy a requirement of
    /// <paramref name="requiredMajor"/>: JDK 21.0.2 satisfies "21", and 8u292 satisfies "8".
    /// </summary>
    public static bool SatisfiesMajor(VersionNumber installed, int requiredMajor) =>
        Major(installed) == requiredMajor;

    /// <summary>Reads a Gradle/Java source-compatibility string such as <c>"1.8"</c> or <c>JavaVersion.VERSION_17</c>.</summary>
    public static bool TryParseCompatibility(string? text, out VersionNumber? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim().Trim('"', '\'');

        // JavaVersion.VERSION_1_8 / VERSION_17
        var marker = s.IndexOf("VERSION_", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) s = s[(marker + "VERSION_".Length)..];

        // VERSION_1_8 -> 1.8 ; VERSION_17 -> 17
        if (s.Contains('_') && !s.Contains('.')) s = s.Replace('_', '.');

        if (!VersionNumber.TryParse(s, out version)) return false;
        version = version!.PreRelease is null ? version : VersionNumber.FromParts(version.Parts.ToArray());
        return true;
    }
}
