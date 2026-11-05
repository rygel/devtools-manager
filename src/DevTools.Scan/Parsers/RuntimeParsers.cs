using System.Text.RegularExpressions;
using DevTools.Core.Model;
using DevTools.Scan.Parsers;

namespace DevTools.Scan.Parsers;

/// <summary>
/// .NET SDK extraction. <c>global.json</c> is the only real pin; a <c>TargetFramework</c>
/// is a lower bound on the SDK, so it is advisory and mapped to a major.minor.
/// </summary>
public sealed partial class DotNetParser : IProjectParser
{
    public string Id => "dotnet";

    public Ecosystem Ecosystem => Ecosystem.DotNet;

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<;]+)")]
    private static partial Regex TargetFrameworkRegex();

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.DotNet);

        if (context.Fs.FileExists("global.json")) ParseGlobalJson(context);

        foreach (var project in context.Fs.ListFiles(".", "*.csproj")
                     .Concat(context.Fs.ListFiles(".", "*.fsproj"))
                     .Concat(context.Fs.ListFiles(".", "*.vbproj")))
        {
            ParseProjectFile(context, project);
        }

        if (context.Fs.FileExists("Directory.Build.props")) ParseProjectFile(context, "Directory.Build.props");

        var solution = context.Fs.ListFiles(".", "*.sln").Concat(context.Fs.ListFiles(".", "*.slnx")).FirstOrDefault();
        if (solution is not null) context.Name ??= Path.GetFileNameWithoutExtension(solution);
    }

    private static void ParseGlobalJson(ProjectContext context)
    {
        var text = context.Read("global.json");
        using var doc = JsonHelper.TryParse(text);
        if (doc is null)
        {
            context.Warn("invalid-manifest", "global.json could not be parsed", "global.json");
            return;
        }

        if (JsonHelper.GetText(doc.RootElement, "sdk", "version") is { } version)
        {
            var rollForward = JsonHelper.GetText(doc.RootElement, "sdk", "rollForward");
            var provenance = Provenance.FileOf("global.json", 1, "sdk.version");
            if (rollForward is not null)
                provenance.Note = "rollForward: " + rollForward;

            var requirement = Requirement.Pinned(ToolKind.DotNetSdk, version, provenance);
            requirement.RollForward = rollForward;
            context.Add(requirement);
        }
    }

    private static void ParseProjectFile(ProjectContext context, string file)
    {
        var text = context.Read(file);
        if (text is null) return;

        var match = TargetFrameworkRegex().Match(text);
        if (!match.Success) return;

        var tfm = match.Groups[1].Value.Trim();
        if (!TryMapTargetFramework(tfm, out var version)) return;

        context.Add(Requirement.Advisory(ToolKind.DotNetSdk, version,
            Provenance.FileOf(file, ParserText.LineOf(text, match.Index), "TargetFramework")));
    }

    /// <summary>net8.0 → 8.0, netcoreapp3.1 → 3.1. netstandard/net4x are not SDK constraints.</summary>
    internal static bool TryMapTargetFramework(string tfm, out string version)
    {
        version = "";

        if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)) return false;
        if (tfm.StartsWith("net4", StringComparison.OrdinalIgnoreCase)) return false;

        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            version = tfm["netcoreapp".Length..];
            return version.Length > 0;
        }

        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = tfm[3..];
            // "net8.0-windows10.0.19041.0" → "8.0"
            var dash = candidate.IndexOf('-');
            if (dash >= 0) candidate = candidate[..dash];
            if (candidate.Length == 0 || !char.IsAsciiDigit(candidate[0])) return false;
            version = candidate;
            return true;
        }

        return false;
    }
}

/// <summary>Python extraction: PEP 621 <c>requires-python</c>, Poetry's <c>python</c> key, and pin files.</summary>
public sealed class PythonParser : IProjectParser
{
    public string Id => "python";

    public Ecosystem Ecosystem => Ecosystem.Python;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Python);

        if (context.Fs.FileExists("pyproject.toml")) ParsePyproject(context);
        if (context.Fs.FileExists(".python-version")) ParsePinFile(context);
        if (context.Fs.FileExists("Pipfile")) ParsePipfile(context);

        if (context.Fs.FileExists("uv.lock"))
        {
            context.Lockfiles.Add("uv.lock");
            context.Add(Requirement.Advisory(ToolKind.Uv, "*", Provenance.FileOf("uv.lock", null, "lockfile")));
        }

        if (context.Fs.FileExists("requirements.txt")) context.Lockfiles.Add("requirements.txt");
    }

    private static void ParsePyproject(ProjectContext context)
    {
        var text = context.Read("pyproject.toml");
        if (text is null) return;

        var toml = TomlLite.Parse(text);

        var requires = toml.Find("project.requires-python", "tool.poetry.dependencies.python");
        if (requires is not null)
        {
            context.Add(Requirement.Declared(ToolKind.Python, requires.Value.Value,
                Provenance.FileOf("pyproject.toml", requires.Value.Line, "requires-python")));
        }

        context.Name ??= toml.Get("project.name") ?? toml.Get("tool.poetry.name");

        if (toml.Find("tool.uv.workspace") is not null ||
            toml.Get("tool.uv.workspace.members") is not null)
            context.IsWorkspaceRoot = true;
    }

    private static void ParsePinFile(ProjectContext context)
    {
        var text = context.Read(".python-version");

        // pyenv allows a file with several lines; the first is the active one.
        var first = ParserText.FirstMeaningfulLine(text);
        if (first is null) return;

        context.Add(Requirement.Pinned(ToolKind.Python, first.Value.Value,
            Provenance.FileOf(".python-version", first.Value.Line)));
    }

    private static void ParsePipfile(ProjectContext context)
    {
        var text = context.Read("Pipfile");
        if (text is null) return;

        var toml = TomlLite.Parse(text);
        if (toml.Find("requires.python_version", "requires.python_full_version") is { } requires)
        {
            context.Add(Requirement.Declared(ToolKind.Python, requires.Value,
                Provenance.FileOf("Pipfile", requires.Line, "requires.python_version")));
        }
    }
}

/// <summary>Go extraction from <c>go.mod</c>: the <c>go</c> directive is a minimum, <c>toolchain</c> is a pin.</summary>
public sealed partial class GoParser : IProjectParser
{
    public string Id => "go";

    public Ecosystem Ecosystem => Ecosystem.Go;

    [GeneratedRegex(@"^\s*go\s+([0-9][0-9A-Za-z.]*)", RegexOptions.Multiline)]
    private static partial Regex GoDirective();

    [GeneratedRegex(@"^\s*toolchain\s+go([0-9][0-9A-Za-z.]*)", RegexOptions.Multiline)]
    private static partial Regex ToolchainDirective();

    [GeneratedRegex(@"^\s*module\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex ModuleDirective();

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Go);

        if (!context.Fs.FileExists("go.mod")) return;

        var text = context.Read("go.mod");
        if (text is null) return;

        var module = ModuleDirective().Match(text);
        if (module.Success) context.Name ??= module.Groups[1].Value.Split('/').Last();

        var go = GoDirective().Match(text);
        if (go.Success)
        {
            // "go 1.27.0" is a *minimum* language version, not a pin: Go 1.27.3 satisfies it.
            context.Add(Requirement.Declared(ToolKind.Go, ">= " + go.Groups[1].Value,
                Provenance.FileOf("go.mod", ParserText.LineOf(text, go.Index), "go")));
        }

        var toolchain = ToolchainDirective().Match(text);
        if (toolchain.Success)
        {
            context.Add(Requirement.Pinned(ToolKind.Go, toolchain.Groups[1].Value,
                Provenance.FileOf("go.mod", ParserText.LineOf(text, toolchain.Index), "toolchain")));
        }
    }
}

/// <summary>Rust extraction: <c>rust-toolchain.toml</c> channel and Cargo's <c>rust-version</c>.</summary>
public sealed class RustParser : IProjectParser
{
    public string Id => "rust";

    public Ecosystem Ecosystem => Ecosystem.Rust;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Rust);

        if (context.Fs.FileExists("rust-toolchain.toml"))
        {
            var toml = TomlLite.Parse(context.Read("rust-toolchain.toml"));
            var channel = toml.Find("toolchain.channel", "channel");
            if (channel is { } channelPin)
            {
                context.Add(Requirement.Pinned(ToolKind.Rustc, channelPin.Value,
                    Provenance.FileOf("rust-toolchain.toml", channelPin.Line, "toolchain.channel")));
            }
        }

        if (context.Fs.FileExists("rust-toolchain"))
        {
            var first = ParserText.FirstMeaningfulLine(context.Read("rust-toolchain"));
            if (first is not null)
            {
                var value = first.Value.Value.Contains('[') ? "stable" : first.Value.Value;
                context.Add(Requirement.Pinned(ToolKind.Rustc, value,
                    Provenance.FileOf("rust-toolchain", first.Value.Line)));
            }
        }

        if (!context.Fs.FileExists("Cargo.toml")) return;

        var cargo = TomlLite.Parse(context.Read("Cargo.toml"));
        context.Name ??= cargo.Get("package.name");

        if (cargo.Find("workspace") is not null || cargo.Get("workspace.members") is not null)
            context.IsWorkspaceRoot = true;

        if (cargo.Find("package.rust-version") is { } rustVersion)
        {
            context.Add(Requirement.Advisory(ToolKind.Rustc, rustVersion.Value,
                Provenance.FileOf("Cargo.toml", rustVersion.Line, "package.rust-version")));
        }
    }
}
