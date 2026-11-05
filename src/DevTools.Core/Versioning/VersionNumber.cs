using System.Globalization;

namespace DevTools.Core.Versioning;

/// <summary>
/// A parsed version with numeric parts plus an optional pre-release suffix.
/// Comparison is numeric-part-wise (missing parts are zero), then pre-release
/// rules identical to semver: <c>1.0.0-alpha &lt; 1.0.0</c>.
/// </summary>
/// <remarks>
/// Deliberately not a record: equality must be value-based over the parts array,
/// and a record would compare the array by reference.
/// </remarks>
public sealed class VersionNumber : IComparable<VersionNumber>, IEquatable<VersionNumber>
{
    private readonly int[] _parts;

    private VersionNumber(string raw, int[] parts, string? preRelease)
    {
        Raw = raw;
        _parts = parts;
        PreRelease = preRelease;
    }

    /// <summary>The string this version was parsed from.</summary>
    public string Raw { get; }

    public IReadOnlyList<int> Parts => _parts;

    public string? PreRelease { get; }

    public int Major => _parts.Length > 0 ? _parts[0] : 0;
    public int Minor => _parts.Length > 1 ? _parts[1] : 0;
    public int Patch => _parts.Length > 2 ? _parts[2] : 0;

    public int PartAt(int index) => index < _parts.Length ? _parts[index] : 0;

    public static VersionNumber FromParts(params int[] parts)
    {
        var raw = string.Join('.', parts);
        return new VersionNumber(raw, parts.Length == 0 ? [0] : parts, null);
    }

    public VersionNumber WithPreRelease(string? pre) =>
        new(Raw + (pre is null ? "" : "-" + pre), _parts, pre);

    /// <summary>
    /// Parses a version out of arbitrary tool output. Tolerates vendor prefixes
    /// (<c>temurin-21.0.2+13</c>), leading <c>v</c>, and Java's <c>1.8.0_292</c>.
    /// </summary>
    public static bool TryParse(string? text, out VersionNumber? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim().Trim('"', '\'', '(', ')', ',', ';').Trim();
        if (s.Length == 0) return false;

        // Drop a leading "v"/"V" ("v20.11.0").
        if ((s[0] == 'v' || s[0] == 'V') && s.Length > 1 && char.IsDigit(s[1]))
            s = s[1..];

        // Skip a vendor/name prefix such as "temurin-" or "jdk-" or "java version ".
        var start = 0;
        while (start < s.Length && !char.IsDigit(s[start])) start++;
        if (start >= s.Length) return false;
        if (start > 0) s = s[start..];

        // A version must not start with a separator.
        if (s[0] == '.' || s[0] == '_' || s[0] == '-') return false;

        var parts = new List<int>();
        var i = 0;
        var numericEnd = 0;

        while (i < s.Length)
        {
            if (char.IsAsciiDigit(s[i]))
            {
                var runStart = i;
                while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
                if (!int.TryParse(s.AsSpan(runStart, i - runStart), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var value))
                    return false; // absurdly long numeric run: not a version
                parts.Add(value);
                numericEnd = i;
                continue;
            }

            if (s[i] is '.' or '_')
            {
                i++;
                continue;
            }

            break;
        }

        if (parts.Count == 0) return false;

        var suffix = s[numericEnd..].TrimStart('.', '_');
        string? preRelease = null;

        if (suffix.Length > 0)
        {
            // '+' is build metadata and never affects ordering.
            var plus = suffix.IndexOf('+');
            if (plus >= 0) suffix = suffix[..plus];

            suffix = suffix.Trim('-', '.');

            // Java update/build suffixes ("b10", "u292") are not ordering-relevant
            // in the pre-release sense; treat everything after the numeric core as
            // a pre-release only when it looks like a semver-ish qualifier.
            if (suffix.Length > 0 && !suffix.All(char.IsAsciiDigit))
                preRelease = suffix;
        }

        version = new VersionNumber(text.Trim(), parts.ToArray(), preRelease);
        return true;
    }

    public static VersionNumber? TryParseOrNull(string? text) =>
        TryParse(text, out var v) ? v : null;

    public static VersionNumber Parse(string text) =>
        TryParse(text, out var v) ? v! : throw new FormatException($"Not a version: '{text}'");

    public int CompareTo(VersionNumber? other)
    {
        if (other is null) return 1;

        var count = Math.Max(_parts.Length, other._parts.Length);
        for (var i = 0; i < count; i++)
        {
            var cmp = PartAt(i).CompareTo(other.PartAt(i));
            if (cmp != 0) return cmp;
        }

        var thisPre = PreRelease is not null;
        var otherPre = other.PreRelease is not null;

        if (!thisPre && !otherPre) return 0;
        if (!thisPre) return 1;   // release > pre-release
        if (!otherPre) return -1;

        return ComparePreRelease(PreRelease!, other.PreRelease!);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var n = Math.Max(x.Length, y.Length);

        for (var i = 0; i < n; i++)
        {
            if (i >= x.Length) return -1;
            if (i >= y.Length) return 1;

            var xi = x[i];
            var yi = y[i];
            var xNum = long.TryParse(xi, NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNum = long.TryParse(yi, NumberStyles.None, CultureInfo.InvariantCulture, out var yn);

            int cmp = (xNum, yNum) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1, // numeric identifiers sort before alphanumeric
                (false, true) => 1,
                _ => string.CompareOrdinal(xi, yi)
            };

            if (cmp != 0) return cmp;
        }

        return 0;
    }

    public bool Equals(VersionNumber? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is VersionNumber v && Equals(v);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        // Trailing zeros are insignificant: 8 == 8.0 == 8.0.0
        var last = _parts.Length - 1;
        while (last > 0 && _parts[last] == 0) last--;
        for (var i = 0; i <= last; i++) hash.Add(_parts[i]);
        hash.Add(PreRelease?.ToLowerInvariant());
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var core = string.Join('.', _parts);
        return PreRelease is null ? core : core + "-" + PreRelease;
    }

    public static bool operator <(VersionNumber a, VersionNumber b) => a.CompareTo(b) < 0;
    public static bool operator >(VersionNumber a, VersionNumber b) => a.CompareTo(b) > 0;
    public static bool operator <=(VersionNumber a, VersionNumber b) => a.CompareTo(b) <= 0;
    public static bool operator >=(VersionNumber a, VersionNumber b) => a.CompareTo(b) >= 0;
    public static bool operator ==(VersionNumber? a, VersionNumber? b) =>
        a is null ? b is null : a.Equals(b);
    public static bool operator !=(VersionNumber? a, VersionNumber? b) => !(a == b);
}
