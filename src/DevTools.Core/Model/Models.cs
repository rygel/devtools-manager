using System.Text.Json.Serialization;
using DevTools.Core.Constraints;
using DevTools.Core.Versioning;

namespace DevTools.Core.Model;

public enum RequirementStrength
{
    /// <summary>A version-manager pin file. A mismatch is an error.</summary>
    Pinned,
    /// <summary>A build descriptor states a version or range. A mismatch is a warning.</summary>
    Declared,
    /// <summary>Engines fields, container tags, devcontainers. Informational only.</summary>
    Advisory,
    /// <summary>Produced by a heuristic. Off by default.</summary>
    Inferred
}

public enum ProvenanceKind
{
    File,
    Command,
    Inferred,
    User,
    GlobalDefault
}

/// <summary>
/// Where a requirement came from. Non-negotiable: every requirement can be traced
/// to a file and line, or to an explicit rule.
/// </summary>
public sealed class Provenance
{
    public ProvenanceKind Kind { get; set; } = ProvenanceKind.File;

    /// <summary>Path relative to the project root, e.g. ".nvmrc" or "pom.xml".</summary>
    public string? FilePath { get; set; }

    public int? Line { get; set; }

    /// <summary>The XML property / JSON key / TOML key that carried the value.</summary>
    public string? Key { get; set; }

    public string? Note { get; set; }

    public static Provenance FileOf(string path, int? line = null, string? key = null) =>
        new() { Kind = ProvenanceKind.File, FilePath = path, Line = line, Key = key };

    public static Provenance Command(string note) =>
        new() { Kind = ProvenanceKind.Command, Note = note };

    public static Provenance Inferred(string rule) =>
        new() { Kind = ProvenanceKind.Inferred, Note = rule };

    public string Describe()
    {
        if (Kind == ProvenanceKind.Inferred) return "inferred: " + (Note ?? "heuristic");
        if (Kind == ProvenanceKind.GlobalDefault) return "machine default";
        if (Kind == ProvenanceKind.User) return "user override";

        var location = FilePath ?? "?";
        if (Line is not null) location += ":" + Line;
        if (Key is not null) location += " (" + Key + ")";
        return location;
    }
}

/// <summary>
/// A project's need for a specific tool at a specific version. Serialised directly into
/// the index; <see cref="Constraint"/> is re-derived on load from <see cref="Raw"/> so the
/// two can never disagree.
/// </summary>
public sealed class Requirement
{
    public ToolKind Tool { get; set; }

    /// <summary>The version text exactly as it appeared ("1.8", "lts/iron", "&gt;=18 &lt;21").</summary>
    public string Raw { get; set; } = "";

    public RequirementStrength Strength { get; set; } = RequirementStrength.Declared;

    /// <summary>Lower is stronger. See the precedence table in the design document (§9.3).</summary>
    public int Precedence { get; set; } = 2;

    public Provenance Provenance { get; set; } = new();

    /// <summary>
    /// The .NET SDK roll-forward policy from <c>global.json</c> ("disable", "latestFeature", …).
    /// Only meaningful for <see cref="ToolKind.DotNetSdk"/>.
    /// </summary>
    public string? RollForward { get; set; }

    [JsonIgnore]
    public Constraint Constraint => _constraint ??= ConstraintParser.Parse(Tool, Raw);

    [JsonIgnore] private Constraint? _constraint;

    [JsonIgnore] public string Display => Constraint.Display();

    public static Requirement Pinned(ToolKind tool, string raw, Provenance provenance) =>
        new() { Tool = tool, Raw = raw, Strength = RequirementStrength.Pinned, Precedence = 1, Provenance = provenance };

    public static Requirement Declared(ToolKind tool, string raw, Provenance provenance) =>
        new() { Tool = tool, Raw = raw, Strength = RequirementStrength.Declared, Precedence = 2, Provenance = provenance };

    public static Requirement Advisory(ToolKind tool, string raw, Provenance provenance) =>
        new() { Tool = tool, Raw = raw, Strength = RequirementStrength.Advisory, Precedence = 4, Provenance = provenance };

    public override string ToString() => $"{ToolKinds.Display(Tool)} {Display}";
}

public enum ToolchainHealth
{
    Ok,
    /// <summary>The directory exists but the executable does not.</summary>
    MissingBinary,
    /// <summary>The executable ran but reported an unexpected version.</summary>
    Broken,
    /// <summary>A placeholder that is not a real installation (Windows Store aliases).</summary>
    Stub,
    /// <summary>The location looks like an installation but its executable has not been verified.</summary>
    Unverified
}

/// <summary>One concrete installed instance of a tool.</summary>
public sealed class Toolchain
{
    public ToolKind Tool { get; set; }

    /// <summary>Normalised version, e.g. "20.11.0".</summary>
    public string Version { get; set; } = "";

    public string? RawVersion { get; set; }

    /// <summary>Install root (not the executable).</summary>
    public string Path { get; set; } = "";

    /// <summary>nvm-windows | nvm | fnm | volta | asdf | mise | sdkman | rustup | pyenv | wrapper | system</summary>
    public string? Manager { get; set; }

    public bool IsGlobalDefault { get; set; }

    /// <summary>True when the path is a shim/symlink whose target can change (nvm's Program Files node).</summary>
    public bool IsProxy { get; set; }

    public ToolchainHealth Health { get; set; } = ToolchainHealth.Ok;

    /// <summary>The installation was not accessible during the latest scan.</summary>
    public bool Stale { get; set; }

    public string? HealthDetail { get; set; }

    public long? DiskBytes { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public long? VerifiedExecutableSize { get; set; }

    public DateTimeOffset? VerifiedExecutableModifiedUtc { get; set; }

    [JsonIgnore] private VersionNumber? _parsed;
    [JsonIgnore] private bool _parsedAttempted;

    [JsonIgnore]
    public VersionNumber? Parsed
    {
        get
        {
            if (!_parsedAttempted)
            {
                _parsedAttempted = true;
                _parsed = VersionNumber.TryParse(Version, out var v) ? v : null;
                if (_parsed is not null && Tool == ToolKind.Jdk) _parsed = JavaVersions.Normalize(_parsed);
            }

            return _parsed;
        }
    }

    public string Describe() =>
        $"{ToolKinds.Display(Tool)} {Version}" + (Manager is null ? "" : $" ({Manager})");
}

public enum ProjectState
{
    Active,
    /// <summary>User asked to ignore it. Survives rescans by project key.</summary>
    Ignored,
    Archived,
    /// <summary>Detected but scored too low to be a project. Kept so the decision is inspectable.</summary>
    Suppressed
}

public sealed class MarkerHit
{
    public string RelPath { get; set; } = "";
    public bool Strong { get; set; }
    public Ecosystem? Ecosystem { get; set; }
}

/// <summary>A discovered development project and everything we extracted from it.</summary>
public sealed class ProjectRecord
{
    /// <summary>Stable identity across moves: git remote + subpath, else manifest identity, else path hash.</summary>
    public string Key { get; set; } = "";

    public string Path { get; set; } = "";

    /// <summary>Symlink-resolved path. Empty when it equals <see cref="Path"/>.</summary>
    public string RealPath { get; set; } = "";

    public string? Name { get; set; }

    public string? GitRemote { get; set; }

    public string? GitBranch { get; set; }

    public DateTimeOffset? LastCommitAt { get; set; }

    public DateTimeOffset? LastActivityAt { get; set; }

    public double Confidence { get; set; }

    public ProjectState State { get; set; } = ProjectState.Active;

    public string? SuppressedReason { get; set; }

    /// <summary>Retained from an earlier scan because its directory was unavailable.</summary>
    public bool Stale { get; set; }

    public List<Ecosystem> Ecosystems { get; set; } = [];

    public List<Requirement> Requirements { get; set; } = [];

    /// <summary>Application frameworks the project declares. Not toolchains: nothing here is installed or managed.</summary>
    public List<FrameworkUse> Frameworks { get; set; } = [];

    public List<MarkerHit> Markers { get; set; } = [];

    public List<Diagnostic> Diagnostics { get; set; } = [];

    public long? SizeBytes { get; set; }

    /// <summary>Build artifacts (node_modules, target, bin, obj, dist) measured separately.</summary>
    public long? ArtifactBytes { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrEmpty(Name)) return Name;
            var trimmed = Path.TrimEnd('/', '\\');
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    [JsonIgnore]
    public bool IsActive => State == ProjectState.Active;

    /// <summary>The scanner could not interpret something. CI drift is a finding about the project, not a parse failure.</summary>
    [JsonIgnore]
    public bool Incomplete => Diagnostics.Any(d => d.Severity != DiagnosticSeverity.Info && d.Code != DriftCode);

    /// <summary>CI installs a different tool version than the project declares.</summary>
    [JsonIgnore]
    public bool HasCiDrift => Diagnostics.Any(d => d.Code == DriftCode);

    public const string DriftCode = "ci-drift";
}

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed class Diagnostic
{
    public DiagnosticSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ProjectKey { get; set; }
    public string? FilePath { get; set; }
    public int? Line { get; set; }

    public static Diagnostic Warn(string code, string message, string? projectKey = null, string? file = null, int? line = null) =>
        new() { Severity = DiagnosticSeverity.Warning, Code = code, Message = message, ProjectKey = projectKey, FilePath = file, Line = line };

    public static Diagnostic Info(string code, string message) =>
        new() { Severity = DiagnosticSeverity.Info, Code = code, Message = message };

    public static Diagnostic Error(string code, string message, string? projectKey = null) =>
        new() { Severity = DiagnosticSeverity.Error, Code = code, Message = message, ProjectKey = projectKey };
}

public sealed class ScanStats
{
    public int DirsVisited { get; set; }
    public int DirsPruned { get; set; }
    public int Errors { get; set; }
    public int ProjectsFound { get; set; }
    public int ProjectsSuppressed { get; set; }
    public string Mode { get; set; } = "walk";
    public long DurationMs { get; set; }
    public bool Partial { get; set; }
    public Dictionary<string, int> PrunedByReason { get; set; } = [];
}

/// <summary>The whole persisted world: projects, toolchains, and how we got them.</summary>
public sealed class IndexSnapshot
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset ScannedAt { get; set; }

    public List<string> Roots { get; set; } = [];

    /// <summary>Configured scan roots, including roots unavailable during the last scan.</summary>
    public List<string> ConfiguredRoots { get; set; } = [];

    public List<ProjectRecord> Projects { get; set; } = [];

    public List<Toolchain> Toolchains { get; set; } = [];

    public ScanStats Stats { get; set; } = new();

    public List<Diagnostic> Diagnostics { get; set; } = [];

    public string HostName { get; set; } = Environment.MachineName;

    public string Platform { get; set; } = Environment.OSVersion.Platform.ToString();

    public string EnvironmentName { get; set; } = "Unknown";

    public static string CurrentEnvironmentName => OperatingSystem.IsWindows() ? "Windows" :
        Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 } distro ? "WSL: " + distro :
        OperatingSystem.IsMacOS() ? "macOS" : "Linux";
}
