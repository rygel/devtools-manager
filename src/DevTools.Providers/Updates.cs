using DevTools.Core.Model;

namespace DevTools.Providers;

/// <summary>
/// Turns "these toolchains are installed" into the update commands their own manager documents.
/// A toolchain whose manager has no documented, non-interactive update command becomes a manual
/// step that shows the documented route instead of a command this program would guess at.
/// Every rule is listed, with its source, in docs/UPDATING.md.
/// </summary>
public static class UpdatePlanner
{
    /// <summary>Builds the plan for the installed toolchains, optionally for a single tool.</summary>
    /// <param name="releases">Cached release data. When given, each step records whether a newer release exists.</param>
    /// <param name="outdatedOnly">Keep only steps whose toolchain is known to be outdated. Tools with no release data are left out.</param>
    public static InstallPlan Build(MachineView view, string? onlyTool = null,
        IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>>? releases = null, bool outdatedOnly = false) =>
        Build(view.Snapshot.Toolchains, onlyTool, name => PathSolver.Which(name) is not null, releases, outdatedOnly);

    internal static InstallPlan Build(IEnumerable<Toolchain> installed, string? onlyTool, Func<string, bool> onPath,
        IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>>? releases = null, bool outdatedOnly = false)
    {
        var steps = new List<PlannedStep>();
        var byKey = new Dictionary<string, PlannedStep>(StringComparer.Ordinal);

        foreach (var toolchain in installed
                     .Where(t => t.Health == ToolchainHealth.Ok && !t.Stale)
                     .OrderBy(t => t.Tool).ThenByDescending(t => t.Parsed))
        {
            if (onlyTool is not null &&
                !string.Equals(ToolKinds.Display(toolchain.Tool), onlyTool, StringComparison.OrdinalIgnoreCase))
                continue;

            var freshness = releases is null ? null : FreshnessAssessor.Assess(releases, toolchain);
            foreach (var step in StepsFor(toolchain, onPath))
            {
                if (freshness is { State: not FreshnessState.Unknown })
                {
                    step.Outdated = freshness.State == FreshnessState.Outdated;
                    step.Available = freshness.Describe();
                }

                // Several installs under one manager often need the same command once. The command covers
                // all of them, so it is outdated when any of them is.
                var key = step.Tool + "|" + step.Display;
                if (!byKey.TryGetValue(key, out var existing))
                {
                    byKey[key] = step;
                    steps.Add(step);
                }
                else if (step.Outdated == true && existing.Outdated != true)
                {
                    existing.Outdated = true;
                    existing.Available = step.Available;
                }
                else if (step.Outdated == false && existing.Outdated is null)
                {
                    existing.Outdated = false;
                    existing.Available = step.Available;
                }
            }
        }

        if (outdatedOnly) steps.RemoveAll(step => step.Outdated != true);

        return new InstallPlan
        {
            Title = steps.Count == 0
                ? "Nothing to update"
                : $"Update {steps.Count} toolchain{(steps.Count == 1 ? "" : "s")}",
            Steps = steps
        };
    }

    internal static IEnumerable<PlannedStep> StepsFor(Toolchain t, Func<string, bool> onPath)
    {
        var manager = (t.Manager ?? "").ToLowerInvariant();
        var tool = ToolKinds.Display(t.Tool);
        var major = Segment(t.Version, 1);
        var minor = Segment(t.Version, 2);

        switch (manager)
        {
            case "fnm" when t.Tool == ToolKind.Node:
                yield return Run(t, $"latest Node {major}.x", "fnm", ["install", major]);
                break;

            case "volta" when t.Tool == ToolKind.Node:
                // `volta install` also makes the version the default, which is how Volta documents installing.
                yield return Run(t, $"latest Node {major}.x (becomes the Volta default)", "volta", ["install", $"node@{major}"]);
                break;

            case "nvm" when t.Tool == ToolKind.Node:
                yield return Manual(t, "latest Node",
                    "nvm is a shell function, so run it in a shell where nvm is loaded: nvm ls-remote, then nvm install <version> (or nvm install --lts)");
                break;

            case "nvm-windows" when t.Tool == ToolKind.Node:
                yield return Manual(t, "latest Node",
                    "In an elevated shell: nvm list available, then nvm install <version> (or nvm install lts), then nvm use <version>");
                break;

            case "corepack" when t.Tool is ToolKind.Pnpm or ToolKind.Yarn:
                yield return Run(t, "latest " + tool, "corepack", ["install", "-g", tool]);
                break;

            case "asdf":
            {
                var plugin = t.Tool == ToolKind.Node ? "nodejs" : tool;
                var prefix = t.Tool is ToolKind.Node or ToolKind.Deno or ToolKind.Jdk ? major : minor;
                yield return Run(t, "refresh the " + plugin + " plugin", "asdf", ["plugin", "update", plugin]);
                yield return Run(t, $"latest {tool} {prefix}", "asdf", ["install", plugin, $"latest:{prefix}"]);
                break;
            }

            case "mise":
                yield return Run(t, "latest within the configured range", "mise", ["upgrade", MiseName(t.Tool)]);
                break;

            case "sdkman":
                yield return Manual(t, "latest " + tool,
                    $"sdk is a shell function (source ~/.sdkman/bin/sdkman-init.sh first): sdk upgrade {SdkmanCandidate(t.Tool)}");
                break;

            case "dvm" when t.Tool == ToolKind.Deno:
                yield return Run(t, "latest Deno", "dvm", ["install"]);
                break;

            case "pyenv":
                yield return Manual(t, "latest Python",
                    "pyenv update (needs the pyenv-update plugin; on pyenv-win it refreshes the version list), pyenv install -l, then pyenv install <version>");
                break;

            case "uv" when t.Tool == ToolKind.Python:
                yield return Run(t, $"latest Python {minor}", "uv", ["python", "upgrade", minor]);
                break;

            case "rustup":
                yield return Run(t, "latest toolchains and rustup", "rustup", ["update"]);
                break;

            case "rbenv":
                yield return Manual(t, "latest Ruby",
                    "Update ruby-build (brew upgrade ruby-build, or git -C \"$(rbenv root)\"/plugins/ruby-build pull), then rbenv install --list, rbenv install <version>, rbenv global <version>");
                break;

            case "tfenv":
                yield return Run(t, "latest Terraform", "tfenv", ["install", "latest"]);
                break;

            case "zvm":
                yield return Run(t, "latest stable Zig (becomes the active version)", "zvm", ["i", "stable"]);
                break;

            case "system":
                foreach (var step in SystemSteps(t, tool, major, onPath)) yield return step;
                break;
        }
    }

    /// <summary>
    /// The executable name each tool's PATH entry is probed under (the probes in ToolchainInventory use these).
    /// </summary>
    internal static string ExecutableFor(ToolKind tool) => tool switch
    {
        ToolKind.Jdk => "java",
        ToolKind.Rustc => "rustc",
        ToolKind.DotNetSdk => "dotnet",
        ToolKind.Lazarus => "lazbuild",
        ToolKind.Kotlin => "kotlinc",
        ToolKind.Maven => "mvn",
        ToolKind.Dmd => "dmd",
        ToolKind.R => "R",
        _ => ToolKinds.Display(tool)
    };

    /// <summary>
    /// Tools found on PATH. When a package manager owns the executable, that manager's command is used,
    /// because the tool's own updater fails on managed installs (pnpm's documented caveat). Otherwise the
    /// tool's own documented self-update is used, if it has one.
    /// </summary>
    private static IEnumerable<PlannedStep> SystemSteps(Toolchain t, string tool, string major, Func<string, bool> onPath)
    {
        var executable = ExecutableFor(t.Tool);
        if (onPath(executable) && PathSolver.Which(executable) is { } found && PackageOwnership.Detect(found) is { } owner)
        {
            yield return OwnedStep(t, tool, owner);
            yield break;
        }

        switch (t.Tool)
        {
            case ToolKind.Pnpm:
                yield return Run(t, "latest pnpm", "pnpm", ["self-update"]);
                break;
            case ToolKind.Bun:
                yield return Run(t, "latest Bun", "bun", ["upgrade"]);
                break;
            case ToolKind.Deno:
                yield return Run(t, "latest stable Deno", "deno", ["upgrade"]);
                break;
            case ToolKind.Uv:
                yield return Run(t, "latest uv (standalone installs only)", "uv", ["self", "update"]);
                break;
            case ToolKind.Npm:
                yield return Run(t, "latest npm", "npm", ["install", "-g", "npm@latest"]);
                break;
            case ToolKind.Julia when onPath("juliaup"):
                yield return Run(t, "latest Julia on every channel", "juliaup", ["update"]);
                break;
            case ToolKind.Swift when onPath("swiftly"):
                yield return Run(t, "latest Swift toolchain", "swiftly", ["update", "--assume-yes"]);
                break;
            case ToolKind.R when onPath("rig"):
                // rig installs side by side; the default R only changes with `rig default`.
                yield return Run(t, "latest R release (installed beside the current ones)", "rig", ["add", "release"]);
                break;
            case ToolKind.Nim when onPath("choosenim"):
                yield return Run(t, "latest stable Nim", "choosenim", ["update", "stable", "-y"]);
                break;
            case ToolKind.Ocaml when onPath("opam"):
                // opam upgrades packages inside a switch; a newer compiler is a new switch, which is manual.
                yield return Run(t, "refresh the opam package list", "opam", ["update"]);
                yield return Run(t, "packages in the current switch", "opam", ["upgrade", "--yes"]);
                yield return Manual(t, "newer OCaml compiler",
                    "A new compiler is a new switch: opam switch list-available, then opam switch create <switch> <compiler>");
                break;
            case ToolKind.Ghc when onPath("ghcup"):
                yield return Manual(t, "latest GHC",
                    "ghcup list, ghcup install ghc, then ghcup set ghc <version>; ghcup upgrade updates ghcup itself");
                break;
            case ToolKind.Yarn:
                yield return Manual(t, "latest Yarn",
                    "Yarn 2+ is updated per project: run yarn set version stable inside the project. Yarn 1.x: npm install --global yarn@1");
                break;
            case ToolKind.DotNetSdk:
                yield return Manual(t, $"latest .NET SDK {major}",
                    $"winget upgrade --id Microsoft.DotNet.SDK.{major} (elevated), or dotnet-install -Channel {major}.0 for a per-user install");
                break;
            case ToolKind.Go:
                yield return Manual(t, "latest Go",
                    "Download the release from go.dev/dl and install it; on Linux remove the old /usr/local/go first, never untar over it");
                break;
        }
    }

    /// <summary>
    /// The command of the package manager that owns the installation. Homebrew and Scoop run without elevation,
    /// so they are steps. apt, dnf and pacman need root and Chocolatey needs an elevated shell, so they are manual.
    /// </summary>
    internal static PlannedStep OwnedStep(Toolchain t, string tool, PackageOwner owner) => owner.Manager switch
    {
        "brew" => Run(t, $"latest {tool} via Homebrew", "brew", ["upgrade", owner.Package]),
        "scoop" => Run(t, $"latest {tool} via Scoop", "scoop", ["update", owner.Package]),
        "choco" => Manual(t, $"latest {tool} via Chocolatey",
            $"from an elevated shell: choco upgrade {owner.Package} -y"),
        "apt" => Manual(t, $"latest {tool} via apt",
            $"sudo apt-get update && sudo apt-get install --only-upgrade -y {owner.Package}"),
        "dnf" => Manual(t, $"latest {tool} via dnf", $"sudo dnf upgrade -y {owner.Package}"),
        "pacman" => Manual(t, $"latest {tool} via pacman",
            $"sudo pacman -S --noconfirm {owner.Package}   (Arch recommends a full upgrade, sudo pacman -Syu)"),
        _ => Manual(t, $"latest {tool}", $"owned by {owner.Manager}; update it with that manager")
    };

    private static PlannedStep Run(Toolchain t, string target, string file, string[] args) => new()
    {
        Tool = ToolKinds.Display(t.Tool),
        Version = target,
        Description = $"update {ToolKinds.Display(t.Tool)} ({t.Manager})",
        FileName = file,
        Arguments = [.. args],
        RequiresNetwork = true
    };

    private static PlannedStep Manual(Toolchain t, string target, string hint) => new()
    {
        Tool = ToolKinds.Display(t.Tool),
        Version = target,
        Description = $"update {ToolKinds.Display(t.Tool)} ({t.Manager})",
        Manual = true,
        Hint = hint,
        RequiresNetwork = true
    };

    /// <summary>The first <paramref name="count"/> dotted segments of a version ("20.11.0", 2 → "20.11").</summary>
    private static string Segment(string version, int count)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(count);
        return string.Join('.', parts.Select(part => new string(part.TakeWhile(char.IsAsciiDigit).ToArray())));
    }

    private static string MiseName(ToolKind tool) => tool switch
    {
        ToolKind.Jdk => "java",
        ToolKind.Node => "node",
        _ => ToolKinds.Display(tool)
    };

    private static string SdkmanCandidate(ToolKind tool) => tool switch
    {
        ToolKind.Jdk => "java",
        _ => ToolKinds.Display(tool)
    };
}
