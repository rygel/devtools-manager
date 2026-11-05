using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Core.Versioning;
using DevTools.Scan;

namespace DevTools.Providers;

public sealed class MigrationOptions
{
    public ToolKind Tool { get; init; } = ToolKind.Node;

    /// <summary>The version to write, verbatim: "24", "24.4.1", "3.12", "21.0.2-tem".</summary>
    public string Target { get; init; } = "";

    /// <summary>Only touch projects currently on this major ("18" or "18.20.2").</summary>
    public string? From { get; init; }

    /// <summary>
    /// Range declarations (<c>engines.node</c>, <c>requires-python</c>) are skipped unless
    /// asked for: rewriting someone's compatibility range is a bigger decision than moving
    /// a pin, and silently narrowing it would break their published package.
    /// </summary>
    public bool IncludeRanges { get; init; }
}

public sealed class FileEdit
{
    public string ProjectName { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";

    /// <summary>Human description, e.g. <c>volta.node 18.20.2 → 24</c>.</summary>
    public string Change { get; init; } = "";

    public string FullPath => Path.Combine(ProjectPath, RelativePath);
}

public sealed class MigrationPlan
{
    public ToolKind Tool { get; init; }
    public string Target { get; init; } = "";
    public List<FileEdit> Edits { get; } = [];
    public List<string> Skipped { get; } = [];

    public int ProjectsAffected => Edits
        .Select(edit => edit.ProjectPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public bool IsEmpty => Edits.Count == 0;

    public string Fingerprint
    {
        get
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((int)Tool);
                writer.Write(Target);
                foreach (var edit in Edits.OrderBy(edit => edit.FullPath, StringComparer.Ordinal))
                {
                    writer.Write(edit.FullPath);
                    writer.Write(edit.Before);
                    writer.Write(edit.After);
                }
                foreach (var skipped in Skipped.Order(StringComparer.Ordinal)) writer.Write(skipped);
            }
            return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16];
        }
    }
}

/// <summary>
/// Computes the file edits needed to move projects from one toolchain version to another.
/// Pure apart from reading the files it is about to change: no writing happens here, so a
/// plan can always be shown before anything is touched.
/// </summary>
public static class MigrationPlanner
{
    public static bool IsValidTarget(string value) => !string.IsNullOrEmpty(value)
        && value.Length <= 100 && (char.IsAsciiDigit(value[0]) ||
            (value.Length > 1 && value[0] is 'v' or 'V' && char.IsAsciiDigit(value[1])))
        && VersionNumber.TryParseOrNull(value) is not null
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private static readonly string[] NodeFiles =
        [".nvmrc", ".node-version", ".tool-versions", "mise.toml", ".mise.toml", "package.json"];

    private static readonly string[] DenoFiles = [".dvmrc", ".tool-versions", "mise.toml", ".mise.toml"];

    private static readonly string[] PythonFiles =
        [".python-version", ".tool-versions", "mise.toml", ".mise.toml", "pyproject.toml"];

    private static readonly string[] JavaFiles =
        [".java-version", ".sdkmanrc", ".tool-versions", "mise.toml", ".mise.toml"];

    private static readonly string[] RubyFiles =
        [".ruby-version", ".tool-versions", "mise.toml", ".mise.toml"];

    private static readonly string[] DotNetFiles = ["global.json"];
    private static readonly string[] GradleFiles = ["gradle/wrapper/gradle-wrapper.properties"];
    private static readonly Regex GradleChecksum = new(@"(?m)^[ \t]*distributionSha256Sum(?:[ \t]*[:=]|[ \t]+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex GradleUrl = new(@"(?m)^([ \t]*distributionUrl[ \t]*=[ \t]*https\\?://services\.gradle\.org/distributions/gradle-)([0-9][0-9A-Za-z.+-]*?)(-(?:bin|all)\.zip[ \t]*\r?)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<string> FilesFor(ToolKind tool) => tool switch
    {
        ToolKind.Node => NodeFiles,
        ToolKind.Deno => DenoFiles,
        ToolKind.Python => PythonFiles,
        ToolKind.Jdk => JavaFiles,
        ToolKind.Ruby => RubyFiles,
        ToolKind.DotNetSdk => DotNetFiles,
        ToolKind.Gradle => GradleFiles,
        _ => []
    };

    public static MigrationPlan Plan(IEnumerable<ProjectRecord> projects, MigrationOptions options)
    {
        if (!IsValidTarget(options.Target)) throw new ArgumentException("The target must be a version identifier without spaces or control characters.", nameof(options));
        if (options.From is not null && !IsValidTarget(options.From))
            throw new ArgumentException("The source version filter is invalid.", nameof(options));
        var plan = new MigrationPlan { Tool = options.Tool, Target = options.Target };
        var plannedCharacters = 0L;

        foreach (var project in projects.Where(p => p.State == ProjectState.Active))
        {
            if (options.From is not null && !MatchesFrom(project, options)) continue;

            foreach (var relative in FilesFor(options.Tool))
            {
                var full = Path.Combine(project.Path, relative);
                if (!File.Exists(full)) continue;

                if (ArtifactCleanup.HasUnsafePath(full))
                {
                    plan.Skipped.Add($"{project.DisplayName}: {relative} — symbolic link or unavailable path");
                    continue;
                }

                var text = MigrationFile.Read(full, out _);
                if (text is null)
                {
                    plan.Skipped.Add($"{project.DisplayName}: {relative} — unreadable");
                    continue;
                }

                var result = Rewrite(relative, text, options);

                if (result.Content is null)
                {
                    plan.Skipped.Add($"{project.DisplayName}: {relative} — {result.Reason}");
                    continue;
                }

                if (result.Content == text) continue; // already on the target

                var editSize = (long)text.Length + result.Content.Length;
                if (plannedCharacters + editSize > 8 * 1024 * 1024)
                {
                    plan.Skipped.Add($"{project.DisplayName}: {relative} — migration review size limit reached");
                    continue;
                }
                plannedCharacters += editSize;

                plan.Edits.Add(new FileEdit
                {
                    ProjectName = project.DisplayName,
                    ProjectPath = project.Path,
                    RelativePath = relative,
                    Before = text,
                    After = result.Content,
                    Change = result.Description ?? options.Target
                });
            }
        }

        return plan;
    }

    private static bool MatchesFrom(ProjectRecord project, MigrationOptions options)
    {
        var from = VersionNumber.TryParseOrNull(options.From);
        if (from is null) return true;

        var current = project.Requirements
            .Where(requirement => requirement.Tool == options.Tool)
            .Select(requirement => requirement.Constraint.Exact ?? requirement.Constraint.Min)
            .FirstOrDefault(version => version is not null);

        return current is not null && current.Major == from.Major;
    }

    // ---------------------------------------------------------------- editors

    internal readonly record struct RewriteResult(string? Content, string? Description, string? Reason);

    private static RewriteResult Failed(string reason) => new(null, null, reason);

    private static RewriteResult Unchanged(string content) => new(content, null, null);

    private static RewriteResult Changed(string content, string description) => new(content, description, null);

    /// <summary>
    /// True when the current declaration already means the target. Asking to move "to 24"
    /// while a project is pinned to 24.21.0 should be a no-op, not a needless rewrite that
    /// throws away the patch version.
    /// </summary>
    internal static bool AlreadyOnTarget(string current, string target)
    {
        if (string.Equals(current, target, StringComparison.Ordinal)) return true;

        var desired = VersionNumber.TryParseOrNull(target);
        var present = VersionNumber.TryParseOrNull(current);
        if (desired is null || present is null) return false;

        // A single-part target ("24") names a release line, not a build.
        return desired.Parts.Count == 1
            ? present.Major == desired.Major
            : present.CompareTo(desired) == 0;
    }

    private static RewriteResult Rewrite(string relative, string content, MigrationOptions options)
    {
        if (relative.Replace('\\', '/').Equals("gradle/wrapper/gradle-wrapper.properties", StringComparison.OrdinalIgnoreCase))
            return RewriteGradleWrapper(content, options);
        switch (relative.ToLowerInvariant())
        {
            case ".nvmrc":
            case ".node-version":
            case ".dvmrc":
            case ".python-version":
            case ".java-version":
            case ".ruby-version":
                return RewriteFirstLine(content, options.Target, relative);

            case ".tool-versions":
                return RewriteToolVersions(content, options);

            case "mise.toml":
            case ".mise.toml":
                return RewriteMise(content, options);

            case ".sdkmanrc":
                return RewriteSdkmanrc(content, options);

            case "package.json":
                return RewritePackageJson(content, options);

            case "global.json":
                return RewriteGlobalJson(content, options);

            case "pyproject.toml":
                return RewritePyproject(content, options);

            default:
                return Failed("no editor for this file");
        }
    }

    internal static RewriteResult RewriteGradleWrapper(string content, MigrationOptions options)
    {
        var target = VersionNumber.TryParseOrNull(options.Target);
        if (target is null || (target.Major >= 9 && target.Parts.Count < 3))
            return Failed("Gradle 9 and later require a full major.minor.patch wrapper version");
        foreach (var line in content.Split('\n'))
        {
            var property = line.TrimStart();
            if (property.Length == 0 || property[0] is '#' or '!') continue;
            var separator = property.IndexOfAny(['=', ':', ' ', '\t', '\r']);
            var key = separator < 0 ? property : property[..separator];
            if (key.Contains('\\', StringComparison.Ordinal))
                return Failed("escaped or continued property keys require manual handling");
        }
        if (GradleChecksum.IsMatch(content))
            return Failed("distribution checksum must be updated for the new version; handle the wrapper manually");
        var matches = GradleUrl.Matches(content);
        if (matches.Count != 1)
            return Failed("expected one official Gradle distribution URL");
        var match = matches[0];
        var old = match.Groups[2].Value;
        if (string.Equals(old, options.Target, StringComparison.Ordinal)) return Unchanged(content);
        var version = match.Groups[2];
        var updated = content[..version.Index] + options.Target + content[(version.Index + version.Length)..];
        return Changed(updated, $"Gradle wrapper {old} → {options.Target} (wrapper scripts and JAR are unchanged)");
    }

    /// <summary>Rewrites the first meaningful line of a single-version pin file.</summary>
    internal static RewriteResult RewriteFirstLine(string content, string target, string file)
    {
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(newline);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;

            var looksLikeVersion = VersionNumber.TryParse(trimmed, out _) ||
                                   trimmed.StartsWith("lts", StringComparison.OrdinalIgnoreCase) ||
                                   trimmed is "node" or "stable" or "system";

            if (!looksLikeVersion)
                return Failed($"first line is not a version ('{Truncate(trimmed, 24)}')");

            if (AlreadyOnTarget(trimmed, target)) return Unchanged(content);

            var old = trimmed;
            lines[i] = target;
            return Changed(string.Join(newline, lines), $"{old} → {target}");
        }

        return Failed("file has no version line");
    }

    /// <summary>Rewrites the version of one tool inside an asdf <c>.tool-versions</c> file.</summary>
    internal static RewriteResult RewriteToolVersions(string content, MigrationOptions options)
    {
        var names = AsdfNames(options.Tool);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(newline);
        var changed = false;
        var before = "";
        var after = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (!names.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;

            var old = parts[1];
            if (AlreadyOnTarget(old, options.Target)) continue;

            // Replace only the version token, keeping indentation and any extra versions.
            var index = lines[i].IndexOf(old, StringComparison.Ordinal);
            if (index < 0) continue;

            lines[i] = lines[i][..index] + options.Target + lines[i][(index + old.Length)..];
            changed = true;
            before = old;
            after = options.Target;
        }

        if (!changed) return Unchanged(content);
        return Changed(string.Join(newline, lines), $"{names[0]} {before} → {after}");
    }

    /// <summary>Rewrites <c>[tools] node = "…"</c> in a mise configuration file.</summary>
    internal static RewriteResult RewriteMise(string content, MigrationOptions options)
    {
        var keys = AsdfNames(options.Tool).Concat([ToolKinds.Display(options.Tool)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(newline);
        var table = "";
        var changed = false;
        var before = "";
        var key = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith('['))
            {
                table = trimmed.Trim('[', ']').Trim();
                continue;
            }

            if (table != "tools" && !trimmed.StartsWith("tools.", StringComparison.Ordinal)) continue;
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;

            var name = trimmed[..equals].Trim().Trim('"');
            if (!keys.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            var value = trimmed[(equals + 1)..].Trim();
            var old = ParserText.Unquote(value);
            if (AlreadyOnTarget(old, options.Target)) continue;

            var start = lines[i].IndexOf(value, StringComparison.Ordinal);
            if (start < 0) continue;

            lines[i] = lines[i][..start] + "\"" + options.Target + "\"" + lines[i][(start + value.Length)..];
            changed = true;
            before = old;
            key = name;
        }

        if (!changed) return Unchanged(content);
        return Changed(string.Join(newline, lines), $"{key} {before} → {options.Target}");
    }

    /// <summary>Rewrites <c>java=…</c> in <c>.sdkmanrc</c>. Refuses a target without a vendor.</summary>
    internal static RewriteResult RewriteSdkmanrc(string content, MigrationOptions options)
    {
        var key = options.Tool switch
        {
            ToolKind.Jdk => "java",
            ToolKind.Maven => "maven",
            ToolKind.Gradle => "gradle",
            _ => null
        };

        if (key is null) return Failed("not a tool SDKMAN manages");

        // SDKMAN identifiers are distribution-qualified ("21.0.2-tem"); a bare major is not
        // installable, so writing one would produce a file that cannot be acted on.
        if (!options.Target.Contains('-'))
            return Failed("SDKMAN needs a distribution id such as 21.0.2-tem");

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(newline);

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;

            var old = ParserText.Unquote(trimmed[(key.Length + 1)..].Trim());
            if (AlreadyOnTarget(old, options.Target)) return Unchanged(content);

            var start = lines[i].IndexOf(old, StringComparison.Ordinal);
            if (start < 0) continue;

            lines[i] = lines[i][..start] + options.Target + lines[i][(start + old.Length)..];
            return Changed(string.Join(newline, lines), $"{key} {old} → {options.Target}");
        }

        return Failed($"no {key}= entry");
    }

    /// <summary>Rewrites <c>volta.node</c> always, and <c>engines.node</c> only when asked.</summary>
    internal static RewriteResult RewritePackageJson(string content, MigrationOptions options)
    {
        if (options.Tool != ToolKind.Node) return Failed("only node pins live in package.json");

        var current = content;
        var changes = new List<string>();

        if (JsonText.TryGetString(current, ["volta", "node"]) is { } volta && !AlreadyOnTarget(volta, options.Target))
        {
            var updated = JsonText.ReplaceString(current, ["volta", "node"], options.Target);
            if (updated is null) return Failed("could not rewrite volta.node");
            current = updated;
            changes.Add($"volta.node {volta} → {options.Target}");
        }

        if (options.IncludeRanges && JsonText.TryGetString(current, ["engines", "node"]) is { } engines)
        {
            var target = ">=" + options.Target;
            if (engines != target)
            {
                var updated = JsonText.ReplaceString(current, ["engines", "node"], target);
                if (updated is null) return Failed("could not rewrite engines.node");
                current = updated;
                changes.Add($"engines.node {engines} → {target}");
            }
        }
        else if (!options.IncludeRanges && JsonText.TryGetString(current, ["engines", "node"]) is { } untouched)
        {
            changes.Add($"engines.node {untouched} left alone (--include-ranges)");
        }

        if (current == content) return Unchanged(content);

        var description = string.Join("; ", changes.Where(c => !c.Contains("left alone", StringComparison.Ordinal)));
        return Changed(current, description.Length == 0 ? "package.json" : description);
    }

    internal static RewriteResult RewriteGlobalJson(string content, MigrationOptions options)
    {
        if (options.Tool != ToolKind.DotNetSdk) return Failed("only the .NET SDK is pinned in global.json");

        var current = JsonText.TryGetString(content, ["sdk", "version"]);
        if (current is null) return Failed("no sdk.version");
        if (AlreadyOnTarget(current, options.Target)) return Unchanged(content);

        var updated = JsonText.ReplaceString(content, ["sdk", "version"], options.Target);
        if (updated is null) return Failed("could not rewrite sdk.version");

        return Changed(updated, $"sdk.version {current} → {options.Target}");
    }

    internal static RewriteResult RewritePyproject(string content, MigrationOptions options)
    {
        if (options.Tool != ToolKind.Python) return Failed("not a Python file");

        if (!options.IncludeRanges)
        {
            return JsonText.ContainsKey(content, "requires-python")
                ? Failed("requires-python is a compatibility range (use --include-ranges)")
                : Failed("no requires-python");
        }

        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = content.Split(newline);
        var table = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                table = trimmed.Trim('[', ']').Trim();
                continue;
            }

            var inProject = table is "project" or "tool.poetry.dependencies" or "";
            if (!inProject) continue;

            var key = table == "tool.poetry.dependencies" ? "python" : "requires-python";
            if (!trimmed.StartsWith(key, StringComparison.Ordinal)) continue;

            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;

            var value = trimmed[(equals + 1)..].Trim();
            var old = ParserText.Unquote(value);
            var replacement = ">=" + options.Target;
            if (AlreadyOnTarget(old, replacement)) return Unchanged(content);

            var start = lines[i].IndexOf(value, StringComparison.Ordinal);
            if (start < 0) continue;

            lines[i] = lines[i][..start] + "\"" + replacement + "\"" + lines[i][(start + value.Length)..];
            return Changed(string.Join(newline, lines), $"{key} {old} → {replacement}");
        }

        return Failed("no python requirement");
    }

    private static string[] AsdfNames(ToolKind tool) => tool switch
    {
        ToolKind.Node => ["nodejs", "node"],
        ToolKind.Jdk => ["java"],
        ToolKind.Python => ["python"],
        ToolKind.Ruby => ["ruby"],
        ToolKind.Go => ["golang", "go"],
        ToolKind.Rustc => ["rust"],
        ToolKind.Php => ["php"],
        _ => [ToolKinds.Display(tool)]
    };

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..width] + "…";
}

/// <summary>Byte-precise JSON string replacement: reformats nothing it does not have to.</summary>
internal static class JsonText
{
    public static string? TryGetString(string json, IReadOnlyList<string> path)
    {
        if (!Validate(json)) return null;
        if (!TryLocate(json, path, out _, out var value)) return null;
        return value;
    }

    public static bool ContainsKey(string json, string key) =>
        json.Contains("\"" + key + "\"", StringComparison.Ordinal);

    public static string? ReplaceString(string json, IReadOnlyList<string> path, string newValue)
    {
        // A file we cannot parse in full is a file we refuse to edit: a half-parseable
        // manifest is usually mid-edit by the user or corrupted, and splicing bytes into it
        // would turn a recoverable problem into a silent one.
        if (!Validate(json)) return null;
        if (!TryLocate(json, path, out var span, out _)) return null;

        var bytes = Encoding.UTF8.GetBytes(json);
        var replacement = "\"" + Escape(newValue) + "\"";

        var head = Encoding.UTF8.GetString(bytes, 0, span.Start);
        var tail = Encoding.UTF8.GetString(bytes, span.End + 1, bytes.Length - span.End - 1);
        return head + replacement + tail;
    }

    private readonly record struct Span(int Start, int End);

    private static bool Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryLocate(string json, IReadOnlyList<string> path, out Span span, out string value)
    {
        span = default;
        value = "";

        var bytes = Encoding.UTF8.GetBytes(json);

        // Index 0 is unused: System.Text.Json reports the root object at depth 0 and the
        // names inside it at depth 1, so a value at depth D belongs to the path
        // names[1..D].
        var names = new List<string?> { null };

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var depth = reader.CurrentDepth;
                    while (names.Count <= depth) names.Add(null);
                    names[depth] = reader.GetString();
                    continue;
                }

                if (reader.TokenType != JsonTokenType.String) continue;

                var valueDepth = reader.CurrentDepth;
                if (valueDepth != path.Count) continue;

                var matches = true;
                for (var i = 0; i < path.Count; i++)
                {
                    if (!string.Equals(names[i + 1], path[i], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches) continue;

                var start = (int)reader.TokenStartIndex;
                var end = FindStringEnd(bytes, start);
                if (end < 0) return false;

                span = new Span(start, end);
                value = reader.GetString() ?? "";
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static int FindStringEnd(byte[] bytes, int start)
    {
        if (start >= bytes.Length || bytes[start] != (byte)'"') return -1;

        for (var i = start + 1; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\\')
            {
                i++;
                continue;
            }

            if (bytes[i] == (byte)'"') return i;
        }

        return -1;
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => character.ToString()
            });
        }

        return builder.ToString();
    }
}

public static class MigrationFile
{
    /// <summary>Reads UTF-8, recording whether the file carried a BOM so it can be preserved.</summary>
    public static string? Read(string path, out bool hadBom)
    {
        hadBom = false;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) return null;
            hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return new UTF8Encoding(false, true).GetString(bytes, hadBom ? 3 : 0, bytes.Length - (hadBom ? 3 : 0));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Writes an edit with three guarantees: it stays inside the project, the file has not
/// changed since the plan was built, and a byte-exact backup exists before anything is lost.
/// </summary>
public static class MigrationExecutor
{
    public static (bool Ok, string Message) Apply(FileEdit edit, string backupRoot, string planId)
    {
        try
        {
            var full = Path.GetFullPath(edit.FullPath);
            var root = Path.GetFullPath(edit.ProjectPath)
                .TrimEnd(Path.DirectorySeparatorChar, '/');

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return (false, "path escapes the project root");

            if (ArtifactCleanup.HasUnsafePath(full)) return (false, "path contains a symbolic link or is unavailable");

            var current = MigrationFile.Read(full, out var hadBom);
            if (current is null) return (false, "file unreadable");
            if (current != edit.Before) return (false, "file changed since the plan was built");

            var backupDirectory = Path.Combine(backupRoot, planId);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, BackupName(edit));
            File.Copy(full, backupPath, overwrite: false);
            var backup = MigrationFile.Read(backupPath, out var backupBom);
            if (backup != edit.Before || backupBom != hadBom || ArtifactCleanup.HasUnsafePath(full) || MigrationFile.Read(full, out _) != edit.Before)
                return (false, "file changed while the backup was created");

            File.WriteAllText(full, edit.After, new UTF8Encoding(hadBom));
            return (true, "ok");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string BackupName(FileEdit edit)
    {
        var safe = edit.RelativePath.Replace('/', '_').Replace('\\', '_');
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(edit.ProjectPath)))[..12].ToLowerInvariant();

        return $"{hash}-{safe}";
    }
}


