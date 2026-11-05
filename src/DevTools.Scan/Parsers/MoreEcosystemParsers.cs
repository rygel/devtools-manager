using System.Text.RegularExpressions;
using DevTools.Core.Model;

namespace DevTools.Scan.Parsers;

/// <summary>Swift: the first line of <c>Package.swift</c> is <c>// swift-tools-version: 5.9</c>, the minimum toolchain.</summary>
public sealed class SwiftParser : IProjectParser
{
    private static readonly Regex ToolsVersion = new(@"^//\s*swift-tools-version\s*:\s*([0-9][0-9.]*)", RegexOptions.Compiled);

    public string Id => "swift";

    public Ecosystem Ecosystem => Ecosystem.Swift;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Swift);

        if (context.Fs.FileExists("Package.resolved")) context.Lockfiles.Add("Package.resolved");

        var text = context.Read("Package.swift");
        if (text is null) return;

        var firstLine = text.TrimStart('﻿').Split('\n', 2)[0].Trim();
        var match = ToolsVersion.Match(firstLine);
        if (!match.Success) return;

        context.Add(Requirement.Declared(ToolKind.Swift, ">= " + match.Groups[1].Value,
            Provenance.FileOf("Package.swift", 1, "swift-tools-version")));
    }
}

/// <summary>Crystal: <c>crystal:</c> in <c>shard.yml</c>. Shards only warns about it, so it is advisory.</summary>
public sealed class CrystalParser : IProjectParser
{
    private static readonly Regex CrystalKey = new(@"^crystal:[ \t]*(?:""([^""]*)""|'([^']*)'|([^#\r\n]+?))[ \t]*(?:#.*)?\r?$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public string Id => "crystal";

    public Ecosystem Ecosystem => Ecosystem.Crystal;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Crystal);

        if (context.Fs.FileExists("shard.lock")) context.Lockfiles.Add("shard.lock");

        var text = context.Read("shard.yml");
        if (text is null) return;

        var match = CrystalKey.Match(text);
        if (!match.Success) return;

        var value = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).Trim();
        if (value.Length == 0 || value == "*") return;

        context.Add(Requirement.Advisory(ToolKind.Crystal, value,
            Provenance.FileOf("shard.yml", ParserText.LineOf(text, match.Index), "crystal")));
    }
}

/// <summary>Gleam: <c>gleam = "&gt;= 1.0.0"</c> in <c>gleam.toml</c> is the required compiler version.</summary>
public sealed class GleamParser : IProjectParser
{
    public string Id => "gleam";

    public Ecosystem Ecosystem => Ecosystem.Gleam;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Gleam);

        if (context.Fs.FileExists("manifest.toml")) context.Lockfiles.Add("manifest.toml");

        var text = context.Read("gleam.toml");
        if (text is null) return;

        var toml = TomlLite.Parse(text);
        context.Name ??= ParserText.SanitizeName(toml.Get("name"));

        var gleam = toml.Find("gleam");
        if (gleam is null || string.IsNullOrWhiteSpace(gleam.Value.Value)) return;

        context.Add(Requirement.Declared(ToolKind.Gleam, gleam.Value.Value,
            Provenance.FileOf("gleam.toml", gleam.Value.Line, "gleam")));
    }
}

/// <summary>Nim: <c>requires "nim &gt;= 1.6.0"</c> in any top-level <c>.nimble</c> file.</summary>
public sealed class NimParser : IProjectParser
{
    private static readonly Regex RequiresLine = new(@"^[ \t]*requires\b(.*)$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Quoted = new(@"""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex NimEntry = new(@"^nim[ \t]*((?:[<>=!~^]).*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Id => "nim";

    public Ecosystem Ecosystem => Ecosystem.Nim;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.Nim);

        if (context.Fs.FileExists("nimble.lock")) context.Lockfiles.Add("nimble.lock");

        foreach (var file in context.Fs.ListFiles(".", "*.nimble").Take(4))
        {
            var text = context.Read(file);
            if (text is null) continue;

            foreach (Match line in RequiresLine.Matches(text))
            {
                foreach (Match quoted in Quoted.Matches(line.Groups[1].Value))
                {
                    foreach (var entry in quoted.Groups[1].Value.Split(','))
                    {
                        var nim = NimEntry.Match(entry.Trim());
                        if (!nim.Success) continue;

                        context.Add(Requirement.Declared(ToolKind.Nim, nim.Groups[1].Value.Trim(),
                            Provenance.FileOf(file, ParserText.LineOf(text, line.Index), "requires nim")));
                        return;
                    }
                }
            }
        }
    }
}

/// <summary>R: <c>Depends: R (&gt;= 4.1.0)</c> in <c>DESCRIPTION</c>.</summary>
public sealed class RParser : IProjectParser
{
    private static readonly Regex DependsBlock = new(@"^Depends:[ \t]*((?:.*\r?\n?)(?:[ \t]+.*\r?\n?)*)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex RVersion = new(@"(?<![A-Za-z0-9_.])R[ \t\r\n]*\([ \t\r\n]*(>=|>|==|<=|<)[ \t\r\n]*([0-9][0-9.\-]*)[ \t\r\n]*\)", RegexOptions.Compiled);

    public string Id => "r";

    public Ecosystem Ecosystem => Ecosystem.R;

    public void Extract(ProjectContext context)
    {
        context.Tag(Ecosystem.R);

        if (context.Fs.FileExists("renv.lock")) context.Lockfiles.Add("renv.lock");

        var text = context.Read("DESCRIPTION");
        if (text is null) return;

        var depends = DependsBlock.Match(text);
        if (!depends.Success) return;

        var version = RVersion.Match(depends.Groups[1].Value);
        if (!version.Success) return;

        context.Add(Requirement.Declared(ToolKind.R, version.Groups[1].Value + " " + version.Groups[2].Value,
            Provenance.FileOf("DESCRIPTION", ParserText.LineOf(text, depends.Index), "Depends: R")));
    }
}
