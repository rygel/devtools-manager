using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Scan.Parsers;

namespace DevTools.Scan.Parsers;

public sealed partial class RubyParser : IProjectParser
{
    public string Id => "ruby";

    public Ecosystem Ecosystem => Ecosystem.Ruby;

    [GeneratedRegex(@"^\s*ruby\s+['""]([^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex GemfileRuby();

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Ruby);

        if (context.Fs.FileExists(".ruby-version"))
        {
            var first = ParserText.FirstMeaningfulLine(context.Read(".ruby-version"));
            if (first is not null)
            {
                // rbenv files sometimes carry a "ruby-" prefix.
                var value = first.Value.Value.StartsWith("ruby-", StringComparison.OrdinalIgnoreCase)
                    ? first.Value.Value[5..]
                    : first.Value.Value;
                context.Add(Requirement.Pinned(ToolKind.Ruby, value,
                    Provenance.FileOf(".ruby-version", first.Value.Line)));
            }
        }

        if (!context.Fs.FileExists("Gemfile")) return;

        var text = context.Read("Gemfile");
        if (text is null) return;

        var match = GemfileRuby().Match(text);
        if (match.Success)
        {
            context.Add(Requirement.Declared(ToolKind.Ruby, match.Groups[1].Value,
                Provenance.FileOf("Gemfile", ParserText.LineOf(text, match.Index), "ruby")));
        }
    }
}

public sealed class PhpParser : IProjectParser
{
    public string Id => "php";

    public Ecosystem Ecosystem => Ecosystem.Php;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Php);

        var text = context.Read("composer.json");
        if (text is null) return;

        using var doc = JsonHelper.TryParse(text);
        if (doc is null)
        {
            context.Warn("invalid-manifest", "composer.json could not be parsed", "composer.json");
            return;
        }

        context.Name ??= JsonHelper.GetText(doc.RootElement, "name");

        if (JsonHelper.GetText(doc.RootElement, "require", "php") is { } php)
        {
            context.Add(Requirement.Declared(ToolKind.Php, php,
                Provenance.FileOf("composer.json", 1, "require.php")));
        }
    }
}

public sealed class DartParser : IProjectParser
{
    public string Id => "dart";

    public Ecosystem Ecosystem => Ecosystem.Dart;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Dart);

        var text = context.Read("pubspec.yaml");
        if (text is null) return;

        foreach (var (line, number) in ParserText.Lines(text))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("sdk:", StringComparison.Ordinal)) continue;

            var value = ParserText.Unquote(trimmed[4..].Trim());
            if (value.Length == 0) continue;

            context.Add(Requirement.Declared(ToolKind.Dart, value,
                Provenance.FileOf("pubspec.yaml", number, "environment.sdk")));
            return;
        }

        var nameLine = ParserText.Assignment(text, "name");
        if (nameLine is not null) context.Name ??= nameLine.Value.Value;
    }
}

/// <summary>
/// Deno projects. Deno has no version field in <c>deno.json</c>; the runtime is pinned with
/// <c>.dvmrc</c> (dvm), asdf/mise entries, or not at all.
/// </summary>
public sealed class DenoParser : IProjectParser
{
    public string Id => "deno";

    public Ecosystem Ecosystem => Ecosystem.Deno;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Deno);

        if (context.Fs.FileExists("deno.lock")) context.Lockfiles.Add("deno.lock");

        var first = ParserText.FirstMeaningfulLine(context.Fs.FileExists(".dvmrc") ? context.Read(".dvmrc") : null);
        if (first is not null)
        {
            context.Add(Requirement.Pinned(ToolKind.Deno, first.Value.Value,
                Provenance.FileOf(".dvmrc", first.Value.Line)));
        }

        if (context.Fs.FileExists("deno.json") || context.Fs.FileExists("deno.jsonc"))
        {
            var file = context.Fs.FileExists("deno.json") ? "deno.json" : "deno.jsonc";
            using var document = JsonHelper.TryParse(context.Read(file));

            if (document is null)
            {
                context.Warn("invalid-manifest", file + " could not be parsed", file);
                return;
            }

            // deno.json has no project name field, but the "name" key is used by JSR packages.
            context.Name ??= ParserText.SanitizeName(JsonHelper.GetText(document.RootElement, "name"));
        }
    }
}

/// <summary>
/// asdf / mise / Volta multi-tool pin files. These are the highest-authority declarations
/// because the user explicitly wrote them for a version manager.
/// </summary>
public sealed class VersionPinParser : IProjectParser
{
    private static readonly Dictionary<string, ToolKind> ToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nodejs"] = ToolKind.Node,
        ["node"] = ToolKind.Node,
        ["pnpm"] = ToolKind.Pnpm,
        ["yarn"] = ToolKind.Yarn,
        ["bun"] = ToolKind.Bun,
        ["deno"] = ToolKind.Deno,
        ["java"] = ToolKind.Jdk,
        ["maven"] = ToolKind.Maven,
        ["gradle"] = ToolKind.Gradle,
        ["python"] = ToolKind.Python,
        ["uv"] = ToolKind.Uv,
        ["golang"] = ToolKind.Go,
        ["go"] = ToolKind.Go,
        ["rust"] = ToolKind.Rustc,
        ["ruby"] = ToolKind.Ruby,
        ["php"] = ToolKind.Php
    };

    public string Id => "pins";

    public Ecosystem Ecosystem => Ecosystem.Node;

    public void Extract(ProjectContext context)
    {
        if (context.Fs.FileExists(".tool-versions")) ParseToolVersions(context);
        if (context.Fs.FileExists("mise.toml")) ParseMise(context, "mise.toml");
        if (context.Fs.FileExists(".mise.toml")) ParseMise(context, ".mise.toml");
    }

    private static void ParseToolVersions(ProjectContext context)
    {
        var text = context.Read(".tool-versions");
        if (text is null) return;

        foreach (var (line, number) in ParserText.Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            if (!ToolNames.TryGetValue(parts[0], out var tool)) continue;

            // "nodejs 20.11.0 22.0.0" — only the first listed version is active.
            context.Add(Requirement.Pinned(tool, parts[1], Provenance.FileOf(".tool-versions", number, parts[0])));
        }
    }

    private static void ParseMise(ProjectContext context, string file)
    {
        var text = context.Read(file);
        if (text is null) return;

        var toml = TomlLite.Parse(text);
        foreach (var (key, value) in toml.All)
        {
            if (!key.StartsWith("tools.", StringComparison.Ordinal)) continue;

            var toolName = key["tools.".Length..];
            if (!ToolNames.TryGetValue(toolName, out var tool)) continue;

            context.Add(Requirement.Pinned(tool, value.Value, Provenance.FileOf(file, value.Line, key)));
        }
    }
}

/// <summary>Tags an ecosystem without extracting a version requirement (C++, Nix, Elixir).</summary>
public sealed class EcosystemOnlyParser : IProjectParser
{
    private readonly Ecosystem _ecosystem;

    public EcosystemOnlyParser(Ecosystem ecosystem, string id)
    {
        _ecosystem = ecosystem;
        Id = id;
    }

    public string Id { get; }

    public Ecosystem Ecosystem => _ecosystem;

    public void Extract(ProjectContext context) => context.Tag(_ecosystem);
}
