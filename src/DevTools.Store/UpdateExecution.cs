using DevTools.Providers;

namespace DevTools.Store;

/// <summary>Rechecks an approved update plan and records each executed step in the audit log.</summary>
public static class UpdateExecution
{
    /// <param name="stepToken">When set, only the step with this single-step token runs (the fingerprint then covers that step).</param>
    /// <param name="releases">The release data the plan was built with. It and <paramref name="outdatedOnly"/> must match the dry run, or the token will not.</param>
    public static InstallResult Apply(SqliteStore store, string? tool, string fingerprint, string? stepToken = null,
        IReadOnlyDictionary<DevTools.Core.Model.ToolKind, IReadOnlyList<EolCycle>>? releases = null, bool outdatedOnly = false,
        CancellationToken cancellationToken = default)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, [], "Another operation is using this index.");
        var snapshot = store.Load();
        if (snapshot is null) return new(false, [], "The index is unavailable.");

        var plan = UpdatePlanner.Build(MachineView.Build(snapshot), tool, releases, outdatedOnly);
        var steps = stepToken is null
            ? plan.Steps
            : plan.Steps.Where(step => string.Equals(step.Plan, stepToken, StringComparison.OrdinalIgnoreCase)).ToList();
        var actual = stepToken is null ? plan.Plan : InstallPlanner.Fingerprint(steps);
        if (steps.Count == 0 || !string.Equals(actual, fingerprint, StringComparison.OrdinalIgnoreCase))
            return new(false, [], "The update plan changed. Review it again.");

        var results = new List<InstallItemResult>();
        var planId = Guid.NewGuid().ToString("N")[..8];
        foreach (var step in steps)
        {
            if (step.Manual)
            {
                results.Add(new(step.Tool, step.Version, "manual", step.Hint ?? step.Description));
                continue;
            }

            var target = step.Tool + " " + step.Version;
            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "update", Target = target, Detail = step.Display, Outcome = "started" }))
                return new(false, results, "Audit log unavailable; no further commands were run.");

            var execution = InstallExecutor.Execute(step, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken);
            var detail = execution.Combined.Trim();
            var message = execution.Succeeded
                ? "Updated; rescan to refresh the inventory."
                : execution.Error ?? (detail.Length > 0 ? detail : "exit code " + execution.ExitCode);
            results.Add(new(step.Tool, step.Version, execution.Succeeded ? "ok" : "failed", message));

            if (!store.AppendAudit(new AuditEntry { PlanId = planId, Action = "update", Target = target,
                    Detail = step.Display, Outcome = execution.Succeeded ? "ok" : "failed", Reversible = false }))
                return new(false, results, "A command ran, but its final audit entry could not be written.");

            // One failed manager must not stop unrelated ones, but the run is reported as failed.
            if (cancellationToken.IsCancellationRequested) return new(false, results, "Update cancelled.");
        }

        var failed = results.Count(item => item.Outcome == "failed");
        return new(failed == 0, results, failed == 0 ? null : $"{failed} update{(failed == 1 ? "" : "s")} failed.");
    }
}
