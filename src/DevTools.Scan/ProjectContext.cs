using DevTools.Core.Model;
using DevTools.Scan.Parsers;

namespace DevTools.Scan;

public interface IProjectParser
{
    string Id { get; }

    Ecosystem Ecosystem { get; }

    void Extract(ProjectContext context);
}

/// <summary>
/// Everything a parser may read and write for one project. Parsers never touch the
/// filesystem directly, which is what makes them testable without fixtures on disk.
/// </summary>
public sealed class ProjectContext
{
    public required string Directory { get; init; }

    public required IProjectFileSystem Fs { get; init; }

    public List<Requirement> Requirements { get; } = [];

    public List<Diagnostic> Diagnostics { get; } = [];

    public HashSet<Ecosystem> Ecosystems { get; } = [];

    public List<string> Lockfiles { get; } = [];

    public string? Name { get; set; }

    public bool IsWorkspaceRoot { get; set; }

    public List<string> WorkspaceMembers { get; } = [];

    public void Add(Requirement requirement)
    {
        Requirements.Add(requirement);
        Ecosystems.Add(ToolKinds.EcosystemOf(requirement.Tool));
    }

    public void Tag(Ecosystem ecosystem) => Ecosystems.Add(ecosystem);

    public void Warn(string code, string message, string? file = null, int? line = null) =>
        Diagnostics.Add(Diagnostic.Warn(code, message, Directory, file, line));

    public string? Read(string relativePath)
    {
        var text = Fs.ReadText(relativePath);
        if (text is null && Fs.FileExists(relativePath))
            Warn("input-unreadable", "File could not be read or exceeds the size limit.", relativePath);
        return text;
    }
}

public static class ParserText
{
    public static int LineOf(string text, int index)
    {
        var line = 1;
        var limit = Math.Min(index, text.Length);
        for (var i = 0; i < limit; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>First non-empty, non-comment line — how every pin file is read.</summary>
    public static (string Value, int Line)? FirstMeaningfulLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#') || line.StartsWith("//")) continue;
            return (line, i + 1);
        }

        return null;
    }

    public static IEnumerable<(string Text, int Number)> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            yield return (lines[i], i + 1);
    }

    /// <summary>Finds <c>key = value</c> / <c>key: value</c> and returns the value with its line.</summary>
    public static (string Value, int Line)? Assignment(string? text, string key)
    {
        foreach (var (line, number) in Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//")) continue;

            if (!trimmed.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

            var rest = trimmed[key.Length..].TrimStart();
            if (rest.Length == 0 || (rest[0] != '=' && rest[0] != ':')) continue;

            var value = rest[1..].Trim().TrimEnd(',', ';');
            if (value.Length == 0) continue;
            return (Unquote(value), number);
        }

        return null;
    }

    public static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    /// <summary>
    /// Accepts a manifest name only when it is a real name. POMs and templates routinely
    /// carry <c>${artifactId}</c>, which must fall back to the directory name instead of
    /// producing a project called "${artifactId}".
    /// </summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var trimmed = name.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Contains("${", StringComparison.Ordinal)) return null;
        if (trimmed.Contains('<') || trimmed.Contains('>')) return null;

        return trimmed.Length <= 80 ? trimmed : trimmed[..80];
    }

    /// <summary>
    /// Resolves <c>${property}</c> placeholders, up to three passes. Maven and Gradle both
    /// lean on these, and an unresolved placeholder must surface as "unknown", not as a
    /// bogus version.
    /// </summary>
    public static string? ResolvePlaceholders(string value, IReadOnlyDictionary<string, string> properties)
    {
        var current = value;
        for (var pass = 0; pass < 3; pass++)
        {
            if (!current.Contains("${", StringComparison.Ordinal)) return current;

            var start = current.IndexOf("${", StringComparison.Ordinal);
            var end = current.IndexOf('}', start);
            if (end < 0) return null;

            var key = current[(start + 2)..end].Trim();
            if (!properties.TryGetValue(key, out var replacement)) return null;

            current = current[..start] + replacement + current[(end + 1)..];
        }

        return current.Contains("${", StringComparison.Ordinal) ? null : current;
    }
}
