using DevTools.Core.Model;
using DevTools.Providers;

namespace DevTools.Desktop;

/// <summary>Every list row carries its plain-text form, used for accessibility names and tests.</summary>
public interface IRow
{
    string Plain { get; }
}

public sealed record ProjectRow(ProjectView View, MachineView Machine, bool IsWsl, string Status) : IRow
{
    public string Plain
    {
        get
        {
            var project = View.Project;
            var requirements = View.Resolution.Tools.Count == 0
                ? (project.Incomplete ? "Requirements could not be fully determined" : "No declared requirements")
                : string.Join("\n", View.Resolution.Tools.Select(t =>
                    $"{ToolKinds.Display(t.Tool)} {t.ConstraintDisplay}  ·  {t.Status}{(t.Toolchain is null ? "" : " · available " + t.Toolchain.Version)}  ·  {t.Requirement?.Provenance.Describe()}"));
            var diagnostics = string.Join("\n", project.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Select(d => $"{d.Code}: {d.Message}"));
            var git = project.GitRemote is null ? "" : $"\nGit: {project.GitRemote}" + (project.GitBranch is null ? "" : $" ({project.GitBranch})");
            var frameworks = project.Frameworks.Count == 0 ? "" : "\nFrameworks: " + string.Join(", ", project.Frameworks.Select(f => f.Describe()));
            return $"[{Machine.Snapshot.EnvironmentName}] {project.DisplayName}  ·  {Status}  ·  {string.Join(", ", project.Ecosystems.Select(ToolKinds.DisplayEcosystem))}\n{project.Path}{git}\n{requirements}{frameworks}" +
                (diagnostics.Length > 0 ? "\n" + diagnostics : "");
        }
    }
}

public sealed record ToolRow(Toolchain Toolchain, MachineView Machine, bool IsWsl, int UsedBy, string Plain, EolAssessment? Eol = null) : IRow;

public sealed record DiskRow(ProjectRecord Project, MachineView Machine, bool IsWsl, double Share, string Plain) : IRow;

public sealed record CleanupRow(ArtifactTarget? Local, WslCleanupTarget? Remote, string ProjectPath, string? Distribution,
    string Path, string Size, bool Available, string Plain) : IRow;

public sealed record InstallRow(PlannedStep Step, string? Distribution, string Environment, string Plain) : IRow;

public sealed record UpdateRow(PlannedStep Step, string Plain) : IRow;

public sealed record EditRow(string Title, string Path, string Change, string Before, string After, bool Skipped, string Plain) : IRow;

public sealed record ToolChoice(string Id, string Name, string Detail) : IRow
{
    public string Plain => Name + " — " + Detail;
}

public sealed record MigrationEnvironment(string Label, string Detail, string? Distribution) : IRow
{
    public string Plain => Label + "\n" + Detail;
    public override string ToString() => Label;
}

public sealed record WslChoice(string Name, string Status) : IRow
{
    public string Plain => Name + " — " + Status;
    public override string ToString() => Plain;
}

public sealed record NavItem(string Label, string Count) : IRow
{
    public string Plain => Label;
}

/// <summary>Heads one tool's group on the Toolchains page. Never selectable.</summary>
public sealed record ToolGroupHeader(ToolKind Tool, int Count, bool First = false) : IRow
{
    public string CountText => Count + " installed";
    public string Plain => ToolIcons.Title(Tool) + " — " + CountText;
}

public static class ToolGroups
{
    /// <summary>One header per tool, then that tool's installations newest version first. Unparseable versions sort last.</summary>
    public static IReadOnlyList<IRow> Build(IEnumerable<ToolRow> rows)
    {
        var result = new List<IRow>();
        foreach (var group in rows.GroupBy(row => row.Toolchain.Tool).OrderBy(group => (int)group.Key))
        {
            var ordered = group.OrderByDescending(row => row.Toolchain.Parsed)
                .ThenBy(row => row.Toolchain.Path, StringComparer.Ordinal).ToList();
            result.Add(new ToolGroupHeader(group.Key, ordered.Count, result.Count == 0));
            result.AddRange(ordered);
        }
        return result;
    }
}