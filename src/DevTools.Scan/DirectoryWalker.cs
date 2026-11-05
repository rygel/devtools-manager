using System.Collections.Concurrent;
using DevTools.Core.Model;

namespace DevTools.Scan;

public sealed class WalkResult
{
    public List<DiscoveredDirectory> Candidates { get; } = [];
    public ScanStats Stats { get; } = new();
    public List<Diagnostic> Diagnostics { get; } = [];
    public List<string> RootsScanned { get; } = [];
    public List<string> RootsMissing { get; } = [];
    public List<string> UnavailablePaths { get; } = [];
    public List<string> ToolchainCandidates { get; } = [];
}

/// <summary>
/// Breadth-first, level-parallel directory walk that reads no file contents.
///
/// The invariant this class exists to protect: one enumeration per directory, one
/// attribute read per marker hit, zero file opens. Everything faster than that
/// (MFT/USN) is an optimisation of the same shape.
/// </summary>
public sealed class DirectoryWalker
{
    private readonly PruningPolicy _pruning;
    private static readonly HashSet<string> ToolchainBinaryNames = new(
        OperatingSystem.IsWindows() ? ["java.exe", "mvn.cmd", "gradle.bat", "node.exe"] : ["java", "mvn", "gradle", "node"],
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public DirectoryWalker(PruningPolicy? pruning = null)
    {
        _pruning = pruning ?? new PruningPolicy();
    }

    public WalkResult Walk(ScanOptions options, CancellationToken cancellationToken = default)
    {
        var result = new WalkResult();
        var candidates = new ConcurrentBag<DiscoveredDirectory>();
        var diagnostics = new ConcurrentBag<Diagnostic>();
        var unavailable = new ConcurrentBag<string>();
        var toolchainCandidates = new ConcurrentBag<string>();
        var pruning = options.ExcludeDirectoryNames.Count == 0 ? _pruning : new PruningPolicy(options.ExcludeDirectoryNames);

        var visited = 0;
        var pruned = 0;
        var errors = 0;
        var prunedByReason = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var level = new List<(string Path, int Depth, bool ParentIsProject)>();

        foreach (var root in options.Roots)
        {
            var full = SafeFullPath(root);
            if (full is null || !Directory.Exists(full))
            {
                result.RootsMissing.Add(root);
                if (full is not null) unavailable.Add(full);
                diagnostics.Add(Diagnostic.Warn("root-missing", $"root does not exist: {root}"));
                continue;
            }

            result.RootsScanned.Add(full);
            level.Add((full, 0, false));
        }

        var seen = new ConcurrentDictionary<string, byte>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxParallelism,
            CancellationToken = cancellationToken
        };

        while (level.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var next = new ConcurrentBag<(string, int, bool)>();

            Parallel.ForEach(level, parallelOptions, item =>
            {
                if (!seen.TryAdd(item.Path, 0)) return;
                Interlocked.Increment(ref visited);

                var name = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar));
                if (item.Depth > 0)
                {
                    var parent = Path.GetDirectoryName(item.Path);
                    var parentIsVolumeRoot = parent is not null && Path.GetDirectoryName(parent) is null;

                    var decision = pruning.Evaluate(item.Path, name, item.Depth, item.ParentIsProject, parentIsVolumeRoot);
                    if (decision.ShouldPrune)
                    {
                        Interlocked.Increment(ref pruned);
                        prunedByReason.AddOrUpdate(decision.Reason ?? "other", 1, (_, v) => v + 1);
                        return;
                    }
                }

                if (name.StartsWith("apache-maven-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("gradle-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("jdk-", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("node-v", StringComparison.OrdinalIgnoreCase))
                    toolchainCandidates.Add(item.Path);

                List<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(item.Path).EnumerateFileSystemInfos().ToList();
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException
                                               or IOException or PathTooLongException)
                {
                    Interlocked.Increment(ref errors);
                    unavailable.Add(item.Path);
                    diagnostics.Add(Diagnostic.Warn("read-error",
                        $"{item.Path}: {ex.GetType().Name}"));
                    return;
                }

                var discovered = new DiscoveredDirectory { Path = item.Path, ParentIsProject = item.ParentIsProject };
                var isProjectCandidate = false;
                var isBinDirectory = item.Depth > 0 && name.Equals("bin", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch
                    {
                        continue; // broken link, racing deletion
                    }

                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

                    if (!isDirectory && !isReparsePoint && isBinDirectory)
                    {
                        if (ToolchainBinaryNames.Contains(entry.Name) &&
                            Path.GetDirectoryName(item.Path) is { } installRoot)
                            toolchainCandidates.Add(installRoot);
                    }
                    else if (!isDirectory && !isReparsePoint && OperatingSystem.IsWindows() &&
                             entry.Name.Equals("node.exe", StringComparison.OrdinalIgnoreCase))
                        toolchainCandidates.Add(item.Path);

                    // Junctions, symlinks and cloud placeholders are never descended.
                    if (isDirectory && isReparsePoint && !options.FollowSymlinks) continue;

                    if (!MarkerCatalog.TryClassify(entry.Name, out _, out var strong)) continue;

                    if (isDirectory)
                    {
                        if (MarkerCatalog.MarkerDirectories.Contains(entry.Name))
                            discovered.MarkerDirectories.Add(entry.Name);
                        continue;
                    }

                    discovered.MarkerFiles.Add(entry.Name);
                    if (strong) discovered.HasStrongMarker = true;
                    if (strong) isProjectCandidate = true;
                }

                if (isProjectCandidate) candidates.Add(discovered);

                // Descend. A child of a project is treated as a build output until proven otherwise.
                if (item.Depth >= options.MaxDepth && !discovered.HasWeakMarker) return;

                var childDepth = item.Depth + 1;
                if (childDepth > options.MaxDepth + options.MaxDepthExtension) return;

                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = entry.Attributes; }
                    catch { continue; }

                    if ((attributes & FileAttributes.Directory) == 0) continue;
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && !options.FollowSymlinks) continue;

                    next.Add((Path.Combine(item.Path, entry.Name), childDepth, isProjectCandidate));
                }
            });

            level = next.ToList();
        }

        result.Candidates.AddRange(candidates);
        result.Diagnostics.AddRange(diagnostics);
        result.UnavailablePaths.AddRange(unavailable);
        result.ToolchainCandidates.AddRange(toolchainCandidates);
        result.Stats.DirsVisited = visited;
        result.Stats.DirsPruned = pruned;
        result.Stats.Errors = errors;
        result.Stats.PrunedByReason = prunedByReason.ToDictionary(kv => kv.Key, kv => kv.Value);
        result.Stats.Mode = "walk";
        result.Stats.Partial = cancellationToken.IsCancellationRequested || errors > 0 || result.RootsMissing.Count > 0;
        return result;
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            var expanded = path.StartsWith("~/", StringComparison.Ordinal) || path == "~"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
                : path;

            return Path.GetFullPath(expanded);
        }
        catch
        {
            return null;
        }
    }
}
