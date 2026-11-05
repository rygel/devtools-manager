using DevTools.Providers;

namespace DevTools.Store;

public sealed record MigrationItemResult(string Path, bool Succeeded, string Message);
public sealed record MigrationResult(bool Succeeded, IReadOnlyList<MigrationItemResult> Items, string? Error = null, string? BackupDirectory = null);

/// <summary>Rebuilds and verifies the reviewed migration while holding the store lock.</summary>
public static class MigrationExecution
{
    public static MigrationResult Apply(SqliteStore store, MigrationOptions options, string fingerprint)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, [], "Another operation is using this index.");
        var snapshot = store.Load();
        if (snapshot is null) return new(false, [], "The index is unavailable.");
        var plan = MigrationPlanner.Plan(snapshot.Projects.Where(project => !project.Stale), options);
        if (!string.Equals(plan.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(false, [], "The migration plan changed. Review it again.");
        var planId = Guid.NewGuid().ToString("N");
        var backupRoot = Path.Combine(store.Root, "backups");
        var results = new List<MigrationItemResult>();
        foreach (var edit in plan.Edits)
        {
            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "migrate", Target = edit.FullPath, Detail = edit.Change, Outcome = "started" }))
                return new(false, results, "Audit log unavailable; no further files were changed.", Path.Combine(backupRoot, planId));
            var (ok, message) = MigrationExecutor.Apply(edit, backupRoot, planId);
            results.Add(new(edit.FullPath, ok, message));
            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "migrate", Target = edit.FullPath,
                    Detail = edit.Change, Outcome = ok ? "ok" : "failed: " + message, Reversible = true }))
                return new(false, results, "A migration result could not be recorded; no further files were changed.", Path.Combine(backupRoot, planId));
            if (!ok) return new(false, results, "Migration stopped after a failed edit.", Path.Combine(backupRoot, planId));
        }
        return new(true, results, BackupDirectory: Path.Combine(backupRoot, planId));
    }
}
