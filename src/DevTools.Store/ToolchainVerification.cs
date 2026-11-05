using DevTools.Core.Model;
using DevTools.Providers;

namespace DevTools.Store;

public sealed record ToolchainVerificationResult(bool Succeeded, string Message);

/// <summary>Runs an explicitly selected probe with a lock, audit record, and index update.</summary>
public static class ToolchainVerification
{
    public static ToolchainVerificationResult Run(SqliteStore store, string path)
    {
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return new(false, "Another operation is using this index.");
        IndexSnapshot? snapshot;
        try { snapshot = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { return new(false, "The index could not be read: " + ex.Message); }
        if (snapshot is null) return new(false, "The index is unavailable.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.GetFullPath(PlatformPaths.Expand(path));
        var index = snapshot.Toolchains.FindIndex(t => !t.Stale && t.Health == ToolchainHealth.Unverified &&
            string.Equals(Path.TrimEndingDirectorySeparator(t.Path), Path.TrimEndingDirectorySeparator(full), comparison));
        if (index < 0) return new(false, "No unverified toolchain at that path.");
        var candidate = snapshot.Toolchains[index];
        var executable = ManualToolchainDiscovery.FindExecutable(candidate.Tool, candidate.Path);
        if (executable is null) return new(false, "The expected executable is missing.");
        long beforeSize;
        DateTime beforeTime;
        try
        {
            var before = new FileInfo(executable);
            beforeSize = before.Length;
            beforeTime = before.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "The executable could not be inspected."); }
        if (!store.AppendAudit(new AuditEntry { Action = "verify-toolchain", Target = candidate.Path, Outcome = "started" }))
            return new(false, "Audit log unavailable; the executable was not started.");
        var (version, error) = ManualToolchainDiscovery.Verify(candidate);
        try
        {
            var after = new FileInfo(executable);
            if (!after.Exists || after.Length != beforeSize || after.LastWriteTimeUtc != beforeTime)
            { version = null; error = "The executable changed while it was being verified."; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { version = null; error = "The executable could not be inspected after verification."; }
        if (!store.AppendAudit(new AuditEntry { Action = "verify-toolchain", Target = candidate.Path,
                Outcome = version is null ? "failed" : "verified", Detail = error ?? version }))
            return new(false, "The probe ran, but its result could not be recorded.");
        if (version is null) return new(false, "Verification failed: " + error);
        snapshot.Toolchains[index] = new Toolchain { Tool = candidate.Tool, Version = version,
            RawVersion = version, Path = candidate.Path, Manager = candidate.Manager,
            Health = ToolchainHealth.Ok, VerifiedAt = DateTimeOffset.Now,
            VerifiedExecutableSize = beforeSize, VerifiedExecutableModifiedUtc = beforeTime };
        try { store.Save(snapshot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, "The executable was verified but the index could not be saved: " + ex.Message); }
        if (!store.AppendAudit(new AuditEntry { Action = "verify-toolchain", Target = candidate.Path, Outcome = "indexed", Detail = version }))
            return new(false, "The index was updated but the final audit record could not be written.");
        return new(true, "Verified " + ToolKinds.Display(candidate.Tool) + " " + version);
    }
}
