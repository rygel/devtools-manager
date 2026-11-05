using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Scan;

namespace DevTools.Desktop;

/// <summary>What DevTools does with one toolchain, taken from the real providers and install planner.</summary>
public sealed record SupportedTool(ToolKind Tool, bool Discovered, bool Installable);

/// <summary>Everything supported for one ecosystem: how it is detected, its toolchains and its frameworks.</summary>
public sealed record SupportGroup(
    Ecosystem Ecosystem,
    string Title,
    IReadOnlyList<string> Markers,
    IReadOnlyList<SupportedTool> Tools,
    IReadOnlyList<FrameworkInfo> Frameworks);

/// <summary>
/// The data behind the Supported page. Nothing here is hand-listed: markers, tools, frameworks,
/// discovery and installation all come from the same catalogues the scanner and planner use, so the
/// page cannot drift from what the product does.
/// </summary>
public static class SupportCatalog
{
    private static readonly Lazy<HashSet<ToolKind>> DiscoveredTools = new(() => [.. ToolchainInventory.CreateDefault(false).Tools]);

    public static string EcosystemTitle(Ecosystem ecosystem) => ecosystem switch
    {
        Ecosystem.Node => "Node.js",
        Ecosystem.DotNet => ".NET",
        Ecosystem.Php => "PHP",
        Ecosystem.Cpp => "C / C++",
        Ecosystem.Container => "Containers",
        _ => char.ToUpperInvariant(ToolKinds.DisplayEcosystem(ecosystem)[0]) + ToolKinds.DisplayEcosystem(ecosystem)[1..]
    };

    public static IReadOnlyList<SupportGroup> Build(string? filter = null)
    {
        var term = filter?.Trim() ?? "";
        var markers = MarkerCatalog.MarkersByEcosystem;
        var ecosystems = markers.Keys
            .Concat(ToolKinds.All.Select(ToolKinds.EcosystemOf))
            .Concat(FrameworkCatalog.All.Select(framework => framework.Ecosystem))
            .Distinct()
            .OrderBy(ecosystem => ecosystem);

        var groups = new List<SupportGroup>();
        foreach (var ecosystem in ecosystems)
        {
            var title = EcosystemTitle(ecosystem);
            var tools = ToolKinds.All.Where(tool => ToolKinds.EcosystemOf(tool) == ecosystem)
                .Select(tool => new SupportedTool(tool, DiscoveredTools.Value.Contains(tool), IsInstallable(tool)))
                .ToList();
            var frameworks = FrameworkCatalog.All.Where(framework => framework.Ecosystem == ecosystem).ToList();
            var groupMarkers = markers.TryGetValue(ecosystem, out var names) ? names : [];

            // A marker file name such as "pom.xml" or ".lpi" also finds the ecosystem it identifies.
            if (term.Length > 0 && !Contains(title, term) && !Contains(ToolKinds.DisplayEcosystem(ecosystem), term)
                && !groupMarkers.Any(marker => Contains(marker, term)))
            {
                tools = tools.Where(tool => Contains(ToolIcons.Title(tool.Tool), term) || Contains(ToolKinds.Display(tool.Tool), term)).ToList();
                frameworks = frameworks.Where(framework => Contains(framework.Name, term) || Contains(framework.Id, term)).ToList();
                if (tools.Count == 0 && frameworks.Count == 0) continue;
            }

            groups.Add(new SupportGroup(ecosystem, title, groupMarkers, tools, frameworks));
        }

        return groups;
    }

    public static string Summary()
    {
        var ecosystems = Build().Count;
        return $"{ToolKinds.All.Count} toolchains and {FrameworkCatalog.All.Count} frameworks across {ecosystems} ecosystems. " +
               "Toolchains are tracked and matched against what projects declare; frameworks are only read from project files and are never installed.";
    }

    private static bool IsInstallable(ToolKind tool) =>
        InstallPlanner.Managers.TryGetValue(tool, out var managers) && managers.Any(manager => manager != "manual");

    private static bool Contains(string text, string term) => text.Contains(term, StringComparison.OrdinalIgnoreCase);
}
