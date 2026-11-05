using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DevTools.Core.Model;

namespace DevTools.Scan;

/// <summary>
/// Finds the application frameworks a project declares by reading the dependency sections of
/// its manifests. Only declared dependencies count: a word in a description or a keyword list
/// never does. Versions are reported exactly as written, never resolved or guessed.
/// </summary>
public static class FrameworkDetector
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private sealed record Hit(string Id, string? Version, string File, int? Line, string Key);

    private static Regex Rx(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Dictionary<string, string> NodeMap = new(StringComparer.Ordinal)
    {
        ["react"] = "react", ["next"] = "next", ["vue"] = "vue", ["nuxt"] = "nuxt", ["@angular/core"] = "angular",
        ["svelte"] = "svelte", ["@sveltejs/kit"] = "sveltekit", ["solid-js"] = "solid", ["astro"] = "astro",
        ["@remix-run/react"] = "remix", ["express"] = "express", ["fastify"] = "fastify", ["@nestjs/core"] = "nestjs",
        ["electron"] = "electron", ["react-native"] = "react-native", ["expo"] = "expo"
    };

    private static readonly Dictionary<string, string> ComposerMap = new(StringComparer.Ordinal)
    {
        ["laravel/framework"] = "laravel", ["symfony/framework-bundle"] = "symfony", ["slim/slim"] = "slim"
    };

    private static readonly Dictionary<string, string> PythonMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["django"] = "django", ["flask"] = "flask", ["fastapi"] = "fastapi"
    };

    private static readonly Dictionary<string, string> RustMap = new(StringComparer.Ordinal)
    {
        ["axum"] = "axum", ["actix-web"] = "actix-web", ["rocket"] = "rocket", ["tauri"] = "tauri"
    };

    public static IReadOnlyList<FrameworkUse> Detect(ProjectContext context)
    {
        var hits = new List<Hit>();
        foreach (var reader in new Action<ProjectContext, List<Hit>>[]
                 { ReadPackageJson, ReadComposer, ReadMaven, ReadGradle, ReadDotNet, ReadPython, ReadGemfile, ReadGoMod, ReadCargo, ReadMix, ReadPubspec })
        {
            try { reader(context, hits); }
            catch (Exception ex) when (ex is XmlException or JsonException or RegexMatchTimeoutException or InvalidOperationException)
            { context.Warn("framework-scan-failed", "Framework detection stopped for one manifest: " + ex.Message); }
        }

        var found = new List<FrameworkUse>();
        foreach (var group in hits.GroupBy(h => h.Id))
        {
            var best = group.FirstOrDefault(h => h.Version is not null) ?? group.First();
            var info = FrameworkCatalog.Get(group.Key);
            found.Add(new FrameworkUse
            {
                Id = info.Id,
                Name = info.Name,
                Ecosystem = info.Ecosystem,
                Version = best.Version,
                Provenance = Provenance.FileOf(best.File, best.Line, best.Key)
            });
        }

        // The Spring Framework is part of every Spring Boot application; listing both is noise.
        if (found.Any(f => f.Id == "spring-boot")) found.RemoveAll(f => f.Id == "spring-framework");
        return found;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static int? FindJsonKeyLine(string text, string key)
    {
        var match = Rx("\"" + Regex.Escape(key) + "\"\\s*:").Match(text);
        return match.Success ? LineOf(text, match.Index) : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ───────────────────────── Node, PHP ─────────────────────────

    private static void ReadPackageJson(ProjectContext context, List<Hit> hits) =>
        ReadJsonDependencies(context, hits, "package.json", ["dependencies", "devDependencies"], NodeMap);

    private static void ReadComposer(ProjectContext context, List<Hit> hits) =>
        ReadJsonDependencies(context, hits, "composer.json", ["require", "require-dev"], ComposerMap);

    private static void ReadJsonDependencies(ProjectContext context, List<Hit> hits, string file,
        string[] sections, Dictionary<string, string> map)
    {
        if (!context.Fs.FileExists(file)) return;
        var text = context.Read(file);
        if (text is null) return;
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var section in sections)
        {
            if (!document.RootElement.TryGetProperty(section, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object) continue;
            foreach (var dependency in dependencies.EnumerateObject())
            {
                if (!map.TryGetValue(dependency.Name, out var id)) continue;
                var version = dependency.Value.ValueKind == JsonValueKind.String ? NullIfBlank(dependency.Value.GetString()) : null;
                hits.Add(new Hit(id, version, file, FindJsonKeyLine(text, dependency.Name), dependency.Name));
            }
        }
    }

    // ───────────────────────── Java ─────────────────────────

    private static string? MapMaven(string group, string artifact)
    {
        if (group == "org.springframework.boot") return "spring-boot";
        if (group == "org.springframework" && artifact is "spring-context" or "spring-web" or "spring-webmvc" or "spring-webflux")
            return "spring-framework";
        if (group.StartsWith("io.quarkus", StringComparison.Ordinal)) return "quarkus";
        if (group.StartsWith("io.micronaut", StringComparison.Ordinal)) return "micronaut";
        if (group == "io.javalin" && artifact.StartsWith("javalin", StringComparison.Ordinal)) return "javalin";
        if (group == "com.vaadin") return "vaadin";
        if (group == "io.dropwizard") return "dropwizard";
        if (group.StartsWith("io.helidon", StringComparison.Ordinal)) return "helidon";
        return null;
    }

    private static string? MapGradlePlugin(string plugin)
    {
        if (plugin == "org.springframework.boot") return "spring-boot";
        if (plugin.StartsWith("io.quarkus", StringComparison.Ordinal)) return "quarkus";
        if (plugin.StartsWith("io.micronaut", StringComparison.Ordinal)) return "micronaut";
        if (plugin.StartsWith("com.vaadin", StringComparison.Ordinal)) return "vaadin";
        return null;
    }

    private static string ChildText(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    private static void ReadMaven(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("pom.xml")) return;
        var text = context.Read("pom.xml");
        if (text is null) return;
        var document = XDocument.Parse(text, LoadOptions.SetLineInfo);
        var root = document.Root;
        if (root is null) return;

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var propertyElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "properties");
        if (propertyElement is not null)
            foreach (var property in propertyElement.Elements()) properties[property.Name.LocalName] = property.Value.Trim();
        var parent = root.Elements().FirstOrDefault(e => e.Name.LocalName == "parent");
        if (parent is not null)
        {
            var parentVersion = ChildText(parent, "version");
            if (parentVersion.Length > 0) { properties["project.parent.version"] = parentVersion; properties["parent.version"] = parentVersion; }
        }
        var ownVersion = ChildText(root, "version");
        if (ownVersion.Length > 0) properties["project.version"] = ownVersion;

        string? Resolve(string raw)
        {
            if (raw.Length == 0) return null;
            var unresolved = false;
            var value = Rx(@"\$\{([^}]+)\}").Replace(raw, m =>
            {
                if (properties.TryGetValue(m.Groups[1].Value, out var replacement)) return replacement;
                unresolved = true;
                return m.Value;
            });
            return unresolved ? null : NullIfBlank(value);
        }

        void Consider(XElement element)
        {
            var id = MapMaven(ChildText(element, "groupId"), ChildText(element, "artifactId"));
            if (id is null) return;
            var line = element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : (int?)null;
            hits.Add(new Hit(id, Resolve(ChildText(element, "version")), "pom.xml", line,
                ChildText(element, "groupId") + ":" + ChildText(element, "artifactId")));
        }

        if (parent is not null) Consider(parent);
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName is "dependency" or "plugin")) Consider(element);
    }

    private static void ReadGradle(ProjectContext context, List<Hit> hits)
    {
        foreach (var file in new[] { "build.gradle.kts", "build.gradle" })
        {
            if (!context.Fs.FileExists(file)) continue;
            var text = context.Read(file);
            if (text is null) continue;
            var plugin = Rx(@"\bid\s*\(?\s*[""']([\w.\-]+)[""']\s*\)?(?:\s+version\s+[""']([^""']+)[""'])?");
            var coordinate = Rx(@"[""']([\w.\-]+):([\w.\-]+)(?::([^""':\s$]+))?[""']");
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var pluginMatch = plugin.Match(line);
                if (pluginMatch.Success && MapGradlePlugin(pluginMatch.Groups[1].Value) is { } pluginId)
                    hits.Add(new Hit(pluginId, NullIfBlank(pluginMatch.Groups[2].Value), file, i + 1, pluginMatch.Groups[1].Value));
                foreach (Match match in coordinate.Matches(line))
                {
                    if (MapMaven(match.Groups[1].Value, match.Groups[2].Value) is { } id)
                        hits.Add(new Hit(id, NullIfBlank(match.Groups[3].Value), file, i + 1, match.Groups[1].Value + ":" + match.Groups[2].Value));
                }
            }
        }
    }

    // ───────────────────────── .NET ─────────────────────────

    private static void ReadDotNet(ProjectContext context, List<Hit> hits)
    {
        foreach (var file in context.Fs.ListFiles("", "*.csproj"))
        {
            var text = context.Read(file);
            if (text is null) continue;
            var root = XDocument.Parse(text, LoadOptions.SetLineInfo).Root;
            if (root is null) continue;
            var framework = root.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFramework")?.Value.Trim()
                ?? root.Descendants().FirstOrDefault(e => e.Name.LocalName is "TargetFrameworks")?.Value.Split(';')[0].Trim();
            var versionMatch = framework is null ? null : Rx(@"^net(\d+\.\d+)(?:-|$)").Match(framework);
            var version = versionMatch is { Success: true } ? versionMatch.Groups[1].Value : null;
            var sdk = root.Attribute("Sdk")?.Value ?? "";
            var sdkLine = root is IXmlLineInfo rootInfo && rootInfo.HasLineInfo() ? rootInfo.LineNumber : (int?)null;
            if (sdk == "Microsoft.NET.Sdk.Web") hits.Add(new Hit("aspnet-core", version, file, sdkLine, "Sdk"));
            if (sdk == "Microsoft.NET.Sdk.BlazorWebAssembly") hits.Add(new Hit("blazor", version, file, sdkLine, "Sdk"));
            if (root.Descendants().Any(e => e.Name.LocalName == "UseMaui" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
                hits.Add(new Hit("maui", version, file, sdkLine, "UseMaui"));
            foreach (var reference in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                if (reference.Attribute("Include")?.Value != "Avalonia") continue;
                var packageVersion = reference.Attribute("Version")?.Value ?? ChildText(reference, "Version");
                var line = reference is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : (int?)null;
                hits.Add(new Hit("avalonia", NullIfBlank(packageVersion), file, line, "Avalonia"));
            }
        }
    }

    // ───────────────────────── Python ─────────────────────────

    private static void ReadPython(ProjectContext context, List<Hit> hits)
    {
        var names = string.Join("|", PythonMap.Keys.Select(Regex.Escape));
        var requirement = Rx(@"^\s*(" + names + @")(?![\w\-.])\s*(?:\[[^\]]*\])?\s*([<>=!~][^\s;#]*)?", RegexOptions.IgnoreCase);
        if (context.Fs.FileExists("requirements.txt") && context.Read("requirements.txt") is { } requirements)
        {
            var lines = requirements.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var match = requirement.Match(lines[i]);
                if (match.Success) hits.Add(new Hit(PythonMap[match.Groups[1].Value], PythonVersion(match.Groups[2].Value), "requirements.txt", i + 1, match.Groups[1].Value));
            }
        }

        var quoted = Rx(@"[""']\s*(" + names + @")(?![\w\-.])\s*(?:\[[^\]]*\])?\s*([<>=!~][^""';,\s]*)?", RegexOptions.IgnoreCase);
        var keyed = Rx(@"^\s*(" + names + @")\s*=\s*(?:""([^""]*)""|'([^']*)'|\{[^}]*version\s*=\s*""([^""]*)"")", RegexOptions.IgnoreCase);
        foreach (var file in new[] { "pyproject.toml", "Pipfile" })
        {
            if (!context.Fs.FileExists(file) || context.Read(file) is not { } text) continue;
            var section = "";
            var inArray = false;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && !inArray) { section = trimmed.Trim('[', ']').Trim(); continue; }
                if (!inArray && Rx(@"^(?:dependencies|optional-dependencies|dev-dependencies)\s*=\s*\[|^\w[\w\-]*\s*=\s*\[").IsMatch(trimmed)
                    && (section is "project" or "dependency-groups" || section.StartsWith("project.optional-dependencies", StringComparison.Ordinal)))
                    inArray = true;
                if (inArray)
                {
                    foreach (Match match in quoted.Matches(line))
                        hits.Add(new Hit(PythonMap[match.Groups[1].Value], PythonVersion(match.Groups[2].Value), file, i + 1, match.Groups[1].Value));
                    if (line.Contains(']')) inArray = false;
                    continue;
                }
                if (!section.Contains("dependencies", StringComparison.Ordinal) && !section.Contains("packages", StringComparison.Ordinal)) continue;
                var keyedMatch = keyed.Match(line);
                if (!keyedMatch.Success) continue;
                var raw = keyedMatch.Groups[2].Success ? keyedMatch.Groups[2].Value : keyedMatch.Groups[3].Success ? keyedMatch.Groups[3].Value : keyedMatch.Groups[4].Value;
                hits.Add(new Hit(PythonMap[keyedMatch.Groups[1].Value], PythonVersion(raw), file, i + 1, keyedMatch.Groups[1].Value));
            }
        }
    }

    private static string? PythonVersion(string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("==", StringComparison.Ordinal)) value = value[2..];
        return value is "" or "*" ? null : value;
    }

    // ───────────────────────── Ruby, Go, Rust, Elixir, Dart ─────────────────────────

    private static void ReadGemfile(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("Gemfile") || context.Read("Gemfile") is not { } text) return;
        var gem = Rx(@"^\s*gem\s+[""'](rails|sinatra)[""'](?:\s*,\s*[""']([^""']+)[""'])?");
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = gem.Match(lines[i]);
            if (match.Success) hits.Add(new Hit(match.Groups[1].Value, NullIfBlank(match.Groups[2].Value), "Gemfile", i + 1, match.Groups[1].Value));
        }
    }

    private static void ReadGoMod(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("go.mod") || context.Read("go.mod") is not { } text) return;
        var require = Rx(@"^\s*(?:require\s+)?github\.com/(gin-gonic/gin|labstack/echo|gofiber/fiber)(?:/v\d+)?\s+(v\S+)");
        var ids = new Dictionary<string, string> { ["gin-gonic/gin"] = "gin", ["labstack/echo"] = "echo", ["gofiber/fiber"] = "fiber" };
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = require.Match(lines[i]);
            if (match.Success) hits.Add(new Hit(ids[match.Groups[1].Value], match.Groups[2].Value, "go.mod", i + 1, match.Groups[1].Value));
        }
    }

    private static void ReadCargo(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("Cargo.toml") || context.Read("Cargo.toml") is not { } text) return;
        var names = string.Join("|", RustMap.Keys.Select(Regex.Escape));
        var inline = Rx(@"^\s*(" + names + @")\s*=\s*(?:""([^""]+)""|\{[^}]*?version\s*=\s*""([^""]+)"")?");
        var tableHeader = Rx(@"^(?:[\w\-.]+\.)?(?:dev-|build-)?dependencies\.(" + names + @")$");
        var section = "";
        string? tableName = null;
        int tableIndex = -1;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                section = trimmed.Trim('[', ']').Trim();
                tableName = null;
                var header = tableHeader.Match(section);
                if (header.Success)
                {
                    tableName = header.Groups[1].Value;
                    tableIndex = hits.Count;
                    hits.Add(new Hit(tableName, null, "Cargo.toml", i + 1, tableName));
                }
                continue;
            }
            if (tableName is not null)
            {
                var version = Rx(@"^version\s*=\s*""([^""]+)""").Match(trimmed);
                if (version.Success) hits[tableIndex] = hits[tableIndex] with { Version = version.Groups[1].Value };
                continue;
            }
            if (!section.EndsWith("dependencies", StringComparison.Ordinal)) continue;
            var match = inline.Match(lines[i]);
            if (!match.Success) continue;
            var declared = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            hits.Add(new Hit(match.Groups[1].Value, NullIfBlank(declared), "Cargo.toml", i + 1, match.Groups[1].Value));
        }
    }

    private static void ReadMix(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("mix.exs") || context.Read("mix.exs") is not { } text) return;
        var phoenix = Rx(@"\{:phoenix\b(?:\s*,\s*""([^""]+)"")?");
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = phoenix.Match(lines[i]);
            if (match.Success) hits.Add(new Hit("phoenix", NullIfBlank(match.Groups[1].Value), "mix.exs", i + 1, "phoenix"));
        }
    }

    private static void ReadPubspec(ProjectContext context, List<Hit> hits)
    {
        if (!context.Fs.FileExists("pubspec.yaml") || context.Read("pubspec.yaml") is not { } text) return;
        var match = Rx(@"^[ \t]+flutter:[ \t]*\r?\n[ \t]+sdk:[ \t]*flutter\b", RegexOptions.Multiline).Match(text);
        if (match.Success) hits.Add(new Hit("flutter", null, "pubspec.yaml", LineOf(text, match.Index), "flutter"));
    }
}
