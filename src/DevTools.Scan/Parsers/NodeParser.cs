using DevTools.Core.Model;
using DevTools.Core.Versioning;
using DevTools.Scan.Parsers;

namespace DevTools.Scan.Parsers;

/// <summary>
/// Node/JS toolchain extraction: <c>.nvmrc</c>, <c>.node-version</c>, <c>package.json</c>
/// (engines, packageManager, volta), lockfiles, and <c>.tool-versions</c>.
/// </summary>
public sealed class NodeParser : IProjectParser
{
    public string Id => "node";

    public Ecosystem Ecosystem => Ecosystem.Node;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Node);

        ReadPin(context, ".nvmrc");
        ReadPin(context, ".node-version");
        ReadPin(context, "mise.toml");
        ReadPin(context, ".mise.toml");

        if (context.Fs.FileExists("package.json")) ParsePackageJson(context);

        DetectLockfile(context, "pnpm-lock.yaml", ToolKind.Pnpm);
        DetectLockfile(context, "yarn.lock", ToolKind.Yarn);
        DetectLockfile(context, "bun.lockb", ToolKind.Bun);
        DetectLockfile(context, "bun.lock", ToolKind.Bun);
    }

    private static void ReadPin(ProjectContext context, string file)
    {
        if (!context.Fs.FileExists(file)) return;

        var text = context.Read(file);
        var first = ParserText.FirstMeaningfulLine(text);
        if (first is null) return;

        // mise.toml is a TOML file, not a single-line pin.
        if (file.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            var toml = TomlLite.Parse(text);
            if (toml.Find("tools.node", "tools.nodejs") is { } pin)
            {
                context.Add(Requirement.Pinned(ToolKind.Node, pin.Value,
                    Provenance.FileOf(file, pin.Line, "tools.node")));
            }

            return;
        }

        context.Add(Requirement.Pinned(ToolKind.Node, first.Value.Value, Provenance.FileOf(file, first.Value.Line)));
    }

    private static void ParsePackageJson(ProjectContext context)
    {
        var text = context.Read("package.json");
        using var doc = JsonHelper.TryParse(text);
        if (doc is null)
        {
            context.Warn("invalid-manifest", "package.json could not be parsed as JSON", "package.json");
            return;
        }

        var root = doc.RootElement;

        context.Name ??= ParserText.SanitizeName(JsonHelper.GetText(root, "name"));
        if (JsonHelper.Exists(root, "workspaces")) context.IsWorkspaceRoot = true;

        if (JsonHelper.GetText(root, "engines", "node") is { } engines)
        {
            context.Add(Requirement.Advisory(ToolKind.Node, engines,
                Provenance.FileOf("package.json", FindLine(text, "engines"), "engines.node")));
        }

        if (JsonHelper.GetText(root, "volta", "node") is { } voltaNode)
        {
            context.Add(Requirement.Pinned(ToolKind.Node, voltaNode,
                Provenance.FileOf("package.json", FindLine(text, "\"volta\""), "volta.node")));
        }

        if (JsonHelper.GetText(root, "packageManager") is { } packageManager)
        {
            // "pnpm@9.1.0+sha512.abc" → tool "pnpm", version "9.1.0"
            var at = packageManager.LastIndexOf('@');
            if (at > 0)
            {
                var name = packageManager[..at];
                var version = packageManager[(at + 1)..].Split('+')[0];

                if (ToolKinds.TryParse(name, out var tool))
                {
                    context.Add(Requirement.Declared(tool, version,
                        Provenance.FileOf("package.json", FindLine(text, "packageManager"), "packageManager")));
                }
            }
        }

        if (JsonHelper.GetText(root, "engines", "npm") is { } npmRange)
        {
            context.Add(Requirement.Advisory(ToolKind.Npm, npmRange,
                Provenance.FileOf("package.json", FindLine(text, "engines"), "engines.npm")));
        }
    }

    private static void DetectLockfile(ProjectContext context, string file, ToolKind tool)
    {
        if (!context.Fs.FileExists(file)) return;

        context.Lockfiles.Add(file);

        // A lockfile proves the package manager is used, but says nothing about its version.
        // An unconstrained requirement still answers "is it installed at all?".
        context.Add(Requirement.Advisory(tool, "*", Provenance.FileOf(file, null, "lockfile")));
    }

    private static int FindLine(string? text, string needle)
    {
        if (text is null) return 0;
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? 0 : ParserText.LineOf(text, index);
    }
}
