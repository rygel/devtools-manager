namespace DevTools.Store;

public sealed class StoreConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Directories the user authorized. A scan never begins anywhere else.</summary>
    public List<string> Roots { get; set; } = [];

    public List<string> Excludes { get; set; } = [];

    public int MaxDepth { get; set; } = 12;

    /// <summary>Measuring every project's disk usage is slow; opt-in.</summary>
    public bool MeasureSizes { get; set; }

    public bool FollowSymlinks { get; set; }

    /// <summary>Look up end-of-life dates on endoflife.date. Off by default because it uses the network.</summary>
    public bool CheckEndOfLife { get; set; }

    /// <summary>Set on first run so we only suggest defaults once.</summary>
    public bool Initialised { get; set; }

    public static StoreConfig WithDefaults()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates =
        [
            Path.Combine(home, "code"),
            Path.Combine(home, "dev"),
            Path.Combine(home, "projects"),
            Path.Combine(home, "repos"),
            Path.Combine(home, "source"),
            Path.Combine(home, "workspace"),
            Path.Combine(home, "git"),
            Path.Combine(home, "Documents"),
            @"C:\Develop",
            @"D:\Develop",
            @"C:\Code"
        ];

        return new StoreConfig
        {
            Roots = candidates
                .Where(p => Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList(),
            Initialised = false
        };
    }
}

public sealed class AuditEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string PlanId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string? Detail { get; set; }
    public string Outcome { get; set; } = "planned";
    public bool Reversible { get; set; }
}
