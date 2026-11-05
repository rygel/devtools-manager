namespace DevTools.Scan;

/// <summary>
/// Decides whether a directory is really a project. Presence of a marker is necessary but
/// not sufficient: every fixture directory in every repo has a <c>package.json</c>.
/// </summary>
public static class Confidence
{
    public const double Threshold = 0.5;

    private static readonly HashSet<string> FixtureSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "fixtures", "__fixtures__", "testdata", "test-data", "testData".ToLowerInvariant(), "mocks"
    };

    private static readonly HashSet<string> ExampleSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "examples", "example", "samples", "sample", "demo", "demos", "playground", "sandbox", "scratch"
    };

    private static readonly HashSet<string> Lockfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock",
        "poetry.lock", "uv.lock", "Cargo.lock", "go.sum", "Pipfile.lock", "composer.lock"
    };

    public static (double Score, string? SuppressedReason) Evaluate(DiscoveredDirectory directory)
    {
        var score = 0.60;

        if (directory.MarkerFiles.Any(f => Lockfiles.Contains(f))) score += 0.15;
        if (directory.MarkerDirectories.Contains(".git")) score += 0.15;
        if (directory.MarkerDirectories.Contains(".github")) score += 0.05;

        string? reason = null;

        var segments = directory.Path
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        // The containing directory decides most false positives.
        if (segments.Length >= 2 && FixtureSegments.Contains(segments[^2]))
        {
            score -= 0.40;
            reason = "inside a fixtures/testdata directory";
        }
        else if (segments.Any(FixtureSegments.Contains))
        {
            score -= 0.40;
            reason = "path contains a fixtures/testdata segment";
        }
        else if (segments.Length >= 2 && ExampleSegments.Contains(segments[^2]))
        {
            score -= 0.25;
            reason = "inside an examples/samples directory";
        }

        if (directory.MarkerFiles.Count == 1 && directory.MarkerFiles[0].Equals("Makefile", StringComparison.OrdinalIgnoreCase))
        {
            score -= 0.15;
            reason ??= "only a Makefile is present";
        }

        if (score >= Threshold) return (score, null);

        return (score, reason ?? "insufficient project signals");
    }
}

/// <summary>Measures a project's source and artifact bytes. Opt-in: this is the slowest part of a scan.</summary>
public static class SizeProbe
{
    private static readonly HashSet<string> ArtifactDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "target", "bin", "obj", "dist", "out", "build", ".gradle", ".next", "coverage"
    };

    public static (long Total, long Artifacts) Measure(string projectPath, int maxDepth = 8, CancellationToken cancellationToken = default)
    {
        long total = 0;
        long artifacts = 0;

        var stack = new Stack<(string Path, int Depth, bool InArtifact)>();
        stack.Push((projectPath, 0, false));

        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var (path, depth, inArtifact) = stack.Pop();
            if (depth > maxDepth) continue;

            try
            {
                foreach (var file in new DirectoryInfo(path).EnumerateFiles())
                {
                    var size = file.Length;
                    total += size;
                    if (inArtifact) artifacts += size;
                }

                foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
                {
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                    var isArtifact = inArtifact || ArtifactDirs.Contains(directory.Name);
                    if (directory.Name is ".git" or ".svn" or ".hg") continue;

                    stack.Push((directory.FullName, depth + 1, isArtifact));
                }
            }
            catch
            {
                // Unreadable subtree: contributes nothing rather than failing the measurement.
            }
        }

        return (total, artifacts);
    }
}
