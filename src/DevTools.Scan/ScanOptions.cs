namespace DevTools.Scan;

public sealed class ScanOptions
{
    public List<string> Roots { get; set; } = [];

    /// <summary>How deep below a root a project may live. 12 covers real layouts without bounding nothing.</summary>
    public int MaxDepth { get; set; } = 12;

    public int MaxParallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>Off by default: following links is how you scan the same tree five times or loop forever.</summary>
    public bool FollowSymlinks { get; set; }

    public List<string> ExcludeDirectoryNames { get; set; } = [];

    public int MaxMarkerFileBytes { get; set; } = 1_000_000;

    public bool ReadGitMetadata { get; set; } = true;

    /// <summary>Walking every project to measure size is opt-in; it is the slowest part of a scan.</summary>
    public bool MeasureSizes { get; set; }

    public int MaxDepthExtension { get; set; } = 4;
}

public sealed class DiscoveredDirectory
{
    public string Path { get; set; } = "";

    public List<string> MarkerFiles { get; } = [];

    public List<string> MarkerDirectories { get; } = [];

    public bool ParentIsProject { get; set; }

    public bool HasStrongMarker { get; set; }

    public bool HasWeakMarker => MarkerFiles.Count > 0 || MarkerDirectories.Count > 0;

    public override string ToString() => Path;
}
