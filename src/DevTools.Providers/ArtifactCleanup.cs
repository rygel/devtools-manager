using DevTools.Core.Model;
using System.Security.Cryptography;
using System.Text;

namespace DevTools.Providers;

public sealed record ArtifactTarget(ProjectRecord Project, string Directory, long? SizeBytes, DateTime CreatedUtc, DateTime ModifiedUtc);

/// <summary>Builds a cleanup plan without changing project files.</summary>
public static class ArtifactCleanup
{
    private static readonly string[] Names = ["node_modules", "target", "obj", "dist"];

    public static bool IsKnownName(string name) => Names.Contains(name, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string Fingerprint(IEnumerable<ArtifactTarget> targets)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            foreach (var target in targets.OrderBy(t => t.Directory, StringComparer.Ordinal))
            {
                writer.Write(target.Directory);
                writer.Write(target.CreatedUtc.Ticks);
                writer.Write(target.ModifiedUtc.Ticks);
            }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16];
    }

    public static bool HasUnsafePath(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            while (true)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current) return false;
                current = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    public static bool HasChanged(ArtifactTarget target)
    {
        var info = new DirectoryInfo(target.Directory);
        return !info.Exists || info.CreationTimeUtc != target.CreatedUtc || info.LastWriteTimeUtc != target.ModifiedUtc;
    }

    public static IReadOnlyList<ArtifactTarget> Plan(IEnumerable<ProjectRecord> projects, string? projectPath = null,
        DateTimeOffset? olderThan = null, bool measure = false, string? artifactName = null, CancellationToken cancellationToken = default)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var selectedPath = projectPath is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var results = new List<ArtifactTarget>();
        foreach (var project in projects.Where(p => p.State == ProjectState.Active && !p.Stale))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Path));
            if (selectedPath is not null && !path.Equals(selectedPath, comparison)) continue;
            if (olderThan is not null && (project.LastActivityAt is null || project.LastActivityAt >= olderThan)) continue;
            foreach (var name in Names)
            {
                if (artifactName is not null && !name.Equals(artifactName, comparison)) continue;
                var target = Path.Combine(path, name);
                if (!Directory.Exists(target)) continue;
                var info = new DirectoryInfo(target);
                var created = info.CreationTimeUtc;
                var modified = info.LastWriteTimeUtc;
                results.Add(new ArtifactTarget(project, target, measure ? Measure(target, cancellationToken) : null, created, modified));
            }
        }
        return results;
    }

    private static long? Measure(string root, CancellationToken cancellationToken)
    {
        try
        {
            long total = 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                    else if (entry is FileInfo file) total = checked(total + file.Length);
                }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }
}
