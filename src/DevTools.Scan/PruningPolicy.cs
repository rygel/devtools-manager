namespace DevTools.Scan;

/// <summary>
/// Decides which directories the walker refuses to enter. Every rule here exists to
/// turn a whole-disk walk from "minutes" into "seconds", and every prune is counted so
/// the report can say what was not looked at.
/// </summary>
public sealed class PruningPolicy
{
    /// <summary>Never entered, at any depth, for any root.</summary>
    private static readonly HashSet<string> AlwaysPrune = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components",
        ".git", ".svn", ".hg", ".jj",
        "target", ".gradle", ".mvn-cache",
        ".venv", "venv", "env", "__pycache__", ".tox", ".mypy_cache", ".pytest_cache",
        ".next", ".nuxt", ".svelte-kit", ".parcel-cache", ".turbo", ".nx", ".expo",
        "coverage", ".nyc_output",
        "Pods", ".terraform", ".serverless",
        "dist", "out",
        // PHP/Go dependency trees: vendored code, never a project of its own.
        "vendor",
        ".idea", ".vs", ".fleet",
        ".cargo", ".rustup", ".nuget", ".m2", ".npm", ".pnpm-store", ".yarn", ".bun",
        ".nvm", ".fnm", ".volta", ".asdf", ".sdkman", ".pyenv", ".jdks", ".dotnet",
        ".local", ".cache", ".config", ".opencode", ".cursor-server", ".vscode-server",
        "AppData", "Library", "$RECYCLE.BIN", "System Volume Information",
        ".Trash", ".Trashes", "lost+found"
    };

    /// <summary>Only pruned when the parent is already a detected project (build outputs).</summary>
    private static readonly HashSet<string> ProjectRelativePrune = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "Debug", "Release", "artifacts", "packages"
    };

    /// <summary>OS directories skipped at the top level of a drive root.</summary>
    private static readonly HashSet<string> DriveRootSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "MSOCache", "Config.Msi", "Documents and Settings", "OneDriveTemp",
        "proc", "sys", "dev", "run", "snap", "boot", "usr", "var", "opt", "lib", "lib64",
        "bin", "sbin", "etc", "tmp", "System", "private"
    };

    private readonly HashSet<string> _userExcluded;

    public PruningPolicy(IEnumerable<string>? userExcluded = null)
    {
        _userExcluded = new HashSet<string>(userExcluded ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public PruneDecision Evaluate(string directoryPath, string directoryName, int depth, bool parentIsProject,
        bool parentIsFileSystemRoot = false)
    {
        if (_userExcluded.Contains(directoryName))
            return PruneDecision.Prune("user-exclude");

        // Only skip OS directories when the parent really is a volume root, so a folder
        // called "bin" inside C:\Develop is still scanned.
        if (parentIsFileSystemRoot && DriveRootSkip.Contains(directoryName))
            return PruneDecision.Prune("system");

        if (AlwaysPrune.Contains(directoryName))
            return PruneDecision.Prune("cache-or-output");

        if (parentIsProject && ProjectRelativePrune.Contains(directoryName))
            return PruneDecision.Prune("build-output");

        // A directory whose name starts with '.' is almost never a project, except the
        // handful of dot-directories that *are* markers (.git is handled above).
        if (directoryName.Length > 1 && directoryName[0] == '.' && !MarkerCatalog.MarkerDirectories.Contains(directoryName))
            return PruneDecision.Prune("hidden");

        return PruneDecision.Keep;
    }
}

public readonly record struct PruneDecision(bool ShouldPrune, string? Reason)
{
    public static PruneDecision Keep => new(false, null);

    public static PruneDecision Prune(string reason) => new(true, reason);
}
