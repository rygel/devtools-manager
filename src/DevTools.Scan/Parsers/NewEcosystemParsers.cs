using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Scan.Parsers;

namespace DevTools.Scan.Parsers;

/// <summary>Elixir: <c>mix.exs</c> declares <c>elixir: "~&gt; 1.15"</c> in its project function.</summary>
public sealed class ElixirParser : IProjectParser
{
    private static readonly Regex ElixirRequirement = new(@"elixir:\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex AppName = new(@"app:\s*:([A-Za-z0-9_]+)", RegexOptions.Compiled);

    public string Id => "elixir";

    public Ecosystem Ecosystem => Ecosystem.Elixir;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Elixir);

        if (context.Fs.FileExists("mix.lock")) context.Lockfiles.Add("mix.lock");

        var text = context.Read("mix.exs");
        if (text is null) return;

        var app = AppName.Match(text);
        if (app.Success) context.Name ??= app.Groups[1].Value;

        var requirement = ElixirRequirement.Match(text);
        if (!requirement.Success) return;

        context.Add(Requirement.Declared(ToolKind.Elixir, requirement.Groups[1].Value,
            Provenance.FileOf("mix.exs", ParserText.LineOf(text, requirement.Index), "elixir")));
    }
}

/// <summary>
/// Scala. <c>build.sbt</c> is a Scala program, so only the <c>scalaVersion</c> assignment is
/// read — the rest of the build is never evaluated. <c>scala-cli</c> directives and Mill are
/// read the same conservative way.
/// </summary>
public sealed class ScalaParser : IProjectParser
{
    private static readonly Regex SbtVersion = new(@"scalaVersion\s*:?=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex SbtName = new(@"name\s*:=\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex SbtToolVersion = new(@"^\s*sbt\.version\s*=\s*(\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex UsingDirective = new(@"//>\s*using\s+scala\s+(\S+)", RegexOptions.Compiled);

    public string Id => "scala";

    public Ecosystem Ecosystem => Ecosystem.Scala;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Scala);

        if (context.Fs.FileExists("build.sbt"))
        {
            var text = context.Read("build.sbt");
            if (text is not null)
            {
                var name = SbtName.Match(text);
                if (name.Success) context.Name ??= ParserText.SanitizeName(name.Groups[1].Value);

                var version = SbtVersion.Match(text);
                if (version.Success)
                {
                    context.Add(Requirement.Declared(ToolKind.Scala, version.Groups[1].Value,
                        Provenance.FileOf("build.sbt", ParserText.LineOf(text, version.Index), "scalaVersion")));
                }
            }
        }

        if (context.Fs.FileExists("build.sc"))
        {
            var text = context.Read("build.sc");
            var version = text is null ? Match.Empty : SbtVersion.Match(text);
            if (version.Success)
            {
                context.Add(Requirement.Declared(ToolKind.Scala, version.Groups[1].Value,
                    Provenance.FileOf("build.sc", ParserText.LineOf(text!, version.Index), "scalaVersion")));
            }
        }

        if (context.Fs.FileExists("project/build.properties"))
        {
            var text = context.Read("project/build.properties");
            var version = text is null ? Match.Empty : SbtToolVersion.Match(text);
            if (version.Success)
            {
                // Advisory: sbt is not inventoried or installed, so the pin is shown but never blocks a project.
                context.Add(Requirement.Advisory(ToolKind.Sbt, version.Groups[1].Value,
                    Provenance.FileOf("project/build.properties", ParserText.LineOf(text!, version.Index), "sbt.version")));
            }
        }

        // scala-cli keeps the compiler version inside the source file.
        foreach (var file in context.Fs.ListFiles(".", "*.scala").Take(24))
        {
            var text = context.Read(file);
            if (text is null) continue;

            var directive = UsingDirective.Match(text);
            if (!directive.Success) continue;

            context.Add(Requirement.Declared(ToolKind.Scala, directive.Groups[1].Value,
                Provenance.FileOf(file, ParserText.LineOf(text, directive.Index), "//> using scala")));
            break;
        }
    }
}

/// <summary>Zig: <c>.zigversion</c> is an exact pin; <c>build.zig.zon</c> states a minimum.</summary>
public sealed class ZigParser : IProjectParser
{
    private static readonly Regex MinimumZig = new(@"\.minimum_zig_version\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    public string Id => "zig";

    public Ecosystem Ecosystem => Ecosystem.Zig;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Zig);

        if (context.Fs.FileExists(".zigversion"))
        {
            var first = ParserText.FirstMeaningfulLine(context.Read(".zigversion"));
            if (first is not null)
            {
                context.Add(Requirement.Pinned(ToolKind.Zig, first.Value.Value,
                    Provenance.FileOf(".zigversion", first.Value.Line)));
            }
        }

        var zon = context.Read("build.zig.zon");
        if (zon is null) return;

        var minimum = MinimumZig.Match(zon);
        if (minimum.Success)
        {
            context.Add(Requirement.Declared(ToolKind.Zig, ">= " + minimum.Groups[1].Value,
                Provenance.FileOf("build.zig.zon", ParserText.LineOf(zon, minimum.Index), "minimum_zig_version")));
        }
    }
}

/// <summary>Terraform: <c>required_version</c> in any top-level <c>.tf</c>, or tfenv's pin file.</summary>
public sealed class TerraformParser : IProjectParser
{
    private static readonly Regex RequiredVersion = new(@"required_version\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    public string Id => "terraform";

    public Ecosystem Ecosystem => Ecosystem.Terraform;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Terraform);

        if (context.Fs.FileExists(".terraform-version"))
        {
            var first = ParserText.FirstMeaningfulLine(context.Read(".terraform-version"));
            if (first is not null)
            {
                context.Add(Requirement.Pinned(ToolKind.Terraform, first.Value.Value,
                    Provenance.FileOf(".terraform-version", first.Value.Line)));
            }
        }

        foreach (var file in context.Fs.ListFiles(".", "*.tf").Take(24))
        {
            var text = context.Read(file);
            if (text is null) continue;

            var match = RequiredVersion.Match(text);
            if (!match.Success) continue;

            context.Add(Requirement.Declared(ToolKind.Terraform, match.Groups[1].Value,
                Provenance.FileOf(file, ParserText.LineOf(text, match.Index), "required_version")));
            return;
        }
    }
}

/// <summary>Julia: <c>[compat] julia</c> in <c>Project.toml</c>.</summary>
public sealed class JuliaParser : IProjectParser
{
    public string Id => "julia";

    public Ecosystem Ecosystem => Ecosystem.Julia;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Julia);

        if (context.Fs.FileExists("Manifest.toml")) context.Lockfiles.Add("Manifest.toml");

        var text = context.Read("Project.toml");
        if (text is null) return;

        var toml = TomlLite.Parse(text);
        context.Name ??= ParserText.SanitizeName(toml.Get("name"));

        var compat = toml.Find("compat.julia");
        if (compat is null) return;

        context.Add(Requirement.Declared(ToolKind.Julia, compat.Value.Value,
            Provenance.FileOf("Project.toml", compat.Value.Line, "compat.julia")));
    }
}

/// <summary>
/// C/C++ build tooling. A <c>CMakeLists.txt</c> says nothing about which compiler you have,
/// but it does say which CMake, Meson or Conan you need — and those are versions we can
/// resolve, install-check and report honestly.
/// </summary>
public sealed class CppBuildParser : IProjectParser
{
    private static readonly Regex CMakeMinimum = new(
        @"cmake_minimum_required\s*\(\s*VERSION\s+([0-9][0-9.]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CMakeStandard = new(
        @"(?:CMAKE_CXX_STANDARD|CXX_STANDARD)\s*""?\s*(\d{2})", RegexOptions.Compiled);

    private static readonly Regex MesonVersion = new(
        @"meson_version\s*:\s*['""]([^'""]+)['""]", RegexOptions.Compiled);

    private static readonly Regex ConanVersion = new(
        @"required_conan_version\s*=\s*['""]([^'""]+)['""]", RegexOptions.Compiled);

    private static readonly Regex XmakeMinimum = new(
        @"set_xmakever\s*\(\s*['""]([0-9][0-9.]*)['""]\s*\)", RegexOptions.Compiled);

    public string Id => "cpp";

    public Ecosystem Ecosystem => Ecosystem.Cpp;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Cpp);

        if (context.Fs.FileExists("CMakeLists.txt")) ParseCMake(context);
        if (context.Fs.FileExists("CMakePresets.json")) ParsePresets(context);
        if (context.Fs.FileExists("meson.build")) ParseMeson(context);
        if (context.Fs.FileExists("conanfile.py")) ParseConan(context);
        if (context.Fs.FileExists("xmake.lua")) ParseXmake(context);

        if (context.Fs.FileExists("conanfile.txt") || context.Fs.FileExists("vcpkg.json"))
            context.Diagnostics.Add(Diagnostic.Info("cpp-deps", "C/C++ dependency manifest present; no toolchain version declared"));
    }

    private static void ParseCMake(ProjectContext context)
    {
        var text = context.Read("CMakeLists.txt");
        if (text is null) return;

        var minimum = CMakeMinimum.Match(text);
        if (minimum.Success)
        {
            context.Add(Requirement.Declared(ToolKind.Cmake, ">= " + minimum.Groups[1].Value,
                Provenance.FileOf("CMakeLists.txt", ParserText.LineOf(text, minimum.Index), "cmake_minimum_required")));
        }

        var standard = CMakeStandard.Match(text);
        if (standard.Success)
        {
            context.Diagnostics.Add(Diagnostic.Info("cpp-standard",
                $"C++{standard.Groups[1].Value} language standard declared (compiler version is not pinned)"));
        }
    }

    private static void ParsePresets(ProjectContext context)
    {
        var text = context.Read("CMakePresets.json");
        using var document = JsonHelper.TryParse(text);
        if (document is null) return;

        var version = JsonHelper.GetText(document.RootElement, "cmakeMinimumRequired", "version");
        if (version is null &&
            JsonHelper.GetText(document.RootElement, "cmakeMinimumRequired", "major") is { } major)
        {
            var minor = JsonHelper.GetText(document.RootElement, "cmakeMinimumRequired", "minor") ?? "0";
            var patch = JsonHelper.GetText(document.RootElement, "cmakeMinimumRequired", "patch") ?? "0";
            version = $"{major}.{minor}.{patch}";
        }

        if (version is null) return;

        context.Add(Requirement.Declared(ToolKind.Cmake, ">= " + version,
            Provenance.FileOf("CMakePresets.json", 1, "cmakeMinimumRequired")));
    }

    private static void ParseMeson(ProjectContext context)
    {
        var text = context.Read("meson.build");
        if (text is null) return;

        var match = MesonVersion.Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Declared(ToolKind.Meson, match.Groups[1].Value,
            Provenance.FileOf("meson.build", ParserText.LineOf(text, match.Index), "meson_version")));
    }

    /// <summary>
    /// <c>set_xmakever</c> is the minimum xmake that can load the project. Advisory: xmake is not
    /// inventoried, so it must not turn a project into "missing a toolchain".
    /// </summary>
    private static void ParseXmake(ProjectContext context)
    {
        var text = context.Read("xmake.lua");
        if (text is null) return;

        var match = XmakeMinimum.Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Advisory(ToolKind.Xmake, ">= " + match.Groups[1].Value,
            Provenance.FileOf("xmake.lua", ParserText.LineOf(text, match.Index), "set_xmakever")));
    }

    private static void ParseConan(ProjectContext context)
    {
        var text = context.Read("conanfile.py");
        if (text is null) return;

        var match = ConanVersion.Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Declared(ToolKind.Conan, match.Groups[1].Value,
            Provenance.FileOf("conanfile.py", ParserText.LineOf(text, match.Index), "required_conan_version")));
    }
}

/// <summary>
/// Bazel. <c>.bazelversion</c> is read by Bazelisk, which downloads that Bazel itself, so the version is
/// advisory (no inventory, no install). Floating values such as <c>latest</c>, <c>last_green</c>,
/// <c>rolling</c>, <c>4.x</c> or <c>fork/version</c> name no exact version and are reported, not guessed.
/// </summary>
public sealed class BazelParser : IProjectParser
{
    public string Id => "bazel";

    public Ecosystem Ecosystem => Ecosystem.Bazel;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Bazel);

        var text = context.Read(".bazelversion");
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value)) return;

        var isFloating = !ExactVersion.IsExact(value);
        if (isFloating)
        {
            context.Diagnostics.Add(Diagnostic.Info("bazel-floating-version",
                $".bazelversion is \"{value}\", which does not name an exact Bazel version"));
            return;
        }

        context.Add(Requirement.Advisory(ToolKind.Bazel, value,
            Provenance.FileOf(".bazelversion", 1, "version")));
    }
}

/// <summary>
/// Pants. <c>pants_version</c> in <c>pants.toml</c> is the version the Pants launcher installs for the
/// repository, so it is advisory (no inventory, no install).
/// </summary>
public sealed class PantsParser : IProjectParser
{
    private static readonly Regex PantsVersion = new(
        @"^[ 	]*pants_version[ 	]*=[ 	]*['""]([^'""]+)['""]", RegexOptions.Multiline | RegexOptions.Compiled);

    public string Id => "pants";

    public Ecosystem Ecosystem => Ecosystem.Pants;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Pants);

        var text = context.Read("pants.toml");
        if (text is null) return;

        var match = PantsVersion.Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Advisory(ToolKind.Pants, match.Groups[1].Value,
            Provenance.FileOf("pants.toml", ParserText.LineOf(text, match.Index), "pants_version")));
    }
}

/// <summary>A literal version such as <c>7.2.1</c> or <c>0.12.0-RC1</c>; not <c>latest</c>, <c>4.x</c> or <c>fork/1.0</c>.</summary>
internal static class ExactVersion
{
    private static readonly Regex Pattern = new(@"^[0-9]+(\.[0-9]+)*([A-Za-z]+[0-9]*|[-+][A-Za-z0-9.\-+]+)?$", RegexOptions.Compiled);

    public static bool IsExact(string value) => Pattern.IsMatch(value);
}

/// <summary>
/// Mill (com-lihaoyi). The version may be set in a <c>//| mill-version: x</c> build header,
/// <c>.mill-version</c> or <c>.config/mill-version</c>. Mill's docs do not state which wins, so every
/// declaration found is recorded with its own provenance and none is preferred here. Advisory: the
/// <c>./mill</c> bootstrap script downloads the version itself, so Mill is not inventoried or installed.
/// </summary>
public sealed class MillParser : IProjectParser
{
    private const string HeaderKey = "mill-version:";

    // build.sc is the pre-1.0 name; it is shared with Scala scripts, so it is read here but is not a Mill marker.
    private static readonly string[] BuildFiles = ["build.mill", "build.mill.scala", "build.sc", "build.mill.yaml"];

    public string Id => "mill";

    public Ecosystem Ecosystem => Ecosystem.Mill;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Mill);

        foreach (var file in new[] { ".mill-version", ".config/mill-version" })
            AddIfExact(context, context.Read(file)?.Trim(), file, 1, "version");

        foreach (var file in BuildFiles)
        {
            var text = context.Read(file);
            if (text is null) continue;
            ReadHeader(context, file, text);
        }
    }

    private static void ReadHeader(ProjectContext context, string file, string text)
    {
        var yaml = file.EndsWith(".yaml", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (!yaml)
            {
                // The header is a block of //| lines at the very top; stop at the first other line.
                if (!line.StartsWith("//|", StringComparison.Ordinal)) return;
                line = line[3..].Trim();
            }

            if (!line.StartsWith(HeaderKey, StringComparison.Ordinal)) continue;

            AddIfExact(context, line[HeaderKey.Length..].Trim().Trim('"', '\''), file, index + 1, "mill-version");
            return;
        }
    }

    private static void AddIfExact(ProjectContext context, string? value, string file, int line, string key)
    {
        if (string.IsNullOrEmpty(value)) return;

        if (!ExactVersion.IsExact(value))
        {
            context.Diagnostics.Add(Diagnostic.Info("mill-floating-version",
                $"{file} is \"{value}\", which does not name an exact Mill version"));
            return;
        }

        context.Add(Requirement.Advisory(ToolKind.Mill, value, Provenance.FileOf(file, line, key)));
    }
}

/// <summary>
/// Clojure on the JVM. <c>project.clj</c> is Leiningen; its <c>:min-lein-version</c> is a floor, recorded as
/// advisory (Leiningen is not inventoried or installed). <c>deps.edn</c> (the Clojure CLI) declares no tool
/// version, so it only identifies the project. The Clojure library version in <c>:dependencies</c> is a
/// dependency, not a toolchain, and is deliberately not read.
/// </summary>
public sealed class ClojureParser : IProjectParser
{
    // Anchored so a version in a ';' comment is not read.
    private static readonly Regex MinLein = new(
        @"^[^;\r\n]*:min-lein-version\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.Compiled);

    public string Id => "clojure";

    public Ecosystem Ecosystem => Ecosystem.Clojure;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Clojure);

        var text = context.Read("project.clj");
        if (text is null) return;

        var match = MinLein.Match(text);
        if (!match.Success) return;

        context.Add(Requirement.Advisory(ToolKind.Leiningen, ">= " + match.Groups[1].Value,
            Provenance.FileOf("project.clj", ParserText.LineOf(text, match.Index), ":min-lein-version")));
    }
}
