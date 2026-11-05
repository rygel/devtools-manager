using DevTools.Core.Constraints;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Scan.Parsers;

/// <summary>
/// Reads the tool versions a project's GitHub Actions workflows install (<c>actions/setup-node</c>,
/// <c>setup-java</c>, <c>setup-python</c>, <c>setup-go</c>, <c>setup-dotnet</c>) and compares them with
/// what the project itself declares. CI versions are advisory requirements: they never change what a
/// project needs locally, they only expose drift. Only literal values are read; expressions
/// (<c>${{ matrix.node }}</c>), lists and block scalars are skipped rather than guessed, and
/// <c>*-version-file</c> inputs are skipped because they read the same file the project already declares.
/// </summary>
public sealed class CiParser : IProjectParser
{
    private const string WorkflowDirectory = ".github/workflows";

    private static readonly Dictionary<string, (ToolKind Tool, string Input)> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["actions/setup-node"] = (ToolKind.Node, "node-version"),
        ["actions/setup-java"] = (ToolKind.Jdk, "java-version"),
        ["actions/setup-python"] = (ToolKind.Python, "python-version"),
        ["actions/setup-go"] = (ToolKind.Go, "go-version"),
        ["actions/setup-dotnet"] = (ToolKind.DotNetSdk, "dotnet-version")
    };

    public string Id => "ci";

    // Not tied to one ecosystem: the scanner runs this parser for every project.
    public Ecosystem Ecosystem => Ecosystem.Container;

    public void Extract(ProjectContext context)
    {
        if (!context.Fs.DirectoryExists(WorkflowDirectory)) return;

        var files = context.Fs.ListFiles(WorkflowDirectory, "*.yml")
            .Concat(context.Fs.ListFiles(WorkflowDirectory, "*.yaml"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(32);

        // Local declarations are captured first so CI entries added below are never compared to each other.
        var local = context.Requirements.Where(r => r.Strength != RequirementStrength.Advisory).ToList();

        // The same version is usually repeated across several workflows; one entry is enough.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in files)
        {
            var path = WorkflowDirectory + "/" + name;
            var text = context.Read(path);
            if (text is null) continue;

            foreach (var (tool, value, line, key) in ReadSetups(text))
            {
                // A workflow may set up tools unrelated to the project (a docs build, say); those are not its toolchain.
                if (!context.Ecosystems.Contains(ToolKinds.EcosystemOf(tool))) continue;
                if (!seen.Add(tool + "|" + value)) continue;

                var provenance = Provenance.FileOf(path, line, key);
                context.Requirements.Add(Requirement.Advisory(tool, value, provenance));
                ReportDrift(context, local, tool, value, path, line);
            }
        }
    }

    private static void ReportDrift(ProjectContext context, List<Requirement> local, ToolKind tool, string value, string path, int line)
    {
        var ci = ConstraintParser.Parse(tool, value);
        foreach (var requirement in local.Where(r => r.Tool == tool))
        {
            var declared = ConstraintParser.Parse(tool, requirement.Raw);
            if (!Conflicts(ci, declared, tool)) continue;

            context.Diagnostics.Add(Diagnostic.Warn(ProjectRecord.DriftCode,
                $"CI installs {ToolKinds.Display(tool)} {value} ({path}:{line}) but the project declares {requirement.Raw} ({requirement.Provenance.Describe()})",
                context.Directory, path, line));
            return;
        }
    }

    /// <summary>
    /// Two constraints conflict when neither accepts the other's representative version (its pin, or its
    /// lower bound). Anything that is not a plain pin or range says nothing definite, so it never conflicts.
    /// </summary>
    private static bool Conflicts(Constraint a, Constraint b, ToolKind tool)
    {
        var left = Representative(a);
        var right = Representative(b);
        if (left is null || right is null) return false;

        return !a.Satisfies(right, tool) && !b.Satisfies(left, tool);
    }

    private static VersionNumber? Representative(Constraint constraint) => constraint.Kind switch
    {
        ConstraintKind.Exact => constraint.Exact,
        ConstraintKind.Range => constraint.Min,
        _ => null
    };

    private static IEnumerable<(ToolKind Tool, string Value, int Line, string Key)> ReadSetups(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var (keyColumn, rest) = KeyOf(lines[index], "uses");
            if (keyColumn < 0) continue;

            var action = rest.Split('@')[0].Trim();
            if (!Actions.TryGetValue(action, out var setup)) continue;

            var value = FindInput(lines, index + 1, keyColumn, setup.Input, out var line);
            if (value is not null) yield return (setup.Tool, value, line, setup.Input);
        }
    }

    /// <summary>Scans the step that owns the <c>uses</c> line: every following line indented at least as far as its keys.</summary>
    private static string? FindInput(string[] lines, int from, int keyColumn, string input, out int line)
    {
        line = 0;
        var inWith = false;
        for (var index = from; index < lines.Length; index++)
        {
            var raw = lines[index];
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;

            var indent = raw.Length - raw.TrimStart().Length;
            if (indent < keyColumn) return null;

            if (indent == keyColumn)
            {
                inWith = raw.TrimStart().StartsWith("with:", StringComparison.Ordinal);
                continue;
            }

            if (!inWith) continue;

            var (column, value) = KeyOf(raw, input);
            if (column < 0) continue;

            line = index + 1;
            return Literal(value);
        }

        return null;
    }

    /// <summary>The column of <paramref name="key"/> on this line (after an optional "- "), and the text after the colon.</summary>
    private static (int Column, string Text) KeyOf(string raw, string key)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal)) trimmed = trimmed[2..].TrimStart();
        var column = raw.Length - trimmed.Length;

        return trimmed.StartsWith(key + ":", StringComparison.Ordinal) ? (column, trimmed[(key.Length + 1)..].Trim()) : (-1, "");
    }

    private static string? Literal(string raw)
    {
        var value = raw;
        if (!value.StartsWith('"') && !value.StartsWith('\''))
        {
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment];
        }

        value = value.Trim().Trim('"', '\'').Trim();
        if (value.Length == 0 || value.Contains("${{", StringComparison.Ordinal)) return null;
        if (value[0] is '[' or '|' or '>' or '{') return null;
        return value;
    }
}
