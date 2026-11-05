using System.Text;
using System.Text.Json;

namespace DevTools.Providers;

/// <summary>One resolved package, in the ecosystem naming OSV uses ("npm", "crates.io").</summary>
public sealed record Dependency(string Ecosystem, string Name, string Version);

public sealed record DependencyRead(IReadOnlyList<Dependency> Dependencies, IReadOnlyList<string> Files, IReadOnlyList<string> Errors);

/// <summary>
/// Reads resolved dependency versions from lockfiles. Only lockfiles are used because a manifest range
/// ("^4.17.0") does not say which version is installed, and a vulnerability check on a guess is worse than none.
/// </summary>
public static class DependencyReader
{
    private const string NpmLockFile = "package-lock.json";
    private const string CargoLockFile = "Cargo.lock";
    private const int MaxLockfileBytes = 64 * 1024 * 1024;

    public static DependencyRead Read(string projectDirectory)
    {
        var dependencies = new List<Dependency>();
        var files = new List<string>();
        var errors = new List<string>();

        foreach (var (name, parse) in new (string, Func<string, IReadOnlyList<Dependency>>)[]
                 {
                     (NpmLockFile, ParseNpmLock),
                     (CargoLockFile, ParseCargoLock)
                 })
        {
            var path = Path.Combine(projectDirectory, name);
            if (!File.Exists(path)) continue;

            try
            {
                if (new FileInfo(path).Length > MaxLockfileBytes) throw new InvalidDataException("file is larger than 64 MiB");
                dependencies.AddRange(parse(File.ReadAllText(path)));
                files.Add(name);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return new DependencyRead(dependencies.Distinct().ToList(), files, errors);
    }

    /// <summary>The <c>packages</c> map of lockfile versions 2 and 3. Older lockfiles have no such map and give nothing.</summary>
    public static IReadOnlyList<Dependency> ParseNpmLock(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("not JSON: " + ex.Message, ex); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("packages", out var packages) ||
                packages.ValueKind != JsonValueKind.Object)
                return [];

            const string Marker = "node_modules/";
            var result = new List<Dependency>();
            foreach (var entry in packages.EnumerateObject())
            {
                var at = entry.Name.LastIndexOf(Marker, StringComparison.Ordinal);
                if (at < 0 || entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (entry.Value.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True) continue;
                if (!entry.Value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) continue;

                result.Add(new Dependency("npm", entry.Name[(at + Marker.Length)..], version.GetString()!));
            }

            return result.Distinct().ToList();
        }
    }

    /// <summary>Registry crates from <c>Cargo.lock</c>. Workspace and path crates have no <c>source</c> and are not published packages.</summary>
    public static IReadOnlyList<Dependency> ParseCargoLock(string text)
    {
        var result = new List<Dependency>();
        string? name = null, version = null, source = null;

        void Flush()
        {
            if (name is not null && version is not null && source is not null && source.Contains("crates.io", StringComparison.Ordinal))
                result.Add(new Dependency("crates.io", name, version));
            name = version = source = null;
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line == "[[package]]") { Flush(); continue; }
            if (line.StartsWith('['))
            {
                Flush();
                continue;
            }

            var value = ValueOf(line, "name") ?? ValueOf(line, "version") ?? ValueOf(line, "source");
            if (value is null) continue;
            if (line.StartsWith("name", StringComparison.Ordinal)) name = value;
            else if (line.StartsWith("version", StringComparison.Ordinal)) version = value;
            else source = value;
        }

        Flush();
        return result.Distinct().ToList();
    }

    private static string? ValueOf(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.Ordinal)) return null;
        var rest = line[key.Length..].TrimStart();
        if (!rest.StartsWith('=')) return null;
        rest = rest[1..].Trim();
        return rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"' ? rest[1..^1] : null;
    }
}

public sealed record Vulnerability(string Id, string? Summary, string? Severity);

public sealed record DependencyFinding(Dependency Dependency, IReadOnlyList<Vulnerability> Vulnerabilities);

/// <param name="Checked">How many distinct packages were sent.</param>
/// <param name="Complete">False when anything failed, so an empty list must not be read as "clean".</param>
public sealed record AuditResult(IReadOnlyList<DependencyFinding> Vulnerable, IReadOnlyList<string> Errors, int Checked)
{
    public bool Complete => Errors.Count == 0;
}

/// <summary>
/// Looks up known vulnerabilities on osv.dev. The only data sent is each package's ecosystem, name and version.
/// Callers decide when to run it; nothing here runs on its own.
/// </summary>
public sealed class OsvClient
{
    public const string BaseUrl = "https://api.osv.dev/v1/";
    public const int BatchSize = 500;
    public const int MaxDetailLookups = 200;
    private const int BodyPreviewLength = 300;

    private readonly HttpMessageHandler? _handler;

    public OsvClient(HttpMessageHandler? handler = null) => _handler = handler;

    public AuditResult Audit(IReadOnlyList<Dependency> dependencies, CancellationToken cancellationToken = default)
    {
        var distinct = dependencies.Distinct().ToList();
        var errors = new List<string>();
        var hits = new List<(Dependency Dependency, List<string> Ids)>();
        if (distinct.Count == 0) return new AuditResult([], errors, 0);

        using var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("devtools-manager");

        foreach (var chunk in distinct.Chunk(BatchSize))
        {
            if (cancellationToken.IsCancellationRequested) { errors.Add("cancelled"); break; }
            QueryBatch(client, chunk, hits, errors, cancellationToken);
        }

        var ids = hits.SelectMany(hit => hit.Ids).Distinct().ToList();
        var details = new Dictionary<string, Vulnerability>();
        foreach (var id in ids.Take(MaxDetailLookups))
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (FetchDetail(client, id, errors, cancellationToken) is { } detail) details[id] = detail;
        }

        if (ids.Count > MaxDetailLookups)
            errors.Add($"details for {ids.Count - MaxDetailLookups} more vulnerabilities were not fetched; their identifiers are listed without a summary");

        var findings = hits.Select(hit => new DependencyFinding(hit.Dependency,
            hit.Ids.Select(id => details.GetValueOrDefault(id) ?? new Vulnerability(id, null, null)).ToList())).ToList();
        return new AuditResult(findings, errors, distinct.Count);
    }

    private static void QueryBatch(HttpClient client, Dependency[] chunk, List<(Dependency, List<string>)> hits, List<string> errors, CancellationToken token)
    {
        var url = BaseUrl + "querybatch";
        var payload = JsonSerializer.Serialize(new
        {
            queries = chunk.Select(d => new { package = new { name = d.Name, ecosystem = d.Ecosystem }, version = d.Version })
        });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            using var response = client.Send(request, token);
            var body = ReadBody(response, token);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"OSV batch: HTTP {(int)response.StatusCode} from {url}: {Preview(body)}");
                return;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() != chunk.Length)
            {
                errors.Add($"OSV batch: expected {chunk.Length} results from {url} but the response was: {Preview(body)}");
                return;
            }

            var position = 0;
            foreach (var result in results.EnumerateArray())
            {
                var dependency = chunk[position++];
                if (result.TryGetProperty("next_page_token", out _))
                    errors.Add($"OSV returned more vulnerabilities for {dependency.Name} {dependency.Version} than one page; the list is truncated");
                if (!result.TryGetProperty("vulns", out var vulns) || vulns.ValueKind != JsonValueKind.Array) continue;

                var ids = vulns.EnumerateArray()
                    .Select(vuln => vuln.TryGetProperty("id", out var id) ? id.GetString() : null)
                    .OfType<string>().ToList();
                if (ids.Count > 0) hits.Add((dependency, ids));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            errors.Add($"OSV batch: {ex.Message} ({url})");
        }
    }

    private static Vulnerability? FetchDetail(HttpClient client, string id, List<string> errors, CancellationToken token)
    {
        var url = BaseUrl + "vulns/" + Uri.EscapeDataString(id);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = client.Send(request, token);
            var body = ReadBody(response, token);
            if (!response.IsSuccessStatusCode)
            {
                errors.Add($"OSV {id}: HTTP {(int)response.StatusCode} from {url}: {Preview(body)}");
                return null;
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? severity = null;
            if (root.TryGetProperty("database_specific", out var specific) && specific.ValueKind == JsonValueKind.Object &&
                specific.TryGetProperty("severity", out var level) && level.ValueKind == JsonValueKind.String)
                severity = level.GetString();
            return new Vulnerability(id, summary, severity);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            errors.Add($"OSV {id}: {ex.Message} ({url})");
            return null;
        }
    }

    private static string ReadBody(HttpResponseMessage response, CancellationToken token)
    {
        using var reader = new StreamReader(response.Content.ReadAsStream(token));
        return reader.ReadToEnd();
    }

    private static string Preview(string body) => body.Length <= BodyPreviewLength ? body : body[..BodyPreviewLength] + "…";
}
