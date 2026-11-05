using System.Diagnostics;
using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Providers;
using DevTools.Scan;
using DevTools.Store;

namespace DevTools.Cli;

internal static class Commands
{
    private static SqliteStore Store() => new(PlatformPaths.ApplicationData);

    public static int Snapshot()
    {
        var store = new SqliteStore(PlatformPaths.ApplicationData, createDirectory: false);
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();
        snapshot.ConfiguredRoots = store.LoadConfig(createIfMissing: false).Roots;
        JsonOut.Write(snapshot);
        return 0;
    }

    public static int Environments(Args args)
    {
        if (args.Value("load") is { } distribution)
        {
            if (!OperatingSystem.IsWindows()) return Usage("Loading WSL indexes requires Windows.");
            var service = new WslSnapshots();
            if (args.Value("cleanup-plan") is { } cleanupProject)
            {
                var (targets, cleanupError) = service.CleanupPlan(distribution, cleanupProject);
                if (cleanupError is not null) { Console.Error.WriteLine(cleanupError); return 1; }
                JsonOut.Write(targets);
                return 0;
            }
            if (args.Value("cleanup") is { } cleanupPath)
            {
                var artifact = args.Value("artifact");
                var token = args.Value("plan");
                if (!args.Has("yes") || artifact is null || token is null || !ArtifactCleanup.IsKnownName(artifact))
                    return Usage("--cleanup requires --artifact NAME --plan TOKEN --yes");
                var target = new WslCleanupTarget(cleanupPath.TrimEnd('/') + "/" + artifact, null, token, true);
                var (success, message) = service.Cleanup(distribution, cleanupPath, target);
                if (!success) { Console.Error.WriteLine(message); return 1; }
                Out.Line(message);
                return 0;
            }
            if (args.Has("scan"))
            {
                if (!args.Has("yes")) return Usage("Add --yes to rescan the selected WSL distribution's configured roots.");
                var (current, currentError) = service.Load(distribution);
                if (current is null) { Console.Error.WriteLine(currentError); return 1; }
                if (current.ConfiguredRoots.Count == 0) return Usage("No WSL roots are configured; add them inside the distribution first.");
                var (succeeded, message) = service.Scan(distribution, args.Has("measure"), args.Has("no-probe"));
                if (!succeeded) { Console.Error.WriteLine(message); return 1; }
            }
            var (snapshot, loadError) = service.Load(distribution);
            if (snapshot is null) { Console.Error.WriteLine(loadError); return 1; }
            JsonOut.Write(snapshot);
            return 0;
        }
        IReadOnlyList<string> wsl = [];
        string? error = null;
        if (OperatingSystem.IsWindows()) (wsl, error) = new WslSnapshots().List();
        if (args.Json) JsonOut.Write(new { local = IndexSnapshot.CurrentEnvironmentName, wsl, error });
        else
        {
            Out.Line("  " + IndexSnapshot.CurrentEnvironmentName + " (local)");
            foreach (var name in wsl) Out.Line("  WSL: " + name + " (index not checked)");
            if (!string.IsNullOrEmpty(error)) Out.Line("  WSL discovery: " + error);
        }
        return error is null ? 0 : 1;
    }

    // ---------------------------------------------------------------- scan

    public static async Task<int> Scan(Args args)
    {
        var store = Store();
        using var scanLock = store.TryAcquireScanLock();
        if (scanLock is null)
        {
            Out.Line(Out.RedText("Another scan is already using this index."));
            return 2;
        }
        var config = store.LoadConfig();
        var previous = store.Load();

        var roots = args.Positional.Count > 0
            ? args.Positional.Select(PlatformPaths.Expand).ToList()
            : config.Roots.ToList();

        if (roots.Count == 0)
        {
            roots = StoreConfig.WithDefaults().Roots.ToList();
            if (roots.Count == 0)
            {
                Out.Line(Out.RedText("No scan roots configured."));
                Out.Line("Add one:  " + Out.BoldText("devtools roots add C:\\Develop"));
                return 2;
            }
        }

        var request = new ScanRequest
        {
            Roots = roots,
            MaxDepth = args.GetNumber("depth", config.MaxDepth),
            IncludeInventory = !args.Has("no-inventory"),
            AllowProbing = !args.Has("no-probe"),
            MeasureSizes = args.Has("measure"),
            MeasureToolchains = args.Has("measure"),
            Excludes = config.Excludes,
            FollowSymlinks = args.Has("follow-symlinks")
        };

        if (!args.Json)
        {
            Out.Line();
            Out.Line("  " + Out.DimText("roots") + "  " + string.Join(", ", roots));
            Out.Line("  " + Out.DimText("mode") + "   walk · inventory " +
                     (request.IncludeInventory ? "on" : "off") +
                     (request.MeasureSizes ? " · measuring sizes" : ""));
            Out.Line();
        }

        var stopwatch = Stopwatch.StartNew();
        var snapshot = await new ScanService().ScanAsync(request, previous);
        stopwatch.Stop();

        store.Save(snapshot);

        config.Roots = roots;
        config.Initialised = true;
        config.MaxDepth = request.MaxDepth;
        store.SaveConfig(config);

        var view = MachineView.Build(snapshot);

        if (args.Json)
        {
            JsonOut.Write(new
            {
                scannedAt = snapshot.ScannedAt,
                roots = snapshot.Roots,
                stats = snapshot.Stats,
                projects = snapshot.Projects.Count,
                active = view.Stats.ActiveProjects,
                suppressed = view.Stats.SuppressedProjects,
                withProblems = view.Stats.ProjectsWithProblems,
                toolchains = snapshot.Toolchains.Count,
                ecosystems = view.Stats.ProjectsByEcosystem,
                missing = view.Stats.Missing.Select(m => new { tool = ToolKinds.Display(m.Tool), m.Constraint, projects = m.Projects.Count })
            });
            return 0;
        }

        var stats = snapshot.Stats;
        Out.Line("  " + Out.DimText("visited") + "   " + stats.DirsVisited.ToString("N0") + " directories in " +
                 FormatDuration(stopwatch.Elapsed));
        Out.Line("  " + Out.DimText("pruned") + "    " + stats.DirsPruned.ToString("N0") + "  " +
                 Out.DimText(string.Join(" · ", stats.PrunedByReason.OrderByDescending(kv => kv.Value).Take(4)
                     .Select(kv => $"{kv.Key} {kv.Value:N0}"))));

        if (stats.Errors > 0)
            Out.Line("  " + Out.DimText("skipped") + "   " + stats.Errors + " unreadable");
        if (stats.Partial)
            Out.Line(Out.YellowText("  incomplete scan — previous records for unavailable paths were retained as stale"));

        Out.Line();
        Out.Line("  " + Out.BoldText(stats.ProjectsFound + " projects") +
                 (stats.ProjectsSuppressed > 0 ? Out.DimText($"  ({stats.ProjectsSuppressed} suppressed)") : ""));

        foreach (var (ecosystem, count) in view.Stats.ProjectsByEcosystem.OrderByDescending(kv => kv.Value))
            Out.Line("    " + ecosystem.PadRight(8) + " " + count);

        Out.Line();
        Out.Line("  " + Out.DimText("toolchains") + "  " + snapshot.Toolchains.Count + " installed" +
                 (view.Stats.Missing.Count > 0
                     ? "  ·  " + Out.YellowText(view.Stats.ProjectsWithProblems + " projects need something you don't have")
                     : "  ·  " + Out.GreenText("every requirement satisfied")));

        if (view.Stats.Missing.Count > 0)
        {
            Out.Line();
            foreach (var missing in view.Stats.Missing.Take(6))
            {
                Out.Line("    " + Out.RedText("✘") + " " + ToolKinds.Display(missing.Tool).PadRight(7) +
                         " " + missing.Constraint.PadRight(16) +
                         Out.DimText("← " + string.Join(", ", missing.Projects.Take(4)) +
                                  (missing.Projects.Count > 4 ? $" +{missing.Projects.Count - 4}" : "")));
            }

            Out.Line();
            Out.Line("  " + Out.DimText("next") + "  devtools doctor     or     devtools install");
        }

        Out.Line();
        return 0;
    }

    // ---------------------------------------------------------------- list

    public static int List(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        IEnumerable<ProjectView> projects = view.Projects;

        if (!args.Has("all")) projects = projects.Where(p => p.Project.State == ProjectState.Active);

        if (args.Value("tool") is { } toolText && ToolKinds.TryParse(toolText, out var tool))
            projects = projects.Where(p => p.Project.Requirements.Any(r => r.Tool == tool));

        if (args.Value("ecosystem") is { } ecosystemText)
            projects = projects.Where(p => p.Project.Ecosystems.Any(e =>
                string.Equals(ToolKinds.DisplayEcosystem(e), ecosystemText, StringComparison.OrdinalIgnoreCase)));

        if (args.Value("framework") is { } frameworkText)
            projects = projects.Where(p => p.Project.Frameworks.Any(f =>
                string.Equals(f.Id, frameworkText, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.Name, frameworkText, StringComparison.OrdinalIgnoreCase)));

        projects = args.Value("status") switch
        {
            "missing" => projects.Where(p => p.HasProblems),
            "ok" => projects.Where(p => !p.HasProblems),
            "problem" => projects.Where(p => p.HasProblems),
            _ => projects
        };

        if (int.TryParse(args.Value("stale"), out var staleDays))
        {
            var cutoff = DateTimeOffset.Now.AddDays(-staleDays);
            projects = projects.Where(p => (p.Project.LastActivityAt ?? DateTimeOffset.MinValue) < cutoff);
        }

        var list = projects.ToList();

        if (args.Json)
        {
            JsonOut.Write(list.Select(p => new
            {
                name = p.Project.DisplayName,
                path = p.Project.Path,
                ecosystems = p.Project.Ecosystems.Select(ToolKinds.DisplayEcosystem),
                state = p.Project.State,
                stale = p.Project.Stale,
                incomplete = p.Project.Incomplete,
                confidence = Math.Round(p.Project.Confidence, 2),
                lastActivity = p.Project.LastActivityAt,
                frameworks = FrameworkJson(p.Project),
                tools = p.Resolution.Tools.Select(t => new
                {
                    tool = ToolKinds.Display(t.Tool),
                    required = t.ConstraintDisplay,
                    status = t.Status,
                    installed = t.Toolchain?.Version,
                    from = t.Requirement?.Provenance.Describe()
                })
            }));
            return 0;
        }

        var table = new Table("PROJECT", "ECOSYSTEMS", "TOOLCHAINS", "FRAMEWORKS", "STATUS", "LAST", "PATH");

        foreach (var project in list.Take(args.GetNumber("limit", 60)))
        {
            var tools = string.Join(" ", project.Resolution.Tools
                .Take(4)
                .Select(t => ToolKinds.Display(t.Tool) + (t.Toolchain?.Version ?? "")));

            table.Add(
                Out.BoldText(Truncate(project.Project.DisplayName, 26)),
                Format.Environments(project.Project.Ecosystems).PadRight(12),
                Truncate(tools, 26),
                Truncate(string.Join(", ", project.Project.Frameworks.Select(f => f.Name)), 24),
                project.Project.Stale ? Out.YellowText("stale scan") : project.Project.Incomplete ? Out.YellowText("incomplete") : project.Resolution.Tools.Count == 0
                    ? Out.DimText("—")
                    : project.HasProblems
                        ? Out.RedText($"{project.Resolution.ProblemCount} issue{(project.Resolution.ProblemCount == 1 ? "" : "s")}")
                        : Out.GreenText("ok"),
                Out.DimText(Format.Relative(project.Project.LastActivityAt)),
                Out.DimText(Format.Path(project.Project.Path, 40)));
        }

        table.Write();
        Out.Line();
        Out.Line(Out.DimText($"{list.Count} project{(list.Count == 1 ? "" : "s")}" +
                          (list.Count > 60 ? "  (showing 60, use --limit)" : "")));
        return 0;
    }

    // ---------------------------------------------------------------- show

    public static int Show(Args args)
    {
        if (args.Positional.Count == 0) return Usage("devtools show <path|name>");

        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var project = FindProject(view, args.Positional[0]);

        if (project is null)
        {
            Out.Line(Out.RedText("No project matching ") + args.Positional[0]);
            return 1;
        }

        if (args.Json)
        {
            JsonOut.Write(new
            {
                project = project.Project,
                frameworks = FrameworkJson(project.Project),
                resolution = project.Resolution,
                tools = project.Resolution.Tools.Select(t => new
                {
                    tool = ToolKinds.Display(t.Tool),
                    required = t.ConstraintDisplay,
                    status = t.Status,
                    installed = t.Toolchain?.Version,
                    nearest = t.Nearest?.Version,
                    detail = t.Detail,
                    provenance = t.Requirement?.Provenance,
                    strength = t.Requirement?.Strength.ToString()
                })
            });
            return project.HasProblems ? 1 : 0;
        }

        var record = project.Project;

        Out.Line();
        Out.Line("  " + Out.BoldText(record.DisplayName) + "   " + Out.DimText(Format.Path(record.Path, 70)));
        if (record.Stale) Out.Line(Out.YellowText("  Stale: the project was unavailable during the last scan."));
        if (record.Incomplete) Out.Line(Out.YellowText("  Incomplete: one or more declarations could not be interpreted; see diagnostics."));
        if (record.GitRemote is not null)
            Out.Line("  " + Out.DimText("git") + "     " + record.GitRemote +
                     (record.GitBranch is null ? "" : "  (" + record.GitBranch + ")") +
                     "   " + Out.DimText(Format.Relative(record.LastCommitAt)));
        Out.Line("  " + Out.DimText("last") + "    " + Format.Relative(record.LastActivityAt) +
                 (record.SizeBytes is not null ? "   " + HumanBytes.Format(record.SizeBytes) +
                     (record.ArtifactBytes is > 0 ? Out.DimText(" (" + HumanBytes.Format(record.ArtifactBytes) + " artifacts)") : "") : ""));
        Out.Line();

        if (project.Resolution.Tools.Count == 0)
        {
            Out.Line("  " + Out.DimText(record.Incomplete ? "Requirements could not be fully determined." : "No toolchain requirements were declared in this project."));
            foreach (var diagnostic in record.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info))
                Out.Line("  " + Out.YellowText(diagnostic.Code + ": ") + diagnostic.Message);
            PrintFrameworks(record);
            Out.Line();
            return 0;
        }

        Out.Line("  " + Out.DimText("REQUIREMENT".PadRight(16) + "VERSION".PadRight(18) + "SOURCE".PadRight(38) + "STATUS"));
        foreach (var tool in project.Resolution.Tools)
        {
            var status = tool.Toolchain is not null
                ? Out.GreenText("✔ available " + tool.Toolchain.Version +
                             (tool.Toolchain.Manager is null ? "" : " (" + tool.Toolchain.Manager + ")")) +
                  (tool.Detail is null ? "" : Out.DimText("  · " + tool.Detail))
                : tool.Status switch
                {
                    ResolutionStatus.Unknown => Out.YellowText("? " + (tool.Detail ?? "unknown")),
                    ResolutionStatus.Missing => Out.RedText("✘ missing"),
                    ResolutionStatus.UnsatisfiedRange => Out.RedText("✘ " + (tool.Nearest is null ? "no match" : "nearest " + tool.Nearest.Version)),
                    _ => Out.DimText(tool.Status.ToString().ToLowerInvariant())
                };

            Out.Line("  " + ToolKinds.Display(tool.Tool).PadRight(16) +
                     Truncate(tool.ConstraintDisplay, 16).PadRight(18) +
                     Truncate(tool.Requirement?.Provenance.Describe() ?? "", 36).PadRight(38) +
                     status);
        }

        PrintFrameworks(record);

        if (args.Has("explain"))
        {
            Out.Line();
            Out.Line("  " + Out.DimText("explanation"));
            foreach (var tool in project.Resolution.Tools)
            {
                var requirement = tool.Requirement;
                if (requirement is null) continue;

                Out.Line("    " + ToolKinds.Display(tool.Tool) + " " + tool.ConstraintDisplay +
                         " ← " + requirement.Strength.ToString().ToLowerInvariant() + " " +
                         requirement.Provenance.Describe());

                if (requirement.Provenance.Note is { } note)
                    Out.Line("        " + Out.DimText(note));

                foreach (var superseded in tool.Superseded)
                {
                    Out.Line("        " + Out.DimText("superseded: " + superseded.Display + " from " +
                                                   superseded.Provenance.Describe()));
                }

                if (tool.Diagnostics.Count > 0)
                {
                    foreach (var diagnostic in tool.Diagnostics)
                        Out.Line("        " + Out.YellowText("⚠ " + diagnostic.Message));
                }
            }
        }

        foreach (var diagnostic in record.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Info) continue;
            Out.Line("  " + Out.YellowText("⚠ ") + diagnostic.Message +
                     (diagnostic.FilePath is null ? "" : Out.DimText("  (" + diagnostic.FilePath +
                         (diagnostic.Line is > 0 ? ":" + diagnostic.Line : "") + ")")));
        }

        Out.Line();
        return project.HasProblems ? 1 : 0;
    }

    // ---------------------------------------------------------------- frameworks

    public static int Frameworks(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();
        var groups = snapshot.Projects
            .Where(p => p.State == ProjectState.Active && !p.Stale)
            .SelectMany(p => p.Frameworks.Select(f => (Project: p, Framework: f)))
            .GroupBy(x => x.Framework.Id)
            .Select(g => (Id: g.Key, First: g.First().Framework, Projects: g.Select(x => x.Project.DisplayName).Distinct().Count(),
                Versions: g.Select(x => x.Framework.Version ?? "undeclared").Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.Projects).ThenBy(g => g.First.Name, StringComparer.Ordinal)
            .ToList();

        if (args.Json)
        {
            JsonOut.Write(groups.Select(g => new
            {
                id = g.Id,
                name = g.First.Name,
                ecosystem = ToolKinds.DisplayEcosystem(g.First.Ecosystem),
                projects = g.Projects,
                versions = g.Versions
            }));
            return 0;
        }

        if (groups.Count == 0)
        {
            Out.Line("  " + Out.DimText("No frameworks were found in the indexed projects. Rescan after adding projects, or see ECOSYSTEMS.md for what is recognised."));
            return 0;
        }

        var table = new Table("FRAMEWORK", "ECOSYSTEM", "PROJECTS", "DECLARED VERSIONS");
        foreach (var group in groups)
            table.Add(Out.BoldText(group.First.Name), ToolKinds.DisplayEcosystem(group.First.Ecosystem), group.Projects.ToString(),
                Truncate(string.Join(", ", group.Versions.Take(4)) + (group.Versions.Count > 4 ? ", …" : ""), 60));
        table.Write();
        Out.Line();
        Out.Line(Out.DimText("Frameworks are declared by projects, not installed. For installed tools use `devtools toolchains`."));
        return 0;
    }

    private static IEnumerable<object> FrameworkJson(ProjectRecord project) => project.Frameworks.Select(f => new
    {
        id = f.Id,
        name = f.Name,
        ecosystem = ToolKinds.DisplayEcosystem(f.Ecosystem),
        version = f.Version,
        from = f.Provenance.Describe()
    });

    /// <summary>Frameworks are what the project is built with, not what is installed: no status, no exit code.</summary>
    private static void PrintFrameworks(ProjectRecord record)
    {
        if (record.Frameworks.Count == 0) return;
        Out.Line();
        Out.Line("  " + Out.DimText("FRAMEWORK".PadRight(20) + "DECLARED".PadRight(18) + "SOURCE"));
        foreach (var framework in record.Frameworks)
            Out.Line("  " + framework.Name.PadRight(20) + Truncate(framework.Version ?? "—", 16).PadRight(18) +
                     Out.DimText(Truncate(framework.Provenance.Describe(), 44)));
        Out.Line("  " + Out.DimText("Frameworks are declared by the project. They are not installed or managed by devtools."));
    }

    // ---------------------------------------------------------------- doctor

    public static int Doctor(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);

        if (args.Positional.Count > 0)
        {
            var exit = Show(new ArgsBuilder(args).Positional(args.Positional[0]).Build());
            return exit;
        }

        if (args.Json)
        {
            JsonOut.Write(new
            {
                stats = view.Stats,
                missing = view.Stats.Missing.Select(m => new
                {
                    tool = ToolKinds.Display(m.Tool),
                    required = m.Constraint,
                    projects = m.Projects
                }),
                conflicts = view.Conflicts.Select(c => new { c.Tool, c.VersionA, c.VersionB, c.ProjectA, c.ProjectB }),
                problems = view.Projects.Where(p => p.HasProblems).Select(p => new
                {
                    name = p.Project.DisplayName,
                    path = p.Project.Path,
                    problems = p.Resolution.Tools.Where(t => t.IsProblem)
                        .Select(t => new { tool = ToolKinds.Display(t.Tool), required = t.ConstraintDisplay, status = t.Status })
                })
            });
            return view.Stats.ProjectsWithProblems > 0 ? 1 : 0;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("machine health"));
        Out.Line("  " + Out.DimText("projects") + "    " + view.Stats.ActiveProjects + " active" +
                 (view.Stats.SuppressedProjects > 0 ? Out.DimText(", " + view.Stats.SuppressedProjects + " suppressed") : ""));
        Out.Line("  " + Out.DimText("toolchains") + "  " + view.Stats.ToolchainsInstalled + " installed, " +
                 view.Stats.UnusedToolchains + " unused");
        Out.Line("  " + Out.DimText("health") + "      " +
                 (view.Stats.ProjectsWithProblems == 0
                     ? Out.GreenText("all requirements satisfied")
                     : Out.RedText(view.Stats.ProjectsWithProblems + " of " + view.Stats.ActiveProjects + " projects have problems")));

        if (view.Stats.Missing.Count > 0)
        {
            Out.Line();
            Out.Line("  " + Out.BoldText("missing toolchains"));
            foreach (var missing in view.Stats.Missing)
            {
                Out.Line("    " + Out.RedText("✘") + " " + ToolKinds.Display(missing.Tool).PadRight(8) +
                         missing.Constraint.PadRight(18) + Out.DimText(string.Join(", ", missing.Projects.Take(5)) +
                             (missing.Projects.Count > 5 ? $" +{missing.Projects.Count - 5}" : "")));
            }

            Out.Line();
            Out.Line("  " + Out.DimText("fix") + "  devtools install            " + Out.DimText("# dry run"));
            Out.Line("       devtools install --apply --yes --plan <plan-token>");
        }

        if (view.Conflicts.Count > 0)
        {
            Out.Line();
            Out.Line("  " + Out.BoldText("conflicting pins"));
            foreach (var conflict in view.Conflicts.Take(10))
                Out.Line("    " + Out.YellowText("⚠") + " " + conflict.Describe());
        }

        var unknown = view.Projects
            .SelectMany(p => p.Resolution.Tools.Select(t => (Project: p, Tool: t)))
            .Where(x => x.Tool.Status == ResolutionStatus.Unknown)
            .Take(5)
            .ToList();

        if (unknown.Count > 0)
        {
            Out.Line();
            Out.Line("  " + Out.BoldText("unreadable declarations"));
            foreach (var (project, tool) in unknown)
            {
                Out.Line("    " + Out.YellowText("?") + " " + project.Project.DisplayName.PadRight(24) +
                         ToolKinds.Display(tool.Tool).PadRight(8) + Out.DimText(tool.Detail ?? ""));
            }
        }

        Out.Line();
        return view.Stats.ProjectsWithProblems > 0 ? 1 : 0;
    }

    // ---------------------------------------------------------------- toolchains

    public static int Toolchains(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var usedPaths = view.Projects
            .SelectMany(p => p.Resolution.Tools)
            .Where(t => t.Toolchain is not null)
            .Select(t => t.Toolchain!.Path)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        IEnumerable<Toolchain> toolchains = snapshot.Toolchains;

        if (args.Value("tool") is { } toolText && ToolKinds.TryParse(toolText, out var tool))
            toolchains = toolchains.Where(t => t.Tool == tool);

        if (args.Has("unused"))
            toolchains = toolchains.Where(t => !usedPaths.Contains(t.Path) && !t.IsGlobalDefault);

        var list = toolchains.ToList();

        if (args.Json)
        {
            JsonOut.Write(list.Select(t => new
            {
                tool = ToolKinds.Display(t.Tool),
                t.Version,
                t.Manager,
                t.Path,
                globalDefault = t.IsGlobalDefault,
                health = t.Health,
                t.Stale,
                t.HealthDetail,
                t.VerifiedAt,
                usedBy = view.Projects.Count(p => p.Resolution.Tools.Any(x =>
                    string.Equals(x.Toolchain?.Path, t.Path, StringComparison.OrdinalIgnoreCase))),
                disk = t.DiskBytes
            }));
            return 0;
        }

        var table = new Table("TOOL", "VERSION", "MANAGER", "USED BY", "DISK", "HEALTH", "PATH");

        Out.Line("  " + Out.DimText("environment") + "  " + snapshot.EnvironmentName);

        foreach (var toolchain in list)
        {
            var usedBy = view.Projects.Count(p => p.Resolution.Tools.Any(x =>
                string.Equals(x.Toolchain?.Path, toolchain.Path, StringComparison.OrdinalIgnoreCase)));

            table.Add(
                ToolKinds.Display(toolchain.Tool).PadRight(8),
                (toolchain.Version + (toolchain.IsGlobalDefault ? Out.GreenText("*") : "")).PadRight(14),
                (toolchain.Manager ?? "—").PadRight(12),
                (usedBy == 0 ? Out.DimText("unused") : usedBy + " project" + (usedBy == 1 ? "" : "s")).PadRight(10),
                HumanBytes.Format(toolchain.DiskBytes).PadRight(8),
                toolchain.Stale ? Out.YellowText("stale") : toolchain.Health == ToolchainHealth.Ok ? Out.GreenText("ok") : Out.YellowText(toolchain.Health.ToString().ToLowerInvariant()),
                Out.DimText(Format.Path(toolchain.Path, 48)));
        }

        table.Write();
        Out.Line();
        Out.Line(Out.DimText($"{list.Count} toolchain{(list.Count == 1 ? "" : "s")}  ·  * = machine default"));
        return 0;
    }

    // ---------------------------------------------------------------- audit

    /// <summary>What changed between the two most recent scans. Reads the local database only.</summary>
    public static int Changes(Args args)
    {
        var store = new SqliteStore(PlatformPaths.ApplicationData, createDirectory: false);
        var changes = store.Changes();
        if (changes is null)
        {
            Out.Line("No scan has been stored yet. Run `devtools scan` first.");
            return 0;
        }

        if (args.Json)
        {
            JsonOut.Write(new
            {
                currentScanAt = changes.CurrentScanAt,
                previousScanAt = changes.PreviousScanAt,
                added = changes.Added,
                removed = changes.Removed,
                changed = changes.Changed
            });
            return 0;
        }

        if (changes.PreviousScanAt is null)
        {
            Out.Line("Only one scan is stored, so there is nothing to compare yet.");
            return 0;
        }

        Out.Line($"Changes from {changes.PreviousScanAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} to {changes.CurrentScanAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (changes.Added.Count + changes.Removed.Count + changes.Changed.Count == 0)
        {
            Out.Line(Out.DimText("  no projects were added, removed or changed"));
            return 0;
        }

        foreach (var project in changes.Added) Out.Line("  " + Out.GreenText("added    ") + project.Name + "  " + Format.Path(project.Path));
        foreach (var project in changes.Removed) Out.Line("  " + Out.RedText("removed  ") + project.Name + "  " + Format.Path(project.Path));
        foreach (var project in changes.Changed) Out.Line("  " + Out.YellowText("changed  ") + project.Name + "  " + Format.Path(project.Path));
        return 0;
    }

    /// <summary>
    /// Vulnerability lookup for one project's locked dependencies. Running it is the consent: it sends each package's
    /// ecosystem, name and version to api.osv.dev and nothing else. Exit code 0 unless --strict and something was found
    /// (or the lookup could not complete, since that is not the same as clean).
    /// </summary>
    public static int Audit(Args args)
    {
        if (args.Positional.Count != 1) return Usage("devtools audit <project-path> [--json] [--strict]");

        var directory = Path.GetFullPath(args.Positional[0]);
        if (!Directory.Exists(directory)) { Console.Error.WriteLine("No such directory: " + directory); return 1; }

        var read = DependencyReader.Read(directory);
        foreach (var error in read.Errors) Console.Error.WriteLine(Out.RedText("audit: ") + error);
        if (read.Dependencies.Count == 0)
        {
            Console.Error.WriteLine("No package-lock.json or Cargo.lock with resolved packages in " + directory +
                ". Only locked versions are checked, because a manifest range does not say what is installed.");
            return read.Errors.Count > 0 ? 2 : 1;
        }

        if (!args.Json)
            Out.Line(Out.DimText($"  sending {read.Dependencies.Count} package names and versions from {string.Join(", ", read.Files)} to api.osv.dev"));

        var result = new OsvClient().Audit(read.Dependencies);
        foreach (var error in result.Errors) Console.Error.WriteLine(Out.RedText("audit: ") + error);

        if (args.Json)
        {
            JsonOut.Write(new
            {
                files = read.Files,
                packagesChecked = result.Checked,
                complete = result.Complete,
                vulnerable = result.Vulnerable.Select(f => new
                {
                    f.Dependency.Ecosystem,
                    f.Dependency.Name,
                    f.Dependency.Version,
                    vulnerabilities = f.Vulnerabilities.Select(v => new { v.Id, v.Summary, v.Severity })
                })
            });
        }
        else
        {
            foreach (var finding in result.Vulnerable.OrderBy(f => f.Dependency.Name, StringComparer.Ordinal))
            {
                Out.Line("  " + Out.BoldText(finding.Dependency.Name) + " " + finding.Dependency.Version + Out.DimText("  (" + finding.Dependency.Ecosystem + ")"));
                foreach (var vulnerability in finding.Vulnerabilities)
                {
                    var severity = vulnerability.Severity is null ? Out.DimText("unrated") : vulnerability.Severity.ToUpperInvariant() is "HIGH" or "CRITICAL" ? Out.RedText(vulnerability.Severity) : Out.YellowText(vulnerability.Severity);
                    Out.Line($"    {vulnerability.Id}  {severity}  {vulnerability.Summary ?? Out.DimText("no summary available")}");
                }
            }

            Out.Line();
            Out.Line(Out.DimText($"{result.Checked} packages checked  ·  {result.Vulnerable.Count} with known vulnerabilities" +
                                 (result.Complete ? "" : "  ·  INCOMPLETE: the errors above mean this is not a clean result")));
        }

        return args.Has("strict") && (!result.Complete || result.Vulnerable.Count > 0) ? 1 : 0;
    }

    // ---------------------------------------------------------------- end of life

    /// <summary>
    /// End-of-life status for installed toolchains. Reading uses only the local cache; --refresh is the one
    /// explicit action that contacts endoflife.date. Informational: it never changes exit codes or project status.
    /// </summary>
    public static int Eol(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        var service = new EndOfLifeService(store.Root);
        if (args.Has("refresh"))
        {
            var tools = snapshot.Toolchains.Select(t => t.Tool).Distinct().ToList();
            if (!args.Json) Out.Line(Out.DimText("  contacting endoflife.date for " + tools.Select(EndOfLifeProducts.For).OfType<string>().Distinct().Count() + " products"));
            var refresh = service.Refresh(tools, DateTimeOffset.UtcNow, force: true);
            foreach (var error in refresh.Errors) Console.Error.WriteLine(Out.RedText("eol: ") + error);
            if (!args.Json) Out.Line(Out.DimText($"  fetched {refresh.Fetched}, failed {refresh.Errors.Count}"));
        }

        var data = service.Load();
        if (data.Count == 0)
        {
            Console.Error.WriteLine("No end-of-life data is cached. Run `devtools eol --refresh` (this contacts endoflife.date).");
            return 1;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        var covered = snapshot.Toolchains.Where(t => EndOfLifeProducts.For(t.Tool) is not null).ToList();
        // An installation whose version is not known cannot be matched to a release line; say so once instead of listing it.
        var unverified = covered.Count(t => string.Equals(t.Version, "unknown", StringComparison.OrdinalIgnoreCase));
        var rows = covered
            .Where(t => !string.Equals(t.Version, "unknown", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => (int)t.Tool).ThenByDescending(t => t.Parsed)
            .Select(t => (Toolchain: t, Result: EndOfLifeAssessor.Assess(data, t, today)))
            .ToList();

        if (args.Json)
        {
            JsonOut.Write(rows.Select(row => new
            {
                tool = ToolKinds.Display(row.Toolchain.Tool),
                row.Toolchain.Version,
                row.Toolchain.Path,
                state = row.Result.State.ToString().ToLowerInvariant(),
                cycle = row.Result.Cycle,
                endDate = row.Result.EndDate?.ToString("yyyy-MM-dd")
            }));
            return 0;
        }

        var table = new Table("TOOL", "VERSION", "STATE", "DETAIL");
        foreach (var (toolchain, result) in rows)
        {
            var state = result.State switch
            {
                EolState.Ended => Out.RedText("end of life"),
                EolState.EndingSoon => Out.YellowText("ending soon"),
                EolState.Supported => Out.GreenText("supported"),
                _ => Out.DimText("unknown")
            };
            table.Add(ToolKinds.Display(toolchain.Tool).PadRight(8), toolchain.Version.PadRight(14), state, Out.DimText(result.Describe()));
        }

        table.Write();
        Out.Line();
        Out.Line(Out.DimText($"{rows.Count} toolchain{(rows.Count == 1 ? "" : "s")}  ·  data from endoflife.date, cached locally" +
                             (unverified > 0 ? $"  ·  {unverified} with an unknown version not listed (verify them with `devtools verify-toolchain`)" : "")));
        return 0;
    }

    // ---------------------------------------------------------------- outdated

    /// <summary>
    /// Which installed toolchains have a newer release, from the same endoflife.date cache `eol` uses.
    /// Reading uses only the local cache; --refresh is the one thing that contacts the network.
    /// </summary>
    public static int Outdated(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        var service = new EndOfLifeService(store.Root);
        if (args.Has("refresh"))
        {
            var tools = snapshot.Toolchains.Select(t => t.Tool).Distinct().ToList();
            if (!args.Json) Out.Line(Out.DimText("  contacting endoflife.date for " + tools.Select(EndOfLifeProducts.For).OfType<string>().Distinct().Count() + " products"));
            var refresh = service.Refresh(tools, DateTimeOffset.UtcNow, force: true);
            foreach (var error in refresh.Errors) Console.Error.WriteLine(Out.RedText("releases: ") + error);
            if (!args.Json) Out.Line(Out.DimText($"  fetched {refresh.Fetched}, failed {refresh.Errors.Count}"));
        }

        var data = service.Load();
        if (data.Count == 0)
        {
            Console.Error.WriteLine("No release data is cached. Run `devtools outdated --refresh` (this contacts endoflife.date).");
            return 1;
        }

        var rows = snapshot.Toolchains
            .Where(t => t.Health == ToolchainHealth.Ok && !t.Stale && FreshnessAssessor.CanCompare(t.Tool)
                        && !string.Equals(t.Version, "unknown", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => (int)t.Tool).ThenByDescending(t => t.Parsed)
            .Select(t => (Toolchain: t, Result: FreshnessAssessor.Assess(data, t)))
            .ToList();
        var skipped = snapshot.Toolchains.Count(t => !FreshnessAssessor.CanCompare(t.Tool));

        if (args.Json)
        {
            JsonOut.Write(rows.Select(row => new
            {
                tool = ToolKinds.Display(row.Toolchain.Tool),
                row.Toolchain.Version,
                row.Toolchain.Path,
                state = row.Result.State.ToString().ToLowerInvariant(),
                line = row.Result.Line,
                latestInLine = row.Result.LatestInLine,
                newestLine = row.Result.NewestLine
            }));
            return 0;
        }

        var table = new Table("TOOL", "VERSION", "STATE", "DETAIL");
        foreach (var (toolchain, result) in rows)
        {
            var state = result.State switch
            {
                FreshnessState.Outdated => Out.YellowText("outdated"),
                FreshnessState.UpToDate => Out.GreenText("current"),
                _ => Out.DimText("unknown")
            };
            table.Add(ToolKinds.Display(toolchain.Tool).PadRight(8), toolchain.Version.PadRight(14), state, Out.DimText(result.Describe()));
        }

        table.Write();
        var outdated = rows.Count(row => row.Result.State == FreshnessState.Outdated);
        Out.Line();
        Out.Line(Out.DimText($"{outdated} outdated of {rows.Count}  ·  data from endoflife.date, cached locally" +
                             (skipped > 0 ? $"  ·  {skipped} JDK and .NET SDK installs not compared (vendor and runtime numbering differ)" : "")));
        if (outdated > 0) Out.Line(Out.DimText("  next  devtools update --outdated-only"));
        return 0;
    }

    // ---------------------------------------------------------------- env

    public static int ProbeEffective(Args args)
    {
        if (args.Positional.Count != 2 || !ToolKinds.TryParse(args.Positional[1], out var tool))
            return Usage("devtools probe-effective <project-path> <tool> [--yes]");
        var command = EffectiveToolchainProbe.Command(tool);
        if (command is null) return Usage("This tool does not support effective-version probing.");
        var store = Store();
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return Usage("Another operation is using this index.");
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();
        var path = Path.GetFullPath(PlatformPaths.Expand(args.Positional[0]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var project = snapshot.Projects.FirstOrDefault(p => !p.Stale && p.State == ProjectState.Active &&
            string.Equals(Path.TrimEndingDirectorySeparator(p.Path), Path.TrimEndingDirectorySeparator(path), comparison));
        if (project is null) return Usage("No current indexed project at that path.");
        var executable = PathSolver.Which(command.Value.Command);
        if (executable is null) return Usage("The executable was not found on this process's PATH.");
        Out.Line("  " + executable + " " + command.Value.Argument + "  in " + project.Path);
        Out.Line("  Uses the DevTools process environment; interactive shell initialization is not run.");
        if (!args.Has("yes")) { Out.Line("  Dry run. Repeat with --yes to run the version probe."); return 0; }
        if (!store.AppendAudit(new AuditEntry { Action = "probe-effective", Target = project.Path,
                Detail = executable, Outcome = "started" }))
            return Usage("Audit log unavailable; the executable was not started.");
        var (version, error) = EffectiveToolchainProbe.Run(tool, executable, project.Path);
        if (!store.AppendAudit(new AuditEntry { Action = "probe-effective", Target = project.Path,
                Detail = error ?? version, Outcome = version is null ? "failed" : "ok" }))
            return Usage("The probe ran, but its result could not be recorded.");
        if (version is null) { Out.Line(Out.RedText("Probe failed: ") + error); return 1; }
        var requirements = project.Requirements.Where(r => r.Tool == tool).ToList();
        if (requirements.Count == 0) { Out.Line($"  effective {ToolKinds.Display(tool)}: {version}; no declaration found"); return 0; }
        var effective = new Toolchain { Tool = tool, Version = version, Path = executable, Health = ToolchainHealth.Ok };
        var result = new ResolutionEngine([effective]).ResolveTool(tool, requirements);
        Out.Line($"  process-selected: {version}  ·  declared: {result.ConstraintDisplay}  ·  {result.Status}");
        return result.IsProblem ? 1 : 0;
    }

    public static int VerifyToolchain(Args args)
    {
        if (args.Positional.Count != 1) return Usage("devtools verify-toolchain <installation-path> [--yes]");
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();
        var path = Path.GetFullPath(PlatformPaths.Expand(args.Positional[0]));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var index = snapshot.Toolchains.FindIndex(t => t.Health == ToolchainHealth.Unverified &&
            string.Equals(Path.TrimEndingDirectorySeparator(t.Path), Path.TrimEndingDirectorySeparator(path), comparison));
        if (index < 0) return Usage("No unverified toolchain at that path.");
        var candidate = snapshot.Toolchains[index];
        var executable = ManualToolchainDiscovery.FindExecutable(candidate.Tool, candidate.Path);
        Out.Line("  " + (executable ?? "executable not found") + "  (version probe)");
        if (!args.Has("yes")) { Out.Line("  Dry run. Repeat with --yes to execute this file."); return 0; }
        var result = ToolchainVerification.Run(store, path);
        Out.Line(result.Succeeded ? Out.GreenText(result.Message) : Out.RedText(result.Message));
        return result.Succeeded ? 0 : 1;
    }

    public static int Ignore(Args args, bool ignored)
    {
        if (args.Positional.Count != 1) return Usage(ignored ? "devtools ignore <project-path>" : "devtools unignore <project-path>");
        var result = ProjectIgnore.Set(Store(), args.Positional[0], ignored);
        Out.Line(result.Succeeded ? Out.GreenText(result.Message) : Out.RedText(result.Message));
        return result.Succeeded ? 0 : 1;
    }

    public static int Env(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var target = args.Positional.Count > 0 ? args.Positional[0] : Environment.CurrentDirectory;

        var project = FindProject(view, target);
        if (project is null)
        {
            Console.Error.WriteLine($"no project at or above {target}");
            return 1;
        }

        var engine = new ResolutionEngine(snapshot.Toolchains);
        var patch = EnvComposer.Compose(project, engine);
        var shell = args.Value("shell") ?? (OperatingSystem.IsWindows() ? "pwsh" : "bash");

        if (args.Json)
        {
            JsonOut.Write(new
            {
                project = project.Project.DisplayName,
                path = project.Project.Path,
                set = patch.Set,
                pathPrepend = patch.PathPrepend,
                notes = patch.Notes
            });
            return 0;
        }

        Console.WriteLine(patch.Render(shell));
        return 0;
    }

    public static int Shell(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var target = args.Positional.Count > 0 ? args.Positional[0] : Environment.CurrentDirectory;
        var project = FindProject(view, target);

        if (project is null)
        {
            Console.Error.WriteLine($"no project at or above {target}");
            return 1;
        }

        var engine = new ResolutionEngine(snapshot.Toolchains);
        var patch = EnvComposer.Compose(project, engine);

        var shell = args.Value("shell") ??
                    (OperatingSystem.IsWindows()
                        ? "pwsh"
                        : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash");

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            UseShellExecute = false,
            WorkingDirectory = project.Project.Path
        };

        foreach (var (key, value) in patch.Set) startInfo.Environment[key] = value;

        if (patch.PathPrepend.Count > 0)
        {
            var separator = OperatingSystem.IsWindows() ? ";" : ":";
            var current = startInfo.Environment.TryGetValue("PATH", out var existing) ? existing : "";
            startInfo.Environment["PATH"] = string.Join(separator, patch.PathPrepend) + separator + current;
        }

        Out.Line(Out.DimText("entering " + project.Project.Path + " with " +
                          (patch.Set.Count + patch.PathPrepend.Count) + " environment changes"));

        try
        {
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            return process?.ExitCode ?? 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("could not start shell: " + ex.Message);
            return 2;
        }
    }

    // ---------------------------------------------------------------- install

    public static int Install(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var plan = InstallPlanner.Build(view, args.Value("tool"), args.Value("version"));

        if (args.Json && !args.Has("apply"))
        {
            JsonOut.Write(plan);
            return 0;
        }

        if (plan.IsEmpty)
        {
            Out.Line(Out.GreenText("No installable missing versions in the current index. Check 'devtools doctor' for unresolved declarations."));
            return 0;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText(plan.Title));
        Out.Line("  plan " + plan.Plan);

        if (!args.Has("apply"))
        {
            var table = new Table("TOOL", "VERSION", "ACTION", "PLAN");
            foreach (var step in plan.Steps)
                table.Add(step.Tool, step.Version, (step.Manual ? "manual: " : "run: ") + step.Display, step.Plan);
            table.Write();

            Out.Line();
            Out.Line(Out.DimText("  dry run — nothing was executed."));
            Out.Line(Out.DimText("  Repeat with the same filters and add --apply --yes --plan " + plan.Plan));
            return 0;
        }

        if (!args.Has("yes"))
        {
            Out.Line(Out.RedText("Refusing to install without --yes."));
            Out.Line(Out.DimText("Confirm you want these commands run on this machine, then repeat with --yes."));
            return 2;
        }

        if (!string.Equals(args.Value("plan"), plan.Plan, StringComparison.OrdinalIgnoreCase))
            return Usage("The installation plan is missing or has changed. Review it again and provide --plan TOKEN.");
        var result = InstallExecution.Apply(store, args.Value("tool"), args.Value("version"), plan.Plan);
        if (args.Json)
        {
            JsonOut.Write(result);
            return result.Succeeded ? 0 : 1;
        }
        foreach (var item in result.Items) Out.Line($"  {item.Tool} {item.Version}  ·  {item.Outcome}  ·  {item.Message}");
        if (result.Error is not null) Out.Line(Out.RedText(result.Error));
        return result.Succeeded ? 0 : 1;
    }

    // ---------------------------------------------------------------- update

    public static int Update(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        var tool = args.Value("tool");
        var service = new EndOfLifeService(store.Root);
        if (args.Has("refresh"))
        {
            var refresh = service.Refresh(snapshot.Toolchains.Select(t => t.Tool).Distinct().ToList(), DateTimeOffset.UtcNow, force: true);
            foreach (var error in refresh.Errors) Console.Error.WriteLine(Out.RedText("releases: ") + error);
        }

        var releases = service.Load();
        var outdatedOnly = args.Has("outdated-only");
        if (outdatedOnly && releases.Count == 0)
        {
            Console.Error.WriteLine("No release data is cached. Run `devtools update --outdated-only --refresh` (this contacts endoflife.date).");
            return 1;
        }

        var plan = UpdatePlanner.Build(MachineView.Build(snapshot), tool, releases, outdatedOnly);

        if (args.Json && !args.Has("apply"))
        {
            JsonOut.Write(plan);
            return 0;
        }

        if (plan.IsEmpty)
        {
            Out.Line(Out.GreenText("No installed toolchain has a documented update route. See docs/UPDATING.md."));
            return 0;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText(plan.Title));
        Out.Line("  plan " + plan.Plan);

        if (!args.Has("apply"))
        {
            var table = new Table("TOOL", "TARGET", "NEWER", "ACTION", "PLAN");
            foreach (var step in plan.Steps)
                table.Add(step.Tool, step.Version,
                    step.Outdated switch { true => Out.YellowText(step.Available ?? "newer"), false => Out.GreenText("current"), _ => Out.DimText("unchecked") },
                    (step.Manual ? "manual: " : "run: ") + step.Display, step.Plan);
            table.Write();

            Out.Line();
            Out.Line(Out.DimText("  dry run — nothing was executed."));
            Out.Line(Out.DimText("  Repeat with the same filters and add --apply --yes --plan " + plan.Plan));
            if (releases.Count == 0)
                Out.Line(Out.DimText("  NEWER is unchecked: run with --refresh to compare against release data from endoflife.date."));
            return 0;
        }

        if (!args.Has("yes"))
        {
            Out.Line(Out.RedText("Refusing to update without --yes."));
            Out.Line(Out.DimText("Confirm you want these commands run on this machine, then repeat with --yes."));
            return 2;
        }

        if (!string.Equals(args.Value("plan"), plan.Plan, StringComparison.OrdinalIgnoreCase))
            return Usage("The update plan is missing or has changed. Review it again and provide --plan TOKEN.");

        var result = UpdateExecution.Apply(store, tool, plan.Plan, releases: releases, outdatedOnly: outdatedOnly);
        if (args.Json)
        {
            JsonOut.Write(result);
            return result.Succeeded ? 0 : 1;
        }

        foreach (var item in result.Items) Out.Line($"  {item.Tool} {item.Version}  ·  {item.Outcome}  ·  {item.Message}");
        if (result.Error is not null) Out.Line(Out.RedText(result.Error));
        Out.Line(Out.DimText("  Run devtools scan to refresh the index."));
        return result.Succeeded ? 0 : 1;
    }

    // ---------------------------------------------------------------- prune

    public static int Prune(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        DateTimeOffset? olderThan = null;
        if (args.Value("older-than") is { } age)
        {
            if (!int.TryParse(age, out var days) || days < 0 || days > 100000)
                return Usage("--older-than requires a non-negative number of days");
            olderThan = DateTimeOffset.Now.AddDays(-days);
        }
        var artifactName = args.Value("artifact");
        if (artifactName is not null && !ArtifactCleanup.IsKnownName(artifactName))
            return Usage("--artifact must be node_modules, target, obj, or dist");
        var targets = ArtifactCleanup.Plan(snapshot.Projects, args.Value("project"), olderThan, args.Has("measure"), artifactName);
        var fingerprint = ArtifactCleanup.Fingerprint(targets);

        if (args.Json && !args.Has("apply"))
        {
            JsonOut.Write(new { plan = fingerprint, targets = targets.Select(t => new { project = t.Project.DisplayName, path = t.Directory, sizeBytes = t.SizeBytes,
                available = !ArtifactCleanup.HasUnsafePath(t.Directory), plan = ArtifactCleanup.Fingerprint([t]) }) });
            return 0;
        }

        if (args.Json && args.Has("apply"))
        {
            if (!args.Has("yes") || !string.Equals(args.Value("plan"), fingerprint, StringComparison.OrdinalIgnoreCase))
                return Usage("Review the cleanup plan and provide --yes --plan TOKEN.");
            var applied = CleanupExecution.Apply(store, args.Value("project"), olderThan, artifactName, fingerprint);
            JsonOut.Write(applied);
            return applied.Succeeded ? 0 : 1;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("reclaimable build artifacts"));
        Out.Line("  " + targets.Count + " directories across " + targets.Select(t => t.Project).Distinct().Count() + " projects");
        Out.Line("  plan " + fingerprint);
        Out.Line();

        foreach (var target in targets)
            Out.Line("    " + Out.DimText("•") + " " + target.Directory +
                     (args.Has("measure") ? "  ·  " + (target.SizeBytes is null ? "size unavailable" : HumanBytes.Format(target.SizeBytes)) : "") +
                     (ArtifactCleanup.HasUnsafePath(target.Directory) ? "  ·  unavailable: unsafe path" : "  ·  available"));
        if (!args.Has("measure")) Out.Line(Out.DimText("  Add --measure to calculate each target's logical file size."));

        Out.Line();

        if (!args.Has("apply"))
        {
            Out.Line(Out.DimText("  dry run — nothing was deleted."));
            Out.Line(Out.DimText("  To move these targets to trash, repeat this command with the same filters and add --apply --yes --plan " + fingerprint));
            return 0;
        }

        if (!args.Has("yes"))
        {
            Out.Line(Out.RedText("Refusing to delete without --yes."));
            return 2;
        }

        if (!string.Equals(args.Value("plan"), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            Out.Line(Out.RedText("The cleanup plan is missing or has changed. Review the current list and use its --plan value."));
            return 2;
        }

        var result = CleanupExecution.Apply(store, args.Value("project"), olderThan, artifactName, fingerprint);
        foreach (var item in result.Items)
            Out.Line("    " + (item.Moved ? Out.GreenText("moved to trash ") : Out.RedText("failed ")) + item.Path + "  " + item.Message);
        if (result.Error is not null) Out.Line(Out.RedText(result.Error));
        Out.Line($"  moved {result.Items.Count(item => item.Moved)} of {targets.Count} directories to trash");
        return result.Succeeded ? 0 : 1;
    }

    // ---------------------------------------------------------------- disk

    public static int Disk(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);

        if (args.Json)
        {
            JsonOut.Write(new
            {
                projects = snapshot.Projects
                    .Where(p => p.SizeBytes is not null)
                    .OrderByDescending(p => p.SizeBytes)
                    .Select(p => new { name = p.DisplayName, path = p.Path, total = p.SizeBytes, artifacts = p.ArtifactBytes }),
                toolchains = snapshot.Toolchains
                    .Where(t => t.DiskBytes is not null)
                    .OrderByDescending(t => t.DiskBytes)
                    .Select(t => new { tool = ToolKinds.Display(t.Tool), t.Version, t.Path, bytes = t.DiskBytes })
            });
            return 0;
        }

        var measured = snapshot.Projects.Where(p => p.SizeBytes is not null).ToList();
        if (measured.Count == 0)
        {
            Out.Line(Out.YellowText("No size data yet.") + " run:  devtools scan --measure");
            return 0;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("disk usage by project"));
        var table = new Table("PROJECT", "TOTAL", "ARTIFACTS", "PATH");

        foreach (var project in measured.OrderByDescending(p => p.SizeBytes).Take(25))
        {
            table.Add(
                Truncate(project.DisplayName, 26),
                HumanBytes.Format(project.SizeBytes).PadRight(9),
                (project.ArtifactBytes is > 0 ? HumanBytes.Format(project.ArtifactBytes) : Out.DimText("—")).PadRight(9),
                Out.DimText(Format.Path(project.Path, 50)));
        }

        table.Write();
        Out.Line();
        Out.Line(Out.DimText("total " + HumanBytes.Format(measured.Sum(p => p.SizeBytes)) +
                          " · artifacts " + HumanBytes.Format(measured.Sum(p => p.ArtifactBytes))));
        return 0;
    }

    // ---------------------------------------------------------------- export

    public static int Export(Args args)
    {
        var snapshot = Store().Load();
        if (snapshot is null) return RequireScan();

        var view = MachineView.Build(snapshot);
        var format = args.Value("format") ?? "mise";

        // One project's own mise.toml instead of the machine-wide summary.
        if (format == "mise" && args.Value("project") is { } projectPath)
        {
            var wanted = Path.GetFullPath(projectPath);
            var match = snapshot.Projects.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Path), wanted,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (match is null) { Console.Error.WriteLine("No indexed project at " + wanted + ". Run `devtools list` to see the indexed paths."); return 1; }
            Console.Write(MiseExport.Build(match).Text);
            return 0;
        }

        switch (format)
        {
            case "mise":
                Console.WriteLine("# generated by devtools — tool pins across this machine");
                Console.WriteLine("[tools]");
                foreach (var group in snapshot.Projects
                             .Where(p => p.State == ProjectState.Active)
                             .SelectMany(p => p.Requirements)
                             .Where(r => r.Strength <= RequirementStrength.Declared)
                             .GroupBy(r => r.Tool))
                {
                    var version = group
                        .Select(r => r.Constraint.Exact?.ToString() ?? r.Raw)
                        .GroupBy(v => v)
                        .OrderByDescending(g => g.Count())
                        .First().Key;

                    Console.WriteLine($"{ToolKinds.Display(group.Key),-8} = \"{version}\"");
                }

                break;

            case "sdkmanrc":
                Console.WriteLine("# generated by devtools");
                foreach (var group in snapshot.Projects
                             .Where(p => p.State == ProjectState.Active)
                             .SelectMany(p => p.Requirements)
                             .Where(r => r.Tool is ToolKind.Jdk or ToolKind.Maven or ToolKind.Gradle)
                             .GroupBy(r => r.Tool))
                {
                    var version = group.Select(r => r.Constraint.Exact?.ToString() ?? r.Raw)
                        .GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

                    var key = group.Key switch { ToolKind.Jdk => "java", ToolKind.Maven => "maven", _ => "gradle" };
                    Console.WriteLine($"{key}={version}");
                }

                break;

            case "lock":
                JsonOut.Write(new
                {
                    schema = 1,
                    generatedAt = snapshot.ScannedAt,
                    toolchains = view.Stats.Missing.Count == 0
                        ? snapshot.Toolchains.GroupBy(t => ToolKinds.Display(t.Tool))
                            .ToDictionary(g => g.Key, g => g.First().Version)
                        : null,
                    projects = snapshot.Projects.Where(p => p.State == ProjectState.Active).Select(p => new
                    {
                        name = p.DisplayName,
                        path = p.Path,
                        requires = p.Requirements
                            .Where(r => r.Strength <= RequirementStrength.Declared)
                            .GroupBy(r => ToolKinds.Display(r.Tool))
                            .ToDictionary(g => g.Key, g => g.First().Constraint.Display())
                    })
                });
                break;

            default:
                return Usage("devtools export --format mise|sdkmanrc|lock");
        }

        return 0;
    }

    // ---------------------------------------------------------------- roots

    public static int Roots(Args args)
    {
        var store = Store();
        using var operationLock = store.TryAcquireScanLock();
        if (operationLock is null) return Usage("Another operation is using this index.");
        var config = store.LoadConfig();
        var action = args.Positional.Count > 0 ? args.Positional[0].ToLowerInvariant() : "list";
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        switch (action)
        {
            case "add" when args.Positional.Count > 1:
            {
                var path = PlatformPaths.Expand(args.Positional[1]);
                if (!Directory.Exists(path))
                {
                    Out.Line(Out.RedText("not a directory: ") + path);
                    return 2;
                }

                if (!config.Roots.Contains(path, pathComparer))
                    config.Roots.Add(path);

                store.SaveConfig(config);
                Out.Line(Out.GreenText("added ") + path);
                return 0;
            }

            case "remove" when args.Positional.Count > 1:
            {
                var path = PlatformPaths.Expand(args.Positional[1]);
                config.Roots.RemoveAll(r => pathComparer.Equals(r, path));
                store.SaveConfig(config);
                Out.Line("removed " + path);
                return 0;
            }

            default:
                Out.Line();
                Out.Line("  " + Out.BoldText("scan roots"));
                if (config.Roots.Count == 0)
                    Out.Line("    " + Out.DimText("none configured"));
                foreach (var root in config.Roots)
                    Out.Line("    " + root + (Directory.Exists(root) ? "" : Out.RedText("  (missing)")));

                Out.Line();
                Out.Line(Out.DimText("  devtools roots add <path>   ·   devtools roots remove <path>"));
                Out.Line();
                return 0;
        }
    }

    // ---------------------------------------------------------------- migrate

    public static int Migrate(Args args)
    {
        var store = Store();
        var snapshot = store.Load();
        if (snapshot is null) return RequireScan();

        var toolText = args.Positional.Count > 0 ? args.Positional[0] : args.Value("tool");
        var target = args.Value("to");

        if (toolText is null || !ToolKinds.TryParse(toolText, out var tool))
            return Usage("devtools migrate <node|deno|python|jdk|gradle|ruby|dotnet> --to <version>");

        if (string.IsNullOrWhiteSpace(target))
            return Usage("devtools migrate " + toolText + " --to <version>");

        if (!MigrationPlanner.IsValidTarget(target.Trim()))
            return Usage("The target must be a version identifier without spaces or control characters.");
        if (args.Value("from") is { } sourceVersion && !MigrationPlanner.IsValidTarget(sourceVersion))
            return Usage("The source version filter is invalid.");

        if (MigrationPlanner.FilesFor(tool).Count == 0)
        {
            Out.Line(Out.RedText("no file editors for ") + ToolKinds.Display(tool));
            Out.Line(Out.DimText("supported: node, deno, python, jdk, gradle, ruby, dotnet"));
            return 2;
        }

        var options = new MigrationOptions
        {
            Tool = tool,
            Target = target.Trim(),
            From = args.Value("from"),
            IncludeRanges = args.Has("include-ranges")
        };

        var plan = MigrationPlanner.Plan(snapshot.Projects.Where(p => !p.Stale), options);

        if (args.Json && !args.Has("apply"))
        {
            JsonOut.Write(new
            {
                tool = ToolKinds.Display(tool),
                target = options.Target,
                plan = plan.Fingerprint,
                projects = plan.ProjectsAffected,
                files = plan.Edits.Count,
                edits = plan.Edits.Select(edit => new
                {
                    project = edit.ProjectName,
                    projectPath = edit.ProjectPath,
                    path = edit.FullPath,
                    edit.RelativePath,
                    edit.Change,
                    edit.Before,
                    edit.After
                }),
                skipped = plan.Skipped
            });
            return 0;
        }

        if (args.Json && args.Has("apply"))
        {
            if (!args.Has("yes") || !string.Equals(args.Value("plan"), plan.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return Usage("Review the migration plan and provide --yes --plan TOKEN.");
            var appliedResult = MigrationExecution.Apply(store, options, plan.Fingerprint);
            JsonOut.Write(appliedResult);
            return appliedResult.Succeeded ? 0 : 1;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText($"migrate {ToolKinds.Display(tool)} → {options.Target}") +
                 (options.From is null ? "" : Out.DimText($"  (from {options.From})")));

        if (plan.IsEmpty)
        {
            Out.Line("  " + Out.GreenText("nothing to change."));
            if (plan.Skipped.Count > 0)
                Out.Line("  " + Out.DimText(plan.Skipped.Count + " file(s) skipped — see --json"));
            Out.Line();
            return 0;
        }

        Out.Line("  " + plan.Edits.Count + " file" + (plan.Edits.Count == 1 ? "" : "s") +
                 " across " + plan.ProjectsAffected + " project" + (plan.ProjectsAffected == 1 ? "" : "s"));
        Out.Line("  plan " + plan.Fingerprint);
        Out.Line();

        var limit = args.GetNumber("limit", 200);
        foreach (var edit in plan.Edits.Take(limit))
        {
            Out.Line("    " + Truncate(edit.ProjectName, 26).PadRight(28) +
                     edit.RelativePath.PadRight(18) +
                     Out.DimText(edit.Change));

            if (args.Has("diff")) WriteDiff(edit);
        }

        if (plan.Edits.Count > limit)
            Out.Line(Out.DimText($"    … and {plan.Edits.Count - limit} more (use --limit)"));

        if (plan.Skipped.Count > 0)
        {
            Out.Line();
            Out.Line("  " + Out.DimText("skipped " + plan.Skipped.Count + " file(s):"));

            foreach (var skipped in plan.Skipped.Distinct().Take(6))
                Out.Line("    " + Out.DimText("· " + skipped));

            if (plan.Skipped.Count > 6)
                Out.Line(Out.DimText("    … use --json for the full list"));
        }

        Out.Line();

        if (!args.Has("apply"))
        {
            Out.Line(Out.DimText("  dry run — nothing was written."));
            Out.Line(Out.DimText("  show the diff:  ") + "devtools migrate " + ToolKinds.Display(tool) +
                     " --to " + options.Target + " --diff");
            Out.Line(Out.DimText("  apply it:       ") + "devtools migrate " + ToolKinds.Display(tool) +
                     " --to " + options.Target + " --apply --yes --plan " + plan.Fingerprint + " (with the same filters)");
            Out.Line();
            return 0;
        }

        if (!args.Has("yes"))
        {
            Out.Line(Out.RedText("Refusing to rewrite files without --yes."));
            Out.Line(Out.DimText("Review with --diff first, then repeat with --apply --yes."));
            return 2;
        }

        if (!string.Equals(args.Value("plan"), plan.Fingerprint, StringComparison.OrdinalIgnoreCase))
            return Usage("The migration plan is missing or has changed. Review it again and provide --plan TOKEN.");
        var result = MigrationExecution.Apply(store, options, plan.Fingerprint);
        if (args.Json) { JsonOut.Write(result); return result.Succeeded ? 0 : 1; }
        foreach (var item in result.Items)
            Out.Line("  " + (item.Succeeded ? Out.GreenText("✎ ") : Out.RedText("✘ ")) + item.Path + "  " + item.Message);
        if (result.Error is not null) Out.Line(Out.RedText(result.Error));
        Out.Line(Out.DimText("  backups: " + result.BackupDirectory + "; rescan to refresh the index"));
        return result.Succeeded ? 0 : 1;
    }

    private static void WriteDiff(FileEdit edit)
    {
        var before = edit.Before.Replace("\r\n", "\n").Split('\n');
        var after = edit.After.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < Math.Min(before.Length, after.Length); i++)
        {
            if (before[i] == after[i]) continue;

            Out.Line("      " + Out.RedText("- " + Truncate(before[i].Trim(), 96)));
            Out.Line("      " + Out.GreenText("+ " + Truncate(after[i].Trim(), 96)));
            return; // one representative hunk is enough for a version bump
        }
    }

    // ---------------------------------------------------------------- support

    public static int Support(Args args)
    {
        var ecosystems = MarkerCatalog.MarkersByEcosystem
            .OrderBy(pair => pair.Key)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var parsers = ProjectScanner.Defaults().Select(p => new { id = p.Id, ecosystem = p.Ecosystem }).ToList();

        if (args.Json)
        {
            JsonOut.Write(new
            {
                ecosystems = ecosystems.Select(pair => new
                {
                    ecosystem = ToolKinds.DisplayEcosystem(pair.Key),
                    markers = pair.Value,
                    suffixMarkers = pair.Key == Ecosystem.DotNet ? MarkerCatalog.ProjectFileSuffixes : []
                }),
                tools = ToolKinds.All.Select(tool => new
                {
                    tool = ToolKinds.Display(tool),
                    ecosystem = ToolKinds.DisplayEcosystem(ToolKinds.EcosystemOf(tool))
                }),
                managers = InstallPlanner.Managers.ToDictionary(
                    pair => ToolKinds.Display(pair.Key),
                    pair => pair.Value.Select(name => new { name, available = InstallPlanner.IsAvailable(name) })),
                parsers,
                resolutions = Enum.GetNames<ResolutionStatus>(),
                constraintKinds = Enum.GetNames<Core.Constraints.ConstraintKind>()
            });
            return 0;
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("what this build understands"));
        Out.Line();

        Out.Line("  " + Out.BoldText("detection") + Out.DimText("  a directory with any of these at its top level is a project"));
        foreach (var (ecosystem, markers) in ecosystems)
        {
            var names = markers.ToList();
            if (ecosystem == Ecosystem.DotNet)
                names.AddRange(MarkerCatalog.ProjectFileSuffixes.Select(suffix => "*" + suffix));

            names.AddRange(MarkerCatalog.SuffixMarkers
                .Where(pair => pair.Value == ecosystem)
                .Select(pair => "*" + pair.Key));

            WriteWrapped("    " + ToolKinds.DisplayEcosystem(ecosystem).PadRight(10) + "  ",
                string.Join(" · ", names), new string(' ', 16));
        }

        Out.Line();

        var byEcosystem = ToolKinds.All
            .GroupBy(ToolKinds.EcosystemOf)
            .OrderBy(group => group.Key);

        Out.Line("  " + Out.BoldText("tools") + Out.DimText("  installed versions are discovered and matched"));
        foreach (var group in byEcosystem)
        {
            WriteWrapped("    " + ToolKinds.DisplayEcosystem(group.Key).PadRight(10) + "  ",
                string.Join(" · ", group.Select(ToolKinds.Display)), new string(' ', 16));
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("version managers") + Out.DimText("  ✓ = present on this machine"));
        foreach (var (tool, managers) in InstallPlanner.Managers.OrderBy(pair => pair.Key))
        {
            var rendered = managers.Select(name =>
                InstallPlanner.IsAvailable(name) ? name + Out.GreenText(" ✓") : name);

            WriteWrapped("    " + ToolKinds.Display(tool).PadRight(10) + "  ",
                string.Join(" · ", rendered), new string(' ', 16));
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("extractors") + Out.DimText("  parsers compiled into this build"));
        WriteWrapped("    " + new string(' ', 12),
            string.Join(" · ", parsers.Select(p => p.id).Distinct()), new string(' ', 16));

        Out.Line();
        Out.Line("  " + Out.BoldText("migration") + Out.DimText("  files `devtools migrate` can rewrite"));
        foreach (var tool in new[] { ToolKind.Node, ToolKind.Python, ToolKind.Jdk, ToolKind.Ruby, ToolKind.DotNetSdk })
        {
            var files = MigrationPlanner.FilesFor(tool);
            if (files.Count == 0) continue;

            WriteWrapped("    " + ToolKinds.Display(tool).PadRight(10) + "  ",
                string.Join(" · ", files), new string(' ', 16));
        }

        Out.Line();
        Out.Line("  " + Out.BoldText("version syntax") + Out.DimText("  normalised into one comparable constraint"));
        Out.Line("      exact      18.20.2 · 20 · 8.0.100");
        Out.Line("      series     20.x · 1.8.x");
        Out.Line("      ranges     >=18 <21 · ^20.0.0 · ~1.2.3 · ~=3.10 (PEP 440) · [17,22) (Maven)");
        Out.Line("      names      lts/iron · lts/* · latest · stable · 21.0.2-tem (SDKMAN)");
        Out.Line("      unions     ^7.3|^8.0 · ^8.0 || ^7.4");
        Out.Line("      policies   .NET rollForward: disable · patch · feature · minor · major");
        Out.Line();
        Out.Line("  " + Out.BoldText("outcomes") + Out.DimText("  " +
                 string.Join(" · ", Enum.GetNames<ResolutionStatus>().Select(ToKebab))));
        Out.Line();
        return 0;
    }

    /// <summary>Prints a label plus a long list, wrapped so wide catalogs stay readable.</summary>
    private static void WriteWrapped(string label, string items, string hangingIndent, int width = 104)
    {
        var line = new System.Text.StringBuilder(label);
        var indentLength = label.Length;
        var hung = false;

        foreach (var item in items.Split(" · "))
        {
            var piece = item + " · ";

            if (line.Length + piece.Length > width && line.Length > indentLength)
            {
                Out.Line(line.ToString().TrimEnd(' ', '·'));
                line.Clear();
                line.Append(hangingIndent);
                hung = true;
            }

            line.Append(piece);
        }

        if (hung || line.Length > 0) Out.Line(line.ToString().TrimEnd(' ', '·'));
    }

    private static string ToKebab(string name)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }

    // ---------------------------------------------------------------- misc

    public static int Version()
    {
        var version = typeof(Commands).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Out.Line("devtools " + version + "  ·  .NET " +
                 Environment.Version.ToString(3) + "  ·  " + Platform());
        Out.Line(Out.DimText("store: " + PlatformPaths.ApplicationData));
        return 0;
    }

    public static int Help()
    {
        Out.Line();
        Out.Line("  " + Out.BoldText("devtools") + "  —  find every project on this machine and manage its toolchain");
        Out.Line();
        Out.Line("  " + Out.DimText("DISCOVERY"));
        Out.Line("    devtools scan [roots…]        scan configured roots and build the index");
        Out.Line("      --depth N                   maximum depth below a root (default 12)");
        Out.Line("      --measure                   also measure project + artifact size");
        Out.Line("      --no-inventory              skip toolchain discovery");
        Out.Line("      --json                      machine-readable output");
        Out.Line();
        Out.Line("  " + Out.DimText("DIAGNOSIS"));
        Out.Line("    devtools list                 all projects with toolchain status");
        Out.Line("      --tool node --ecosystem java --status missing");
        Out.Line("      --framework spring-boot     projects declaring a framework");
        Out.Line("      --stale 180                 not touched in 180 days");
        Out.Line("    devtools show <path|name>     requirement-by-requirement detail");
        Out.Line("      --explain                   where every version came from");
        Out.Line("    devtools doctor [path]        machine (or project) health");
        Out.Line("    devtools toolchains           every installed toolchain");
        Out.Line("    devtools frameworks           frameworks your projects declare");
        Out.Line("    devtools audit PATH           known vulnerabilities in a project's locked npm and Cargo dependencies (contacts osv.dev)");
        Out.Line("    devtools eol [--refresh]      end-of-life status of installed toolchains (--refresh contacts endoflife.date)");
        Out.Line("    devtools verify-toolchain <path> [--yes]   verify an unpacked installation");
        Out.Line("    devtools ignore <path>        hide a project from lists and plans");
        Out.Line("    devtools unignore <path>      show an ignored project again");
        Out.Line("    devtools environments         list the local and available WSL environments");
        Out.Line("      --load DISTRO [--scan --yes]   load or rescan a WSL index");
        Out.Line("      --unused                    only versions no project uses");
        Out.Line("    devtools disk                 disk usage by project");
        Out.Line();
        Out.Line("  " + Out.DimText("ACTION"));
        Out.Line("    devtools env [path]           shell environment for the project");
        Out.Line("      --shell pwsh|bash|zsh|fish");
        Out.Line("    devtools shell [path]         open a shell with that environment");
        Out.Line("    devtools install              plan missing toolchains (dry run)");
        Out.Line("      --apply --yes               execute the plan");
        Out.Line("    devtools changes [--json]     projects added, removed or changed since the previous scan");
        Out.Line("    devtools outdated [--refresh] installed toolchains with a newer release (--refresh contacts endoflife.date)");
        Out.Line("    devtools update [--tool NAME] [--outdated-only] [--refresh]   plan toolchain updates (dry run)");
        Out.Line("      --apply --yes --plan TOKEN  execute the plan (see docs/UPDATING.md)");
        Out.Line("    devtools prune              plan artifact cleanup (dry run)");
        Out.Line("      --project PATH --artifact NAME --older-than DAYS --measure");
        Out.Line("      --apply --yes --plan TOKEN  move the reviewed targets to trash");
        Out.Line("    devtools migrate <tool>       rewrite version pins across projects");
        Out.Line("      --to 24 [--from 18] [--diff] [--apply --yes]");
        Out.Line("      --include-ranges            also rewrite engines/requires ranges");
        Out.Line();
        Out.Line("  " + Out.DimText("OTHER"));
        Out.Line("    devtools support              everything this build detects and manages");
        Out.Line("    devtools export --format mise|sdkmanrc|lock");
        Out.Line("    devtools roots add|remove|list");
        Out.Line("    devtools version");
        Out.Line();
        Out.Line(Out.DimText("  exit codes: 0 ok · 1 problems found · 2 error"));
        Out.Line();
        return 0;
    }

    public static int Unknown(string command)
    {
        Out.Line(Out.RedText("unknown command: ") + command);
        Out.Line(Out.DimText("run 'devtools help'"));
        return 2;
    }

    private static int RequireScan()
    {
        Out.Line(Out.YellowText("No index yet.") + "  run:  " + Out.BoldText("devtools scan"));
        return 2;
    }

    private static int Usage(string usage)
    {
        Out.Line(Out.DimText("usage: ") + usage);
        return 2;
    }

    private static ProjectView? FindProject(MachineView view, string query)
    {
        var normalized = query.Replace('\\', '/').TrimEnd('/');

        var exact = view.Projects.FirstOrDefault(p =>
            p.Project.Path.Replace('\\', '/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var cwd = Environment.CurrentDirectory.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || cwd.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(".", StringComparison.Ordinal))
        {
            var current = view.Projects
                .Where(p => cwd.StartsWith(p.Project.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Project.Path.Length)
                .FirstOrDefault();

            if (current is not null && normalized.Length == 0) return current;
        }

        var suffix = view.Projects
            .Where(p => p.Project.Path.Replace('\\', '/').EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Project.Path.Length)
            .FirstOrDefault();
        if (suffix is not null) return suffix;

        return view.Projects.FirstOrDefault(p =>
            string.Equals(p.Project.DisplayName, query, StringComparison.OrdinalIgnoreCase));
    }

    private static string Truncate(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";

    private static string FormatDuration(TimeSpan span) =>
        span.TotalSeconds < 1 ? $"{span.TotalMilliseconds:0} ms"
        : span.TotalMinutes < 1 ? $"{span.TotalSeconds:0.0} s"
        : $"{span.TotalMinutes:0} min";

    private static string Platform() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

/// <summary>Small helper so one command can invoke another with mutated arguments.</summary>
internal sealed class ArgsBuilder
{
    private readonly string _command;
    private readonly List<string> _positional = [];

    public ArgsBuilder(Args source)
    {
        _command = source.Command;
        _positional.AddRange(source.Positional);
    }

    public ArgsBuilder Positional(string value)
    {
        _positional.Clear();
        _positional.Add(value);
        return this;
    }

    public Args Build() => Args.Parse([_command, .. _positional]);
}

