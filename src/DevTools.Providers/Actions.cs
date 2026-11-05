using DevTools.Core.Model;
using DevTools.Core.Resolution;
using System.Security.Cryptography;
using System.Text;

namespace DevTools.Providers;

/// <summary>
/// The environment a project needs, expressed as data rather than shell commands.
/// Composing patches instead of sourcing vendor scripts keeps <c>devtools env</c>
/// under 150 ms and makes it shell-agnostic.
/// </summary>
public sealed class EnvPatch
{
    public Dictionary<string, string> Set { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> PathPrepend { get; } = [];

    public List<string> Notes { get; } = [];

    public string Render(string shell)
    {
        var lines = new List<string>();
        var separator = OperatingSystem.IsWindows() ? ";" : ":";

        switch (shell.ToLowerInvariant())
        {
            case "pwsh":
            case "powershell":
                foreach (var (key, value) in Set.OrderBy(kv => kv.Key))
                    lines.Add($"$env:{key} = '{value.Replace("'", "''")}'");

                if (PathPrepend.Count > 0)
                {
                    var joined = string.Join(separator, PathPrepend);
                    lines.Add($"$env:PATH = '{joined}{separator}' + $env:PATH");
                }

                break;

            case "fish":
                foreach (var (key, value) in Set.OrderBy(kv => kv.Key))
                    lines.Add($"set -gx {key} '{value.Replace("'", "\\'")}'");

                if (PathPrepend.Count > 0)
                    lines.AddRange(PathPrepend.Select(path => $"set -gx PATH '{path.Replace("'", "\\'")}' $PATH"));

                break;

            default: // bash, zsh, sh
                foreach (var (key, value) in Set.OrderBy(kv => kv.Key))
                    lines.Add($"export {key}='{value.Replace("'", "'\\''")}'");

                if (PathPrepend.Count > 0)
                {
                    var joined = string.Join(separator, PathPrepend);
                    lines.Add($"export PATH='{joined}{separator}'\"$PATH\"");
                }

                break;
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public static class EnvComposer
{
    public static EnvPatch Compose(ProjectView view, ResolutionEngine engine)
    {
        var patch = new EnvPatch();

        foreach (var tool in view.Resolution.Tools.Where(t => t.Toolchain is not null))
        {
            var toolchain = tool.Toolchain!;
            var bin = BinDirectory(toolchain);

            switch (tool.Tool)
            {
                case ToolKind.Node:
                case ToolKind.Deno:
                case ToolKind.Pnpm:
                case ToolKind.Yarn:
                case ToolKind.Bun:
                case ToolKind.Python:
                case ToolKind.Ruby:
                case ToolKind.Php:
                case ToolKind.Dart:
                    patch.PathPrepend.Add(bin);
                    break;

                case ToolKind.Jdk:
                    patch.Set["JAVA_HOME"] = toolchain.Path;
                    patch.PathPrepend.Add(bin);
                    break;

                case ToolKind.Maven:
                    patch.Set["MAVEN_HOME"] = toolchain.Path;
                    patch.PathPrepend.Add(bin);
                    break;

                case ToolKind.Gradle:
                    patch.Set["GRADLE_HOME"] = toolchain.Path;
                    patch.PathPrepend.Add(bin);
                    break;

                case ToolKind.DotNetSdk:
                {
                    // DOTNET_ROOT must be the install root, not the versioned sdk directory.
                    var root = toolchain.Path;
                    if (string.Equals(Path.GetFileName(Path.GetDirectoryName(root)), "sdk", StringComparison.OrdinalIgnoreCase))
                        root = Path.GetDirectoryName(Path.GetDirectoryName(root)) ?? root;

                    patch.Set["DOTNET_ROOT"] = root;
                    patch.PathPrepend.Add(root);
                    break;
                }

                case ToolKind.Go:
                    patch.Set["GOROOT"] = toolchain.Path;
                    patch.PathPrepend.Add(bin);
                    break;

                case ToolKind.Rustc:
                    patch.PathPrepend.Add(bin);
                    break;

                default:
                    patch.PathPrepend.Add(bin);
                    break;
            }
        }

        // Deterministic order: later entries win in PATH, so strongest requirements go last.
        patch.PathPrepend.Reverse();

        foreach (var tool in view.Resolution.Tools.Where(t => t.IsProblem))
        {
            patch.Notes.Add($"{ToolKinds.Display(tool.Tool)} {tool.ConstraintDisplay} is not satisfied " +
                            $"({tool.Status.ToString().ToLowerInvariant()})");
        }

        return patch;
    }

    /// <summary>The directory that holds the executables, wherever the manager hid them.</summary>
    public static string BinDirectory(Toolchain toolchain)
    {
        foreach (var candidate in new[] { "bin", Path.Combine("installation", "bin"), "Contents/Home/bin" })
        {
            try
            {
                var path = Path.Combine(toolchain.Path, candidate);
                if (Directory.Exists(path)) return path;
            }
            catch
            {
                // malformed path
            }
        }

        return toolchain.Path;
    }
}

public sealed class PlannedStep
{
    public string Tool { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string? FileName { get; init; }
    public List<string> Arguments { get; init; } = [];
    public bool RequiresNetwork { get; init; }
    public bool Manual { get; init; }
    public string? Hint { get; init; }
    public List<string> Projects { get; set; } = [];

    /// <summary>For update steps: what the cached release data says, for example "20.11.0 → 20.19.4". Not part of the plan token.</summary>
    public string? Available { get; set; }

    /// <summary>For update steps: true when the cached release data shows a newer release, false when current, null when unknown.</summary>
    public bool? Outdated { get; set; }

    public string Plan => InstallPlanner.Fingerprint([this]);

    public string Display => FileName is null
        ? Hint ?? Description
        : FileName + " " + string.Join(' ', Arguments);
}

public sealed class InstallPlan
{
    public string Title { get; init; } = "";
    public List<PlannedStep> Steps { get; init; } = [];
    public bool IsEmpty => Steps.Count == 0;
    public string Plan => InstallPlanner.Fingerprint(Steps);
}

/// <summary>
/// Turns "these versions are missing" into concrete commands, delegating to whichever
/// version manager is already on the machine. Never reimplements a download when a
/// manager exists, because the manager knows about checksums and platform quirks.
/// </summary>
public static class InstallPlanner
{
    private static readonly Dictionary<ToolKind, string[]> ManagerPreference = new()
    {
        [ToolKind.Node] = ["fnm", "nvm-windows", "nvm", "volta", "mise", "asdf"],
        [ToolKind.Jdk] = ["sdkman"],
        [ToolKind.Maven] = ["sdkman"],
        [ToolKind.Gradle] = ["sdkman"],
        [ToolKind.Python] = ["pyenv", "uv", "mise", "asdf"],
        [ToolKind.Rustc] = ["rustup"],
        [ToolKind.Ruby] = ["rbenv", "mise", "asdf"],
        [ToolKind.Pnpm] = ["corepack"],
        [ToolKind.Yarn] = ["corepack"],
        [ToolKind.Npm] = ["npm"],
        [ToolKind.Deno] = ["dvm", "mise", "asdf"],
        [ToolKind.Elixir] = ["asdf", "mise"],
        [ToolKind.Scala] = ["manual"],
        [ToolKind.Kotlin] = ["sdkman"],
        [ToolKind.Zig] = ["zvm", "asdf", "mise"],
        [ToolKind.Terraform] = ["tfenv", "asdf", "mise"],
        [ToolKind.Julia] = ["juliaup", "asdf", "mise"],
        [ToolKind.Cmake] = ["manual"],
        [ToolKind.Meson] = ["manual"],
        [ToolKind.Conan] = ["manual"],
        [ToolKind.Bun] = ["system"],
        [ToolKind.DotNetSdk] = ["manual"],
        [ToolKind.Go] = ["manual"]
    };

    /// <summary>Which version manager handles each tool, in preference order.</summary>
    public static IReadOnlyDictionary<ToolKind, string[]> Managers => ManagerPreference;

    public static bool IsAvailable(string manager) => IsManagerPresent(manager);

    public static InstallPlan Build(MachineView view, string? onlyTool = null, string? onlyVersion = null)    {
        var steps = new List<PlannedStep>();

        var installedByTool = view.Snapshot.Toolchains
            .GroupBy(t => t.Tool)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var missing in view.Stats.Missing)
        {
            if (onlyTool is not null &&
                !string.Equals(ToolKinds.Display(missing.Tool), onlyTool, StringComparison.OrdinalIgnoreCase))
                continue;

            installedByTool.TryGetValue(missing.Tool, out var installed);
            var manager = ChooseManager(missing.Tool, installed);

            var step = BuildStep(missing, manager);
            step.Projects = missing.Projects.ToList();
            if (onlyVersion is null || string.Equals(step.Version, onlyVersion, StringComparison.OrdinalIgnoreCase))
                steps.Add(step);
        }

        return new InstallPlan
        {
            Title = steps.Count == 0
                ? "Nothing to install"
                : $"Install {steps.Count} missing toolchain{(steps.Count == 1 ? "" : "s")}",
            Steps = steps
        };
    }

    public static string Fingerprint(IEnumerable<PlannedStep> steps)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var step in steps.OrderBy(s => s.Tool, StringComparer.Ordinal).ThenBy(s => s.Version, StringComparer.Ordinal))
            {
                writer.Write(step.Tool);
                writer.Write(step.Version);
                writer.Write(step.Manual);
                writer.Write(step.RequiresNetwork);
                writer.Write(step.Description);
                writer.Write(step.FileName ?? "");
                writer.Write(step.Hint ?? "");
                writer.Write(step.Arguments.Count);
                foreach (var argument in step.Arguments) writer.Write(argument);
                writer.Write(step.Projects.Count);
                foreach (var project in step.Projects.Order(StringComparer.Ordinal)) writer.Write(project);
            }
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16];
    }

    internal static PlannedStep BuildStep(MissingToolchain missing, string manager)
    {
        var version = CleanVersion(missing);
        var tool = missing.Tool;

        if (missing.RequiresVersionChoice)
            return new PlannedStep { Tool = ToolKinds.Display(tool), Version = missing.Constraint, Manual = true,
                Description = "choose a version that satisfies the declared constraint",
                Hint = $"Choose a {ToolKinds.Display(tool)} version satisfying {missing.Constraint}; no version is selected automatically." };

        // SDKMAN needs a distribution-qualified identifier ("21.0.2-tem"); a bare major
        // cannot be installed by name, so ask instead of emitting a command that fails.
        if (tool == ToolKind.Jdk && manager == "sdkman" && !version.Contains('-'))
        {
            return new PlannedStep
            {
                Tool = "jdk",
                Version = version,
                Description = $"pick a JDK {version} distribution",
                Manual = true,
                Hint = $"sdk list java   # then: sdk install java {version}.0.<patch>-tem"
            };
        }

        return manager switch
        {
            "fnm" => Run("fnm", ["install", version], tool, version),
            "nvm-windows" or "nvm" => Command("nvm", $"install {version}", tool, version,
                hint: "nvm is a shell function: run it in a shell where nvm is loaded"),
            "volta" => Run("volta", ["fetch", $"{ToolKinds.Display(tool)}@{version}"], tool, version),
            "mise" => Run("mise", ["install", $"{ToolKinds.Display(tool)}@{version}"], tool, version),
            "asdf" => Run("asdf", ["install", tool == ToolKind.Node ? "nodejs" : ToolKinds.Display(tool), version], tool, version),
            "sdkman" => Command("sdk", $"install {SdkmanCandidate(tool)} {version}", tool, version,
                hint: "sdk is a shell function: run 'source ~/.sdkman/bin/sdkman-init.sh' first"),
            "pyenv" => Run("pyenv", ["install", "-s", version], tool, version),
            "uv" => Run("uv", ["python", "install", version], tool, version),
            "rustup" => Run("rustup", ["toolchain", "install", version], tool, version),
            "rbenv" => Run("rbenv", ["install", "-s", version], tool, version),
            "corepack" => Run("corepack", ["prepare", $"{ToolKinds.Display(tool)}@{version}"], tool, version),
            "npm" => new PlannedStep { Tool = "npm", Version = version, Manual = true,
                Description = "installing npm globally changes the active npm version",
                Hint = $"npm install -g npm@{version}   (changes the global npm version)" },
            "dvm" => Run("dvm", ["install", version], tool, version),
            "zvm" => Run("zvm", ["i", version], tool, version),
            "tfenv" => Run("tfenv", ["install", version], tool, version),
            "juliaup" => Run("juliaup", ["add", version], tool, version),
            _ => Manual(tool, version)
        };
    }

    private static PlannedStep Manual(ToolKind tool, string version) => tool switch
    {
        ToolKind.DotNetSdk => new PlannedStep
        {
            Tool = "dotnet",
            Version = version,
            Description = $"install .NET SDK {version}",
            Manual = true,
            Hint = $"dotnet-install --version {version}   (or use the Visual Studio installer)",
            RequiresNetwork = true
        },
        ToolKind.Go => new PlannedStep
        {
            Tool = "go",
            Version = version,
            Description = $"install Go {version}",
            Manual = true,
            Hint = $"go install golang.org/dl/go{version}@latest && go{version} download",
            RequiresNetwork = true
        },
        ToolKind.Uv => new PlannedStep
        {
            Tool = "uv",
            Version = version,
            Description = "install uv",
            Manual = true,
            Hint = "pipx install uv   (or the standalone installer from docs.astral.sh/uv)"
        },
        ToolKind.Pnpm => new PlannedStep
        {
            Tool = "pnpm",
            Version = version,
            Description = $"install pnpm {version}",
            Manual = true,
            Hint = $"npm install -g pnpm@{version}"
        },
        ToolKind.Yarn => new PlannedStep
        {
            Tool = "yarn",
            Version = version,
            Description = $"install yarn {version}",
            Manual = true,
            Hint = $"npm install -g yarn@{version}"
        },
        ToolKind.Bun => new PlannedStep
        {
            Tool = "bun",
            Version = version,
            Description = "install bun",
            Manual = true,
            Hint = "npm install -g bun   (or brew install oven-sh/bun/bun)"
        },
        ToolKind.Php => new PlannedStep
        {
            Tool = "php",
            Version = version,
            Description = $"install PHP {version}",
            Manual = true,
            Hint = "brew install php@<version>   /   apt install php<version>"
        },
        ToolKind.Ruby => new PlannedStep
        {
            Tool = "ruby",
            Version = version,
            Description = $"install Ruby {version}",
            Manual = true,
            Hint = "rbenv install <version>   /   your platform package manager"
        },
        _ => new PlannedStep
        {
            Tool = ToolKinds.Display(tool),
            Version = version,
            Description = $"install {ToolKinds.Display(tool)} {version}",
            Manual = true,
            Hint = ManagerHint(tool)
        }
    };

    /// <summary>What to install first when there is no manager to delegate to.</summary>
    private static string ManagerHint(ToolKind tool) => tool switch
    {
        ToolKind.Node => "install a Node version manager first (fnm or nvm), then re-run devtools install",
        ToolKind.Deno => "install dvm (or mise/asdf) first, then re-run devtools install",
        ToolKind.Jdk => "install SDKMAN (or a JDK directly), then re-run devtools install",
        ToolKind.Maven or ToolKind.Gradle => "install SDKMAN, or use each project's wrapper",
        ToolKind.Python => "install pyenv or uv, then re-run devtools install",
        ToolKind.Ruby => "install rbenv, then re-run devtools install",
        ToolKind.Go => "download Go from go.dev/dl, then re-run devtools install",
        ToolKind.Rustc => "install rustup from rustup.rs, then re-run devtools install",
        ToolKind.Elixir => "install asdf or mise, then re-run devtools install",
        ToolKind.Scala => "install scala-cli (scala-cli.virtuslab.org) or use coursier",
        ToolKind.Kotlin => "install SDKMAN, then: sdk install kotlin <version>",
        ToolKind.Zig => "install zvm (zvm.app) or asdf, then re-run devtools install",
        ToolKind.Terraform => "install tfenv or asdf, then re-run devtools install",
        ToolKind.Julia => "install juliaup, then re-run devtools install",
        ToolKind.Cmake => "install CMake from cmake.org or your platform package manager",
        ToolKind.Meson => "pipx install meson",
        ToolKind.Conan => "pipx install conan",
        _ => $"no supported version manager for {ToolKinds.Display(tool)}; install one first"
    };

    private static PlannedStep Run(string file, string[] args, ToolKind tool, string version) => new()
    {
        Tool = ToolKinds.Display(tool),
        Version = version,
        Description = $"install {ToolKinds.Display(tool)} {version}",
        FileName = file,
        Arguments = args.ToList(),
        RequiresNetwork = true
    };

    private static PlannedStep Command(string file, string arguments, ToolKind tool, string version, string hint) => new()
    {
        Tool = ToolKinds.Display(tool),
        Version = version,
        Description = $"install {ToolKinds.Display(tool)} {version}",
        Manual = true,
        Hint = $"{file} {arguments}   ({hint})"
    };

    private static string ChooseManager(ToolKind tool, List<Toolchain>? installed)
    {
        var preference = ManagerPreference.TryGetValue(tool, out var list) ? list : ["manual"];

        if (installed is not null)
        {
            foreach (var candidate in preference)
            {
                if (installed.Any(t => string.Equals(t.Manager, candidate, StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
        }

        // Presence of the manager on disk is enough, even with nothing installed yet.
        foreach (var candidate in preference)
        {
            if (IsManagerPresent(candidate)) return candidate;
        }

        // No manager at all: emitting "fnm install ..." would just fail, so say so instead.
        return "manual";
    }

    private static bool IsManagerPresent(string manager) => manager switch
    {
        "fnm" => PathSolver.Which("fnm") is not null,
        "nvm" or "nvm-windows" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".nvm"))
                                 || Directory.Exists(Path.Combine(
                                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm")),
        "sdkman" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".sdkman")),
        "pyenv" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".pyenv")),
        "rustup" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".rustup")),
        "rbenv" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".rbenv")),
        "volta" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".volta")),
        "asdf" => Directory.Exists(Path.Combine(PlatformPaths.Home, ".asdf")),
        "mise" => PathSolver.Which("mise") is not null,
        "uv" => PathSolver.Which("uv") is not null,
        "corepack" => PathSolver.Which("corepack") is not null,
        "npm" => PathSolver.Which("npm") is not null,
        "dvm" => PathSolver.Which("dvm") is not null
                 || Directory.Exists(Path.Combine(PlatformPaths.Home, ".dvm")),
        "zvm" => PathSolver.Which("zvm") is not null
                 || Directory.Exists(Path.Combine(PlatformPaths.Home, ".zvm")),
        "tfenv" => PathSolver.Which("tfenv") is not null
                   || Directory.Exists(Path.Combine(PlatformPaths.Home, ".tfenv")),
        "juliaup" => PathSolver.Which("juliaup") is not null,
        _ => false
    };

    private static string SdkmanCandidate(ToolKind tool) => tool switch
    {
        ToolKind.Jdk => "java",
        ToolKind.Gradle => "gradle",
        ToolKind.Maven => "maven",
        ToolKind.Kotlin => "kotlin",
        _ => ToolKinds.Display(tool)
    };

    private static string CleanVersion(MissingToolchain missing)
    {
        var version = missing.SuggestedVersion;

        if (version.StartsWith("lts", StringComparison.OrdinalIgnoreCase))
            return Core.Constraints.LtsRules.NewestKnownNodeLts.ToString();

        if (version.Length == 0 || version == "*") return "latest";

        return version;
    }
}

/// <summary>Executes a planned step. Only ever called behind an explicit confirmation.</summary>
public static class InstallExecutor
{
    public static ProcessResult Execute(PlannedStep step, TimeSpan timeout, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        if (step.Manual || step.FileName is null)
            return new ProcessResult { ExitCode = -1, Error = "manual step" };

        var (file, arguments) = Resolve(step.FileName, step.Arguments);
        return (runner ?? ProcessRunner.Shared).Run(file, arguments, timeout, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// npm, corepack and pnpm are .cmd shims on Windows, which CreateProcess cannot start by bare name.
    /// A command that is not found is passed through unchanged so the failure names it.
    /// </summary>
    internal static (string File, IReadOnlyList<string> Arguments) Resolve(string file, IReadOnlyList<string> arguments)
    {
        var path = PathSolver.Which(file);
        if (path is null) return (file, arguments);

        if (OperatingSystem.IsWindows() &&
            (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            return ("cmd.exe", ["/c", path, .. arguments]);

        return (path, arguments);
    }
}

public static class HumanBytes
{
    public static string Format(long? bytes)
    {
        if (bytes is null) return "—";

        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
