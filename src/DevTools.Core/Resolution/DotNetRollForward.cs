using DevTools.Core.Constraints;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Core.Resolution;

/// <summary>
/// .NET's <c>rollForward</c> policy decides whether the pinned SDK version is a hard
/// requirement or a floor. Ignoring it produces the most annoying possible false alarm:
/// "SDK 10.0.300 is missing" on a machine with 10.0.403, where the project builds fine.
/// </summary>
/// <remarks>
/// Policies per the SDK's documented behaviour. When a policy is absent the SDK defaults
/// to <c>minor</c>, so that is what we assume.
/// </remarks>
public static class DotNetRollForward
{
    public static bool Satisfies(Constraint constraint, VersionNumber installed, string? policy)
    {
        if (constraint.Kind != ConstraintKind.Exact || constraint.Exact is null)
            return constraint.Satisfies(installed, ToolKind.DotNetSdk);

        var required = constraint.Exact;

        switch ((policy ?? "minor").Trim().ToLowerInvariant())
        {
            case "disable":
                return installed.CompareTo(required) == 0;

            case "patch":
                // Same feature band, patch level may move.
                return installed.Major == required.Major
                       && installed.Minor == required.Minor
                       && installed.PartAt(2) == required.PartAt(2)
                       && installed >= required;

            case "feature":
            case "latestfeature":
                return installed.Major == required.Major
                       && installed.Minor == required.Minor
                       && installed >= required;

            case "minor":
            case "latestminor":
                return installed.Major == required.Major && installed >= required;

            case "major":
            case "latestmajor":
                return installed >= required;

            default:
                return installed.CompareTo(required) == 0;
        }
    }

    /// <summary>Human note for the UI when a roll-forward policy did the work.</summary>
    public static string? Explain(Constraint constraint, VersionNumber installed, string? policy)
    {
        if (constraint.Exact is null) return null;
        if (installed.CompareTo(constraint.Exact) == 0) return null;

        return $"resolved by rollForward {(policy ?? "minor")} (wanted {constraint.Exact}, found {installed})";
    }
}
