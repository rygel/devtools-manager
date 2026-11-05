using DevTools.Providers;

namespace DevTools.Store;

public sealed record CleanupItemResult(string Path, bool Moved, string Message);
public sealed record CleanupResult(bool Succeeded, IReadOnlyList<CleanupItemResult> Items, string? Error = null);

/// <summary>Rechecks an approved cleanup plan under the store lock and moves targets to trash.</summary>
public static class CleanupExecution
{
    public static CleanupResult Apply(SqliteStore store, string? projectPath, DateTimeOffset? olderThan,
        string? artifactName, string fingerprint)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, [], "Another operation is using this index.");
        var snapshot = store.Load();
        if (snapshot is null) return new(false, [], "The index is unavailable.");
        var targets = ArtifactCleanup.Plan(snapshot.Projects, projectPath, olderThan, artifactName: artifactName);
        if (!string.Equals(fingerprint, ArtifactCleanup.Fingerprint(targets), StringComparison.OrdinalIgnoreCase))
            return new(false, [], "The cleanup plan changed. Review the current list again.");
        var gio = OperatingSystem.IsLinux() ? PathSolver.Which("gio") : null;
        if (!OperatingSystem.IsWindows() && gio is null)
            return new(false, [], "Recoverable deletion is unavailable on this platform.");
        if (targets.Count == 0) return new(true, []);
        if (!store.AppendAudit(new AuditEntry { Action = "prune-plan", Target = projectPath ?? "multiple projects",
                Detail = fingerprint, Outcome = "started" }))
            return new(false, [], "Audit log unavailable; no targets were moved.");
        var hadMeasurements = targets.Any(target => target.Project.SizeBytes is not null || target.Project.ArtifactBytes is not null);
        if (hadMeasurements)
        {
            foreach (var target in targets)
            {
                target.Project.SizeBytes = null;
                target.Project.ArtifactBytes = null;
            }
            try { store.Save(snapshot); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, [], "Disk measurements could not be invalidated; no targets were moved: " + ex.Message); }
        }
        var results = new List<CleanupItemResult>();
        string? operationError = null;
        foreach (var target in targets)
        {
            try
            {
                if (ArtifactCleanup.HasUnsafePath(target.Directory) || ArtifactCleanup.HasChanged(target))
                {
                    results.Add(new(target.Directory, false, "Unsafe, changed, or unavailable path."));
                    continue;
                }
                if (!store.AppendAudit(new AuditEntry { Action = "prune", Target = target.Directory,
                        Detail = target.Project.DisplayName, Outcome = "started", Reversible = true }))
                { operationError = "Audit log unavailable; no further targets were moved."; break; }
                if (OperatingSystem.IsWindows())
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(target.Directory,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                {
                    var result = ProcessRunner.Shared.Run(gio!, ["trash", "--", target.Directory], TimeSpan.FromMinutes(2));
                    if (!result.Succeeded) throw new IOException(result.Error ?? result.Combined.Trim());
                }
                results.Add(new(target.Directory, true, "Moved to trash."));
                if (!store.AppendAudit(new AuditEntry { Action = "prune", Target = target.Directory,
                        Detail = target.Project.DisplayName, Outcome = "trashed", Reversible = true }))
                { operationError = "A target was moved but its final audit entry could not be written."; break; }
            }
            catch (Exception ex)
            {
                results.Add(new(target.Directory, false, ex.Message));
                if (!store.AppendAudit(new AuditEntry { Action = "prune", Target = target.Directory, Detail = ex.Message, Outcome = "failed" }))
                { operationError = "Could not record the failure; no further targets were moved."; break; }
            }
        }
        return new(operationError is null && results.All(item => item.Moved), results, operationError);
    }
}
