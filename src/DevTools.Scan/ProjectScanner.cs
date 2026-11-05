using DevTools.Core.Model;
using DevTools.Scan.Parsers;

namespace DevTools.Scan;

/// <summary>
/// Turns walk candidates into project records: runs the parsers, scores confidence,
/// reads git metadata, and preserves identity across rescans.
/// </summary>
public sealed class ProjectScanner
{
    private readonly IReadOnlyList<IProjectParser> _parsers;

    public ProjectScanner(IEnumerable<IProjectParser>? parsers = null)
    {
        _parsers = (parsers ?? Defaults()).ToList();
    }

    public static IEnumerable<IProjectParser> Defaults() =>
    [
        new NodeParser(),
        new DenoParser(),
        new JavaParser(),
        new DotNetParser(),
        new PythonParser(),
        new GoParser(),
        new RustParser(),
        new RubyParser(),
        new PhpParser(),
        new DartParser(),
        new VersionPinParser(),
        new ElixirParser(),
        new ScalaParser(),
        new ZigParser(),
        new TerraformParser(),
        new JuliaParser(),
        new CppBuildParser(),
        new BazelParser(),
        new PantsParser(),
        new MillParser(),
        new ClojureParser(),
        new CiParser(),
        new EcosystemOnlyParser(Ecosystem.Nix, "nix"),
        new EcosystemOnlyParser(Ecosystem.Pascal, "pascal"),
        new SwiftParser(),
        new CrystalParser(),
        new GleamParser(),
        new NimParser(),
        new RParser(),
        new EcosystemOnlyParser(Ecosystem.Haskell, "haskell"),
        new EcosystemOnlyParser(Ecosystem.OCaml, "ocaml"),
        new EcosystemOnlyParser(Ecosystem.Erlang, "erlang"),
        new EcosystemOnlyParser(Ecosystem.D, "d"),
        new EcosystemOnlyParser(Ecosystem.Perl, "perl"),
        new EcosystemOnlyParser(Ecosystem.Lua, "lua")
    ];

    public List<ProjectRecord> Build(
        WalkResult walk,
        ScanOptions options,
        IEnumerable<ProjectRecord>? previous = null,
        CancellationToken cancellationToken = default)
    {
        var previousByKey = new Dictionary<string, ProjectRecord>(StringComparer.Ordinal);
        if (previous is not null)
        {
            foreach (var record in previous)
                previousByKey[record.Key] = record;
        }

        var projects = new List<ProjectRecord>(walk.Candidates.Count);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in walk.Candidates)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var key = MakeKey(candidate.Path);
            if (!seenPaths.Add(key)) continue;

            var (score, suppressedReason) = Confidence.Evaluate(candidate);
            var context = new ProjectContext
            {
                Directory = candidate.Path,
                Fs = new PhysicalProjectFileSystem(candidate.Path)
            };

            if (score >= Confidence.Threshold)
            {
                var present = new HashSet<Ecosystem>();
                foreach (var file in candidate.MarkerFiles)
                {
                    // Weak markers (README, .gitignore) say "this is a project" but never
                    // "this is a Node project" — only strong markers establish an ecosystem.
                    if (!MarkerCatalog.Strong.ContainsKey(file)) continue;
                    if (MarkerCatalog.TryClassify(file, out var ecosystem, out _))
                        present.Add(ecosystem);
                }

                foreach (var parser in _parsers)
                {
                    // Running every parser for every project would tag all of them with all
                    // ecosystems. Only parsers whose ecosystem is actually present, plus the
                    // multi-tool pin parser, may contribute.
                    var relevant = parser.Id is "pins" or "ci" || present.Contains(parser.Ecosystem) ||
                                   (parser.Ecosystem == Ecosystem.Container && present.Contains(Ecosystem.Container));

                    if (!relevant) continue;

                    try
                    {
                        parser.Extract(context);
                    }
                    catch (Exception ex)
                    {
                        context.Warn("parser-failed", $"{parser.Id}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            var record = new ProjectRecord
            {
                Key = key,
                Path = candidate.Path,
                RealPath = ResolveRealPath(candidate.Path),
                Name = context.Name,
                Confidence = score,
                State = score >= Confidence.Threshold ? ProjectState.Active : ProjectState.Suppressed,
                SuppressedReason = suppressedReason,
                Ecosystems = BuildEcosystems(candidate, context),
                Requirements = context.Requirements,
                Frameworks = score >= Confidence.Threshold ? DetectFrameworks(context) : [],
                Diagnostics = context.Diagnostics,
                Markers = candidate.MarkerFiles
                    .Select(f => new MarkerHit
                    {
                        RelPath = f,
                        Strong = MarkerCatalog.Strong.ContainsKey(f),
                        Ecosystem = MarkerCatalog.TryClassify(f, out var eco, out _) ? eco : null
                    })
                    .Concat(candidate.MarkerDirectories.Select(d => new MarkerHit { RelPath = d, Strong = false }))
                    .ToList(),
                FirstSeenAt = DateTimeOffset.Now,
                LastSeenAt = DateTimeOffset.Now
            };

            if (options.ReadGitMetadata)
            {
                var git = GitInfo.Read(candidate.Path, context.Fs);
                record.GitRemote = git.Remote;
                record.GitBranch = git.Branch;
                record.LastCommitAt = git.LastCommitAt;

                if (git.Remote is not null && record.Name is null)
                    record.Name = git.Remote.Split('/').LastOrDefault();
            }

            record.LastActivityAt = Max(record.LastCommitAt, TryGetWriteTime(candidate.Path));

            if (options.MeasureSizes)
            {
                var (total, artifacts) = SizeProbe.Measure(candidate.Path, cancellationToken: cancellationToken);
                record.SizeBytes = total;
                record.ArtifactBytes = artifacts;
            }

            // Identity across rescans: first-seen date and user intent survive.
            if (previousByKey.TryGetValue(key, out var prior))
            {
                record.FirstSeenAt = prior.FirstSeenAt;
                if (prior.State == ProjectState.Ignored) record.State = ProjectState.Ignored;

                // Names are cheap to re-derive; only keep a remembered one when it is still
                // a plausible name, otherwise template junk propagates forever.
                if (record.Name is null && prior.Name is not null)
                    record.Name = ParserText.SanitizeName(prior.Name);
            }

            projects.Add(record);
        }

        ApplyInheritance(projects);

        return projects;
    }

    /// <summary>
    /// Workspace inheritance: a child that declares nothing still builds with the parent's
    /// pinned SDK (.nvmrc at the repo root, global.json at the solution root). Only pins are
    /// inherited, only from the nearest ancestor, and only when the child has no opinion of
    /// its own — otherwise a stray root pin would silently override real project metadata.
    /// </summary>
    private static List<FrameworkUse> DetectFrameworks(ProjectContext context) => [.. FrameworkDetector.Detect(context)];

    private static void ApplyInheritance(List<ProjectRecord> projects)
    {
        var active = projects
            .Where(p => p.State == ProjectState.Active)
            .OrderBy(p => p.Path.Length)
            .ToList();

        var separator = Path.DirectorySeparatorChar;

        foreach (var project in active)
        {
            ProjectRecord? parent = null;
            var bestLength = 0;

            foreach (var candidate in active)
            {
                if (ReferenceEquals(candidate, project)) continue;
                if (candidate.Path.Length <= bestLength) continue;
                if (!project.Path.StartsWith(candidate.Path + separator, StringComparison.OrdinalIgnoreCase)) continue;

                parent = candidate;
                bestLength = candidate.Path.Length;
            }

            if (parent is null) continue;

            foreach (var inherited in parent.Requirements.Where(r => r.Strength == RequirementStrength.Pinned))
            {
                if (project.Requirements.Any(r => r.Tool == inherited.Tool)) continue;

                project.Requirements.Add(new Requirement
                {
                    Tool = inherited.Tool,
                    Raw = inherited.Raw,
                    Strength = RequirementStrength.Declared,
                    Precedence = 3,
                    Provenance = new Provenance
                    {
                        Kind = ProvenanceKind.Inferred,
                        FilePath = inherited.Provenance.FilePath,
                        Note = "inherited from " + parent.DisplayName
                    }
                });
            }
        }
    }

    private static List<Ecosystem> BuildEcosystems(DiscoveredDirectory candidate, ProjectContext context)
    {
        var ecosystems = context.Ecosystems.ToList();

        if (ecosystems.Count == 0)
        {
            foreach (var file in candidate.MarkerFiles)
            {
                if (MarkerCatalog.TryClassify(file, out var eco, out _) && !ecosystems.Contains(eco))
                    ecosystems.Add(eco);
            }
        }

        return ecosystems
            .Where(e => e != Ecosystem.Container)
            .OrderBy(e => e)
            .ToList();
    }

    internal static string MakeKey(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized;
    }

    private static string ResolveRealPath(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch
        {
            return path;
        }
    }

    private static DateTimeOffset? TryGetWriteTime(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); }
        catch { return null; }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
