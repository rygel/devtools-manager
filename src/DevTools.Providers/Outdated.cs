using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Providers;

public enum FreshnessState
{
    /// <summary>No cached data, or no release line matches the version. Never guessed.</summary>
    Unknown,
    UpToDate,
    Outdated
}

/// <summary>
/// Whether a newer release exists, from the release lines endoflife.date lists.
/// <paramref name="LatestInLine"/> is the newest release of the installed line (a patch update);
/// <paramref name="NewestLine"/> is the newest release of any line, set only when it is newer than that.
/// </summary>
public sealed record Freshness(FreshnessState State, string Installed, string? Line, string? LatestInLine, string? NewestLine)
{
    public static Freshness None(string installed) => new(FreshnessState.Unknown, installed, null, null, null);

    public string Describe() => State switch
    {
        FreshnessState.Outdated => $"{Installed} → {LatestInLine}" + (NewestLine is null ? "" : $"  ·  newest line {NewestLine}"),
        FreshnessState.UpToDate => "latest in the " + Line + " line" + (NewestLine is null ? "" : $"  ·  newest line {NewestLine}"),
        _ => "no release data"
    };
}

/// <summary>Compares an installed version with the newest release data already cached by <see cref="EndOfLifeService"/>.</summary>
public static class FreshnessAssessor
{
    /// <summary>
    /// The JDK entry covers Eclipse Temurin only and other vendors number builds differently; the .NET entry
    /// lists runtime versions while the inventory holds SDK versions. Comparing either would give wrong answers.
    /// </summary>
    private static readonly HashSet<ToolKind> NotComparable = [ToolKind.Jdk, ToolKind.DotNetSdk];

    public static bool CanCompare(ToolKind tool) => EndOfLifeProducts.For(tool) is not null && !NotComparable.Contains(tool);

    public static Freshness Assess(IReadOnlyList<EolCycle>? cycles, ToolKind tool, string version)
    {
        if (!CanCompare(tool) || cycles is null || cycles.Count == 0) return Freshness.None(version);
        if (!VersionNumber.TryParse(version, out var installed) || installed is null) return Freshness.None(version);

        var line = EndOfLifeAssessor.FindCycle(cycles, tool, version);
        if (line?.Latest is null || !VersionNumber.TryParse(line.Latest, out var latest) || latest is null)
            return Freshness.None(version);

        // endoflife.date lists the newest line first, but order is not relied on.
        var newest = cycles
            .Select(cycle => (Cycle: cycle, Version: VersionNumber.TryParseOrNull(cycle.Latest)))
            .Where(entry => entry.Version is not null)
            .OrderByDescending(entry => entry.Version)
            .FirstOrDefault();
        var newestLine = newest.Version is not null && newest.Version > latest ? newest.Cycle.Latest : null;

        return latest > installed
            ? new Freshness(FreshnessState.Outdated, version, line.Cycle, line.Latest, newestLine)
            : new Freshness(FreshnessState.UpToDate, version, line.Cycle, line.Latest, newestLine);
    }

    public static Freshness Assess(IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>> data, Toolchain toolchain) =>
        Assess(data.GetValueOrDefault(toolchain.Tool), toolchain.Tool, toolchain.Version);
}
