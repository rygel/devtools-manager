using DevTools.Providers;

namespace DevTools.Store;

public sealed record InstallItemResult(string Tool, string Version, string Outcome, string Message);
public sealed record InstallResult(bool Succeeded, IReadOnlyList<InstallItemResult> Items, string? Error = null);

/// <summary>Rechecks an approved installation plan and records each executed step.</summary>
public static class InstallExecution
{
    public static InstallResult Apply(SqliteStore store, string? tool, string? version, string fingerprint, CancellationToken cancellationToken = default)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, [], "Another operation is using this index.");
        var snapshot = store.Load();
        if (snapshot is null) return new(false, [], "The index is unavailable.");
        var plan = InstallPlanner.Build(MachineView.Build(snapshot), tool, version);
        if (!string.Equals(plan.Plan, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(false, [], "The installation plan changed. Review it again.");
        var results = new List<InstallItemResult>();
        var planId = Guid.NewGuid().ToString("N")[..8];
        foreach (var step in plan.Steps)
        {
            if (step.Manual)
            {
                results.Add(new(step.Tool, step.Version, "manual", step.Hint ?? step.Description));
                continue;
            }
            var target = step.Tool + "@" + step.Version;
            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "install", Target = target, Detail = step.Display, Outcome = "started" }))
                return new(false, results, "Audit log unavailable; no further commands were run.");
            var execution = InstallExecutor.Execute(step, TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
            var message = execution.Succeeded ? "Installed; rescan to refresh inventory." : execution.Error ?? execution.Combined.Trim();
            results.Add(new(step.Tool, step.Version, execution.Succeeded ? "ok" : "failed", message));
            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "install", Target = target,
                    Detail = step.Display, Outcome = execution.Succeeded ? "ok" : "failed", Reversible = false }))
                return new(false, results, "A command ran, but its final audit entry could not be written.");
            if (!execution.Succeeded) return new(false, results, "Installation stopped after a failed command.");
        }
        return new(results.All(item => item.Outcome == "ok"), results);
    }
}
