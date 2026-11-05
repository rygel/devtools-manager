using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DevTools.Core.Model;
using DevTools.Core.Versioning;
using DevTools.Scan.Parsers;

namespace DevTools.Scan.Parsers;

/// <summary>
/// Java toolchain extraction across the four places a JDK requirement actually hides:
/// the Maven POM, the Gradle build script, the wrapper properties, and a version-manager
/// pin file. Gradle and Maven themselves are extracted too, because a wrapper pins them
/// just as precisely as a JDK.
/// </summary>
public sealed partial class JavaParser : IProjectParser
{
    public string Id => "java";

    public Ecosystem Ecosystem => Ecosystem.Java;

    [GeneratedRegex(@"languageVersion\s*=\s*JavaLanguageVersion\.of\(\s*(\d+)\s*\)")]
    private static partial Regex GradleLanguageVersion();

    [GeneratedRegex(@"JavaLanguageVersion\.of\(\s*(\d+)\s*\)")]
    private static partial Regex GradleLanguageVersionShort();

    [GeneratedRegex(@"sourceCompatibility\s*[=:]\s*(?:JavaVersion\.VERSION_)?['""]?([0-9][0-9._]*)['""]?")]
    private static partial Regex GradleSourceCompatibility();

    [GeneratedRegex(@"jvmToolchain\(\s*(\d+)\s*\)")]
    private static partial Regex GradleJvmToolchain();

    [GeneratedRegex(@"gradle-([0-9][0-9.]*)-(?:bin|all)\.zip")]
    private static partial Regex GradleDistribution();

    [GeneratedRegex(@"apache-maven-([0-9][0-9.]*)-(?:bin|src)\.zip")]
    private static partial Regex MavenDistribution();

    [GeneratedRegex(@"^FROM\s+(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DockerFrom();

    [GeneratedRegex(@"kotlin\(\s*[""'](?:jvm|android|multiplatform)[""']\s*\)\s*version\s*[""']([^""']+)[""']")]
    private static partial Regex GradleKotlinPlugin();

    [GeneratedRegex(@"id\s*\(?\s*[""']org\.jetbrains\.kotlin\.(?:jvm|android|multiplatform)[""']\s*\)?\s*version\s*\(?\s*[""']([^""']+)[""']")]
    private static partial Regex GradleKotlinPluginId();

    [GeneratedRegex(@"kotlin_version\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex GradleKotlinVariable();

    [GeneratedRegex(@"org\.scala-lang:scala3?-library(?:_[0-9.]+)?:([0-9][^""'\s,)]*)")]
    private static partial Regex GradleScalaDependency();

    [GeneratedRegex(@"(?m)^[ \t]*alias\s*\(\s*libs\.plugins\.([A-Za-z0-9_.-]+)\s*\)")]
    private static partial Regex GradlePluginAlias();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_.-]+)\s*=\s*\{(.*)\}\s*(?:#.*)?$")]
    private static partial Regex CatalogEntry();

    [GeneratedRegex(@"(?:^|,)\s*id\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex CatalogPluginId();

    [GeneratedRegex(@"(?:^|,)\s*version\.ref\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex CatalogVersionRef();

    [GeneratedRegex(@"(?:^|,)\s*version\s*=\s*['""]([^'""]+)['""]")]
    private static partial Regex CatalogVersion();

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Java);

        if (context.Fs.FileExists("pom.xml")) ParsePom(context);
        if (context.Fs.FileExists("build.gradle")) ParseGradle(context, "build.gradle");
        if (context.Fs.FileExists("build.gradle.kts")) ParseGradle(context, "build.gradle.kts");

        ReadSdkmanrc(context);
        ReadSingleLinePin(context, ".java-version", ToolKind.Jdk);

        if (context.Fs.FileExists("gradle/wrapper/gradle-wrapper.properties"))
            ParseWrapper(context, "gradle/wrapper/gradle-wrapper.properties", ToolKind.Gradle);

        if (context.Fs.FileExists(".mvn/wrapper/maven-wrapper.properties"))
            ParseWrapper(context, ".mvn/wrapper/maven-wrapper.properties", ToolKind.Maven);

        if (context.Fs.FileExists("Dockerfile"))
        {
            var docker = context.Read("Dockerfile");
            if (docker is not null)
            {
                foreach (Match match in DockerFrom().Matches(docker))
                {
                    var image = match.Groups[1].Value;
                    if (!image.Contains("jdk", StringComparison.OrdinalIgnoreCase) &&
                        !image.Contains("java", StringComparison.OrdinalIgnoreCase)) continue;

                    context.Diagnostics.Add(Diagnostic.Info("container-base",
                        $"base image {image} — containerised JDK, not managed locally"));
                }
            }
        }
    }

    private static void ReadSingleLinePin(ProjectContext context, string file, ToolKind tool)
    {
        if (!context.Fs.FileExists(file)) return;

        var first = ParserText.FirstMeaningfulLine(context.Read(file));
        if (first is null) return;

        context.Add(Requirement.Pinned(tool, first.Value.Value, Provenance.FileOf(file, first.Value.Line)));
    }

    private static void ReadSdkmanrc(ProjectContext context)
    {
        if (!context.Fs.FileExists(".sdkmanrc")) return;

        var text = context.Read(".sdkmanrc");
        if (text is null) return;

        foreach (var (line, number) in ParserText.Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith("//")) continue;

            var equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;

            var key = trimmed[..equals].Trim();
            var value = ParserText.Unquote(trimmed[(equals + 1)..].Trim());
            if (value.Length == 0) continue;

            var tool = key switch
            {
                "java" => ToolKind.Jdk,
                "maven" => ToolKind.Maven,
                "gradle" => ToolKind.Gradle,
                _ => (ToolKind?)null
            };

            if (tool is null) continue;
            context.Add(Requirement.Pinned(tool.Value, value, Provenance.FileOf(".sdkmanrc", number, key)));
        }
    }

    private static void ParseWrapper(ProjectContext context, string file, ToolKind tool)
    {
        var text = context.Read(file);
        if (text is null) return;

        var pin = ParserText.Assignment(text, "distributionUrl");
        if (pin is null) return;

        // Java properties escape the colon in URLs ("https\://").
        var url = pin.Value.Value.Replace("\\:", ":").Replace("\\", "");
        var match = tool == ToolKind.Gradle ? GradleDistribution().Match(url) : MavenDistribution().Match(url);

        if (!match.Success)
        {
            context.Warn("wrapper-unrecognised",
                $"could not read a version from {file}", file, pin.Value.Line);
            return;
        }

        context.Add(Requirement.Pinned(tool, match.Groups[1].Value,
            Provenance.FileOf(file, pin.Value.Line, "distributionUrl")));
    }

    private static void ParseGradle(ProjectContext context, string file)
    {
        var text = context.Read(file);
        if (text is null) return;

        ParseGradleJavaToolchain(context, file, text);
        ParseGradleKotlin(context, file, text);
        ParseGradleScala(context, file, text);
        ParseGradleCatalog(context, file, text);
    }

    private static void ParseGradleCatalog(ProjectContext context, string buildFile, string buildText)
    {
        var aliases = GradlePluginAlias().Matches(buildText).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (aliases.Count == 0) return;
        const string file = "gradle/libs.versions.toml";
        if (!context.Fs.FileExists(file))
        {
            context.Warn("catalog-unavailable", "The Gradle plugin catalog could not be found in this project.", buildFile);
            return;
        }
        var text = context.Read(file);
        if (text is null) return;
        var versions = TomlLite.Parse(text);
        var unresolved = new HashSet<string>(aliases, StringComparer.Ordinal);
        var inPlugins = false;
        foreach (var (line, number) in ParserText.Lines(text))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) { inPlugins = trimmed == "[plugins]"; continue; }
            if (!inPlugins) continue;
            var entry = CatalogEntry().Match(line);
            if (!entry.Success) continue;
            var alias = entry.Groups[1].Value.Replace('-', '.').Replace('_', '.');
            if (!aliases.Contains(alias)) continue;
            unresolved.Remove(alias);
            var body = entry.Groups[2].Value;
            var id = CatalogPluginId().Match(body);
            if (!id.Success)
            {
                context.Warn("catalog-alias-unresolved", $"Could not read plugin id for alias {alias}.", file, number);
                continue;
            }
            if (!id.Groups[1].Value.StartsWith("org.jetbrains.kotlin.", StringComparison.Ordinal)) continue;
            var reference = CatalogVersionRef().Match(body);
            var literal = CatalogVersion().Match(body);
            var version = reference.Success ? versions.Find("versions." + reference.Groups[1].Value) :
                literal.Success ? (literal.Groups[1].Value, number) : ((string Value, int Line)?)null;
            if (version is null)
            {
                context.Warn("catalog-version-unresolved", $"Could not resolve version for Kotlin plugin alias {alias}.", file, number);
                continue;
            }
            context.Add(Requirement.Declared(ToolKind.Kotlin, version.Value.Value,
                Provenance.FileOf(file, version.Value.Line, "plugin " + alias)));
        }
        foreach (var alias in unresolved)
            context.Warn("catalog-alias-unresolved", $"Could not resolve Gradle plugin alias {alias}.", file);
    }

    /// <summary>
    /// Partial Gradle DSL reading: only version-bearing declarations, never the whole script.
    /// A Gradle build is a program; evaluating it is out of scope, but the plugin and
    /// dependency version strings are literal and safe to read.
    /// </summary>
    private static void ParseGradleKotlin(ProjectContext context, string file, string text)
    {
        Match match = GradleKotlinPlugin().Match(text);
        if (!match.Success) match = GradleKotlinPluginId().Match(text);
        if (!match.Success) match = GradleKotlinVariable().Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Declared(ToolKind.Kotlin, match.Groups[1].Value,
            Provenance.FileOf(file, ParserText.LineOf(text, match.Index), "kotlin plugin")));
    }

    private static void ParseGradleScala(ProjectContext context, string file, string text)
    {
        var match = GradleScalaDependency().Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Declared(ToolKind.Scala, match.Groups[1].Value,
            Provenance.FileOf(file, ParserText.LineOf(text, match.Index), "scala-library")));
    }

    private static void ParseGradleJavaToolchain(ProjectContext context, string file, string text)
    {
        (string Value, int Line)? found = null;

        var match = GradleLanguageVersion().Match(text);
        if (!match.Success) match = GradleLanguageVersionShort().Match(text);
        if (!match.Success) match = GradleJvmToolchain().Match(text);
        if (match.Success) found = (match.Groups[1].Value, ParserText.LineOf(text, match.Index));

        if (found is null)
        {
            var compat = GradleSourceCompatibility().Match(text);
            if (compat.Success) found = (compat.Groups[1].Value, ParserText.LineOf(text, compat.Index));
        }

        if (found is null) return;

        context.Add(Requirement.Declared(ToolKind.Jdk, found.Value.Value,
            Provenance.FileOf(file, found.Value.Line, "toolchain")));
    }

    private static void ParsePom(ProjectContext context)
    {
        var text = context.Read("pom.xml");
        if (text is null) return;

        XDocument document;
        try
        {
            using var reader = new StringReader(text);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception)
        {
            context.Warn("invalid-manifest", "pom.xml could not be parsed as XML", "pom.xml");
            return;
        }

        if (document.Root is null) return;

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var propertiesElement in document.Descendants().Where(e => e.Name.LocalName == "properties"))
        {
            foreach (var property in propertiesElement.Elements())
                properties[property.Name.LocalName] = property.Value.Trim();
        }

        var artifactId = document.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "artifactId");
        context.Name ??= ParserText.SanitizeName(artifactId?.Value);

        if (document.Root.Elements().Any(e => e.Name.LocalName == "modules")) context.IsWorkspaceRoot = true;

        foreach (var module in document.Descendants().Where(e => e.Name.LocalName == "module"))
        {
            var value = module.Value.Trim();
            if (value.Length > 0) context.WorkspaceMembers.Add(value);
        }

        // Kotlin is a JVM toolchain pinned in a Maven property; read it before the Java
        // version loop, which returns as soon as it finds a JDK property.
        if (properties.TryGetValue("kotlin.version", out var kotlinVersion))
        {
            var resolved = ParserText.ResolvePlaceholders(kotlinVersion, properties) ?? kotlinVersion;
            var element = document.Descendants()
                .Where(e => e.Name.LocalName == "properties")
                .SelectMany(e => e.Elements())
                .FirstOrDefault(e => e.Name.LocalName == "kotlin.version");

            context.Add(Requirement.Declared(ToolKind.Kotlin, resolved,
                Provenance.FileOf("pom.xml", (element as IXmlLineInfo)?.LineNumber, "kotlin.version")));
        }

        // Ordered by authority: release > source > java.version.
        string[] keys = ["maven.compiler.release", "maven.compiler.source", "java.version", "maven.compiler.target"];

        foreach (var key in keys)
        {
            var element = document.Descendants()
                .Where(e => e.Name.LocalName == "properties")
                .SelectMany(e => e.Elements())
                .FirstOrDefault(e => e.Name.LocalName == key);

            if (element is null) continue;

            var rawValue = ParserText.ResolvePlaceholders(element.Value.Trim(), properties);
            if (rawValue is null || !VersionNumber.TryParse(rawValue, out var version) || version is null)
            {
                context.Add(Requirement.Declared(ToolKind.Jdk, "unknown",
                    Provenance.FileOf("pom.xml", (element as IXmlLineInfo)?.LineNumber, key)));
                context.Warn("unresolved-property",
                    $"{key} could not be resolved to a version (value: {element.Value.Trim()})", "pom.xml");
                continue;
            }

            context.Add(Requirement.Declared(ToolKind.Jdk, JavaVersions.Normalize(version).ToString(),
                Provenance.FileOf("pom.xml", (element as IXmlLineInfo)?.LineNumber, key)));
            return;
        }
    }
}
