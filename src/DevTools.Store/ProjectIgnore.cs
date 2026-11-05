using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Scan;

namespace DevTools.Store;

public sealed record ProjectIgnoreResult(bool Succeeded, string Message);

/// <summary>
/// Manual hide/unhide of an indexed project. The Ignored state is kept across rescans by
/// project key (see ProjectScanner), and ignored projects are left out of every plan that
/// only considers Active projects.
/// </summary>
public static class ProjectIgnore
{
    public static ProjectIgnoreResult Set(SqliteStore store, string path, bool ignored)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, "Another operation is using this index.");
        IndexSnapshot? snapshot;
        try { snapshot = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(false, "The index could not be read: " + ex.Message); }
        if (snapshot is null) return new(false, "The index is unavailable; run a scan first.");

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PlatformPaths.Expand(path)));
        var project = snapshot.Projects.FirstOrDefault(p =>
            string.Equals(Path.TrimEndingDirectorySeparator(p.Path), full, comparison));
        if (project is null) return new(false, "No indexed project at " + full + ".");

        var action = ignored ? "ignore" : "unignore";
        if (ignored)
        {
            if (project.State == ProjectState.Ignored) return new(true, "Already ignored: " + project.Path);
            project.State = ProjectState.Ignored;
        }
        else
        {
            if (project.State != ProjectState.Ignored) return new(false, "Not ignored: " + project.Path);
            project.State = project.Confidence >= Confidence.Threshold ? ProjectState.Active : ProjectState.Suppressed;
        }

        if (!store.AppendAudit(new AuditEntry { Action = action, Target = project.Path, Outcome = "started" }))
            return new(false, "Audit log unavailable; the index was not changed.");
        try { store.Save(snapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "The index could not be saved: " + ex.Message); }
        if (!store.AppendAudit(new AuditEntry { Action = action, Target = project.Path, Outcome = "indexed" }))
            return new(false, "The index was updated but the final audit record could not be written.");
        return new(true, (ignored ? "Ignored " : "Restored ") + project.Path);
    }
}
