using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools.Core.Model;
using Microsoft.Data.Sqlite;

namespace DevTools.Store;

/// <summary>One scan in the history: when it ran and how large the result was.</summary>
public sealed record ScanSummary(long Id, DateTimeOffset ScannedAt, int ProjectCount, int ToolchainCount);

/// <summary>A project as it was recorded in one scan. <see cref="Digest"/> changes when anything meaningful about it changes.</summary>
public sealed record ScanProject(string Key, string Path, string Name, string State, string Digest);

/// <summary>Projects added, removed, or changed between the two most recent scans.</summary>
public sealed record ScanChanges(
    DateTimeOffset CurrentScanAt,
    DateTimeOffset? PreviousScanAt,
    IReadOnlyList<ScanProject> Added,
    IReadOnlyList<ScanProject> Removed,
    IReadOnlyList<ScanProject> Changed);

/// <summary>
/// Durable state in one SQLite database, <c>devtools.db</c>: the current index, user configuration,
/// the scan history with a digest per project (for "what changed"), and the append-only audit log.
///
/// Every write runs in a transaction, so a crash mid-scan leaves the previous snapshot intact.
/// Earlier versions stored <c>index.json</c>, <c>config.json</c> and <c>audit.jsonl</c>. The first
/// open imports them in one transaction and renames each file to <c>*.migrated</c>. An invalid legacy
/// file stops the import, is left unchanged, and the error names it.
/// </summary>
public sealed class SqliteStore
{
    public const string DatabaseFileName = "devtools.db";

    /// <summary>Older scans are pruned so the database does not grow without bound.</summary>
    public const int ScanHistoryLimit = 100;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS config (id INTEGER PRIMARY KEY CHECK (id = 1), json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS snapshot (id INTEGER PRIMARY KEY CHECK (id = 1), scanned_at TEXT NOT NULL, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS scans (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            scanned_at TEXT NOT NULL,
            project_count INTEGER NOT NULL,
            toolchain_count INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS scan_projects (
            scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
            project_key TEXT NOT NULL,
            path TEXT NOT NULL,
            name TEXT NOT NULL,
            state TEXT NOT NULL,
            digest TEXT NOT NULL,
            PRIMARY KEY (scan_id, project_key));
        CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, json TEXT NOT NULL);
        """;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public SqliteStore(string directory, bool createDirectory = true)
    {
        Root = directory;
        if (createDirectory) Directory.CreateDirectory(directory);
    }

    public string Root { get; }

    public string DatabasePath => Path.Combine(Root, DatabaseFileName);

    /// <summary>Where the pre-SQLite index was kept. Read once during migration, then renamed.</summary>
    public string LegacyIndexPath => Path.Combine(Root, "index.json");

    /// <summary>Where the pre-SQLite configuration was kept. Read once during migration, then renamed.</summary>
    public string LegacyConfigPath => Path.Combine(Root, "config.json");

    /// <summary>Where the pre-SQLite audit log was kept. Read once during migration, then renamed.</summary>
    public string LegacyAuditPath => Path.Combine(Root, "audit.jsonl");

    /// <summary>Exclusive across processes for scans, configuration changes, and mutating actions.</summary>
    public FileStream? TryAcquireScanLock()
    {
        try { return new FileStream(Path.Combine(Root, "scan.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    public IndexSnapshot? Load()
    {
        using var connection = OpenRead();
        if (connection is null) return null;

        var json = ScalarText(connection, "SELECT json FROM snapshot WHERE id = 1");
        return json is null ? null : ParseSnapshot(json, DatabasePath, "stored index");
    }

    public void Save(IndexSnapshot snapshot)
    {
        using var connection = Connect();
        InTransaction(connection, () => WriteSnapshot(connection, snapshot));
    }

    public StoreConfig LoadConfig(bool createIfMissing = true)
    {
        using var connection = createIfMissing ? Connect() : OpenRead();
        if (connection is null) return new StoreConfig();

        var json = ScalarText(connection, "SELECT json FROM config WHERE id = 1");
        if (json is not null) return ParseConfig(json, DatabasePath, "stored configuration");
        if (!createIfMissing) return new StoreConfig();

        var defaults = StoreConfig.WithDefaults();
        WriteConfig(connection, defaults);
        return defaults;
    }

    public void SaveConfig(StoreConfig config)
    {
        using var connection = Connect();
        InTransaction(connection, () => WriteConfig(connection, config));
    }

    /// <summary>Returns false instead of throwing, so callers can refuse to continue when the audit trail is missing.</summary>
    public bool AppendAudit(AuditEntry entry)
    {
        try
        {
            using var connection = Connect();
            Run(connection, "INSERT INTO audit (timestamp, json) VALUES ($at, $json)",
                ("$at", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                ("$json", JsonSerializer.Serialize(entry, Options)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            return false;
        }
    }

    public IReadOnlyList<AuditEntry> ReadAudit(int maximum = 200)
    {
        try
        {
            using var connection = OpenRead();
            if (connection is null) return [];

            using var command = Command(connection,
                "SELECT json FROM audit WHERE id IN (SELECT id FROM audit ORDER BY id DESC LIMIT $limit) ORDER BY id",
                ("$limit", maximum));
            using var reader = command.ExecuteReader();
            var entries = new List<AuditEntry>();
            while (reader.Read())
            {
                try
                {
                    if (JsonSerializer.Deserialize<AuditEntry>(reader.GetString(0), Options) is { } entry) entries.Add(entry);
                }
                catch (JsonException) { }
            }
            return entries;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>The most recent scans first.</summary>
    public IReadOnlyList<ScanSummary> ScanHistory(int maximum = 20)
    {
        using var connection = OpenRead();
        return connection is null ? [] : ScanHistory(connection, maximum);
    }

    /// <summary>Compares the latest scan with the one before it. Null when no scan has been stored.</summary>
    public ScanChanges? Changes()
    {
        using var connection = OpenRead();
        if (connection is null) return null;

        var recent = ScanHistory(connection, 2);
        if (recent.Count == 0) return null;

        var current = ProjectsOf(connection, recent[0].Id);
        if (recent.Count == 1)
        {
            return new ScanChanges(recent[0].ScannedAt, null, [], [], []);
        }

        var previous = ProjectsOf(connection, recent[1].Id);
        var added = current.Values.Where(p => !previous.ContainsKey(p.Key)).ToList();
        var removed = previous.Values.Where(p => !current.ContainsKey(p.Key)).ToList();
        var changed = current.Values
            .Where(p => previous.TryGetValue(p.Key, out var before) && before.Digest != p.Digest)
            .ToList();

        return new ScanChanges(recent[0].ScannedAt, recent[1].ScannedAt, added, removed, changed);
    }

    /// <summary>Content digest of a project. Sizes, timestamps and <c>Stale</c> change without the project changing, so they are left out.</summary>
    public static string ProjectDigest(ProjectRecord project)
    {
        var identity = new
        {
            project.Key,
            project.Path,
            project.Name,
            project.State,
            project.GitRemote,
            project.GitBranch,
            project.Ecosystems,
            project.Requirements,
            project.Frameworks,
            project.Markers
        };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity, Options)));
        return Convert.ToHexString(bytes, 0, 16);
    }

    private static List<ScanSummary> ScanHistory(SqliteConnection connection, int maximum)
    {
        using var command = Command(connection,
            "SELECT id, scanned_at, project_count, toolchain_count FROM scans ORDER BY id DESC LIMIT $limit",
            ("$limit", maximum));
        using var reader = command.ExecuteReader();
        var scans = new List<ScanSummary>();
        while (reader.Read())
        {
            scans.Add(new ScanSummary(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }
        return scans;
    }

    private static Dictionary<string, ScanProject> ProjectsOf(SqliteConnection connection, long scanId)
    {
        using var command = Command(connection,
            "SELECT project_key, path, name, state, digest FROM scan_projects WHERE scan_id = $scan",
            ("$scan", scanId));
        using var reader = command.ExecuteReader();
        var projects = new Dictionary<string, ScanProject>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var project = new ScanProject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
            projects[project.Key] = project;
        }
        return projects;
    }

    /// <summary>Opens the database, creating it and migrating legacy files. Use for writes.</summary>
    private SqliteConnection Connect()
    {
        Directory.CreateDirectory(Root);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooled connections would keep the file open after Dispose, and the CLI and desktop
            // share this file. Each operation opens and closes its own connection.
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();

        try
        {
            Exec(connection, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
            Exec(connection, SchemaSql);
            MigrateLegacyIfNeeded(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens for reading without creating anything. Null when there is nothing stored yet.</summary>
    private SqliteConnection? OpenRead()
    {
        if (!Directory.Exists(Root)) return null;
        if (!File.Exists(DatabasePath) && !HasLegacyFiles()) return null;
        return Connect();
    }

    private bool HasLegacyFiles() =>
        File.Exists(LegacyIndexPath) || File.Exists(LegacyConfigPath) || File.Exists(LegacyAuditPath);

    private void MigrateLegacyIfNeeded(SqliteConnection connection)
    {
        if (IsMigrated(connection)) return;

        // Parse everything before writing anything, so an invalid file changes nothing.
        var legacyIndex = File.Exists(LegacyIndexPath)
            ? ParseSnapshot(File.ReadAllText(LegacyIndexPath), LegacyIndexPath, "legacy index")
            : null;
        var legacyConfig = File.Exists(LegacyConfigPath)
            ? ParseConfig(File.ReadAllText(LegacyConfigPath), LegacyConfigPath, "legacy configuration")
            : null;
        var legacyAudit = File.Exists(LegacyAuditPath) ? ReadLegacyAudit() : [];

        InTransaction(connection, () =>
        {
            // Another process may have finished the import while this one was parsing.
            if (IsMigrated(connection)) return;

            if (legacyIndex is not null) WriteSnapshot(connection, legacyIndex);
            if (legacyConfig is not null) WriteConfig(connection, legacyConfig);
            foreach (var entry in legacyAudit)
            {
                Run(connection, "INSERT INTO audit (timestamp, json) VALUES ($at, $json)",
                    ("$at", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                    ("$json", JsonSerializer.Serialize(entry, Options)));
            }
            Run(connection, "INSERT INTO meta (key, value) VALUES ('legacy_imported', '1') ON CONFLICT(key) DO UPDATE SET value = '1'");
        });

        RetireLegacyFile(LegacyIndexPath);
        RetireLegacyFile(LegacyConfigPath);
        RetireLegacyFile(LegacyAuditPath);
    }

    private static bool IsMigrated(SqliteConnection connection) =>
        ScalarText(connection, "SELECT value FROM meta WHERE key = 'legacy_imported'") == "1";

    private static void RetireLegacyFile(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Move(path, path + ".migrated", overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private List<AuditEntry> ReadLegacyAudit() =>
        File.ReadLines(LegacyAuditPath)
            .Select(line =>
            {
                try { return JsonSerializer.Deserialize<AuditEntry>(line, Options); }
                catch (JsonException) { return null; }
            })
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .ToList();

    private static void WriteSnapshot(SqliteConnection connection, IndexSnapshot snapshot)
    {
        var scannedAt = snapshot.ScannedAt.ToString("O", CultureInfo.InvariantCulture);
        Run(connection, """
            INSERT INTO snapshot (id, scanned_at, json) VALUES (1, $scanned, $json)
            ON CONFLICT(id) DO UPDATE SET scanned_at = excluded.scanned_at, json = excluded.json
            """,
            ("$scanned", scannedAt),
            ("$json", JsonSerializer.Serialize(snapshot, Options)));

        Run(connection, "INSERT INTO scans (scanned_at, project_count, toolchain_count) VALUES ($scanned, $projects, $toolchains)",
            ("$scanned", scannedAt),
            ("$projects", snapshot.Projects.Count),
            ("$toolchains", snapshot.Toolchains.Count));
        var scanId = ScalarLong(connection, "SELECT last_insert_rowid()");

        foreach (var project in snapshot.Projects)
        {
            Run(connection, """
                INSERT OR REPLACE INTO scan_projects (scan_id, project_key, path, name, state, digest)
                VALUES ($scan, $key, $path, $name, $state, $digest)
                """,
                ("$scan", scanId),
                ("$key", project.Key),
                ("$path", project.Path),
                ("$name", project.DisplayName),
                ("$state", project.State.ToString()),
                ("$digest", ProjectDigest(project)));
        }

        Run(connection, "DELETE FROM scans WHERE id NOT IN (SELECT id FROM scans ORDER BY id DESC LIMIT $limit)",
            ("$limit", ScanHistoryLimit));
    }

    private static void WriteConfig(SqliteConnection connection, StoreConfig config) =>
        Run(connection, """
            INSERT INTO config (id, json) VALUES (1, $json)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json
            """,
            ("$json", JsonSerializer.Serialize(config, Options)));

    private static IndexSnapshot ParseSnapshot(string json, string source, string description)
    {
        IndexSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<IndexSnapshot>(json, Options); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {description} is invalid and was left unchanged: {source}.", ex);
        }

        if (snapshot is null) throw new InvalidDataException($"The {description} is empty: {source}.");
        if (snapshot.SchemaVersion > 1) throw new InvalidDataException($"The {description} uses an unsupported schema: {source}.");

        // Recompute derived state that is never persisted.
        foreach (var project in snapshot.Projects)
        {
            foreach (var requirement in project.Requirements) requirement.Provenance ??= new Provenance();
        }
        return snapshot;
    }

    private static StoreConfig ParseConfig(string json, string source, string description)
    {
        StoreConfig? config;
        try { config = JsonSerializer.Deserialize<StoreConfig>(json, Options); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {description} is invalid and was left unchanged: {source}.", ex);
        }

        if (config is null) throw new InvalidDataException($"The {description} is empty: {source}.");
        if (config.SchemaVersion > 1) throw new InvalidDataException($"The {description} uses an unsupported schema: {source}.");
        return config;
    }

    /// <summary>
    /// Runs the work between BEGIN IMMEDIATE and COMMIT, so the write lock is taken before the first read.
    /// Plain SQL is used rather than BeginTransaction because the connection does not expose its transaction.
    /// </summary>
    private static void InTransaction(SqliteConnection connection, Action work)
    {
        Exec(connection, "BEGIN IMMEDIATE");
        try
        {
            work();
            Exec(connection, "COMMIT");
        }
        catch
        {
            try { Exec(connection, "ROLLBACK"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static string? ScalarText(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar() as string;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
