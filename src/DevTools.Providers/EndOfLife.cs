using System.Globalization;
using System.Text.Json;
using DevTools.Core.Model;
using DevTools.Core.Versioning;

namespace DevTools.Providers;

/// <summary>One release line of a product, as endoflife.date lists it.</summary>
/// <param name="Cycle">The release line, for example "20" for Node or "3.12" for Python.</param>
/// <param name="EndDate">When support ends, or null when no date is given.</param>
/// <param name="Ended">True when the source says the line has ended without giving a date.</param>
/// <param name="Latest">The newest release in this line, when the source gives one.</param>
public sealed record EolCycle(string Cycle, DateOnly? EndDate, bool Ended, string? Latest = null);

public enum EolState
{
    /// <summary>No data, or no release line matches the version. Never guessed.</summary>
    Unknown,
    Supported,
    EndingSoon,
    Ended
}

public sealed record EolAssessment(EolState State, string? Cycle, DateOnly? EndDate)
{
    public static EolAssessment None { get; } = new(EolState.Unknown, null, null);

    public string Describe() => State switch
    {
        EolState.Ended => EndDate is null ? "end of life" : "end of life since " + Iso(EndDate.Value),
        EolState.EndingSoon => "ends " + Iso(EndDate!.Value),
        EolState.Supported => EndDate is null ? "supported" : "supported until " + Iso(EndDate.Value),
        _ => "no end-of-life data"
    };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Which endoflife.date product covers each tool. Every id here was confirmed to answer HTTP 200.</summary>
public static class EndOfLifeProducts
{
    private static readonly Dictionary<ToolKind, string> Products = new()
    {
        [ToolKind.Node] = "nodejs",
        [ToolKind.Python] = "python",
        [ToolKind.Jdk] = "eclipse-temurin",
        [ToolKind.DotNetSdk] = "dotnet",
        [ToolKind.Go] = "go",
        [ToolKind.Rustc] = "rust",
        [ToolKind.Ruby] = "ruby",
        [ToolKind.Php] = "php",
        [ToolKind.Deno] = "deno",
        [ToolKind.Bun] = "bun",
        [ToolKind.Kotlin] = "kotlin",
        [ToolKind.Scala] = "scala",
        [ToolKind.Elixir] = "elixir",
        [ToolKind.Terraform] = "terraform",
        [ToolKind.Julia] = "julia",
        [ToolKind.Gradle] = "gradle",
        [ToolKind.Pnpm] = "pnpm",
        [ToolKind.Yarn] = "yarn",
        [ToolKind.Bazel] = "bazel"
    };

    public static string? For(ToolKind tool) => Products.GetValueOrDefault(tool);
}

public static class EndOfLifeParser
{
    /// <summary>Reads the list returned by <c>/api/{product}.json</c>. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static IReadOnlyList<EolCycle> Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("not JSON: " + ex.Message, ex); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("expected a list of release cycles");

            var cycles = new List<EolCycle>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("cycle", out var cycle)) continue;
                var name = cycle.ValueKind switch
                {
                    JsonValueKind.String => cycle.GetString(),
                    JsonValueKind.Number => cycle.GetRawText(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(name)) continue;

                DateOnly? date = null;
                var ended = false;
                if (item.TryGetProperty("eol", out var eol))
                {
                    if (eol.ValueKind == JsonValueKind.True) ended = true;
                    else if (eol.ValueKind == JsonValueKind.String &&
                             DateOnly.TryParseExact(eol.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        date = parsed;
                }

                string? latest = null;
                if (item.TryGetProperty("latest", out var latestElement))
                {
                    latest = latestElement.ValueKind switch
                    {
                        JsonValueKind.String => latestElement.GetString(),
                        JsonValueKind.Number => latestElement.GetRawText(),
                        _ => null
                    };
                }

                cycles.Add(new EolCycle(name, date, ended, string.IsNullOrWhiteSpace(latest) ? null : latest));
            }

            return cycles;
        }
    }
}

public static class EndOfLifeAssessor
{
    public const int SoonDays = 90;

    public static EolAssessment Assess(IReadOnlyList<EolCycle>? cycles, ToolKind tool, string version, DateOnly today)
    {
        var match = FindCycle(cycles, tool, version);
        if (match is null) return EolAssessment.None;

        if (match.Ended) return new EolAssessment(EolState.Ended, match.Cycle, match.EndDate);
        if (match.EndDate is not { } end) return new EolAssessment(EolState.Supported, match.Cycle, null);
        if (end <= today) return new EolAssessment(EolState.Ended, match.Cycle, end);
        return new EolAssessment(end.DayNumber - today.DayNumber <= SoonDays ? EolState.EndingSoon : EolState.Supported, match.Cycle, end);
    }

    /// <summary>The release line a version belongs to, or null when none matches. Never guessed.</summary>
    internal static EolCycle? FindCycle(IReadOnlyList<EolCycle>? cycles, ToolKind tool, string version)
    {
        if (cycles is null || cycles.Count == 0 || string.IsNullOrWhiteSpace(version)) return null;

        var segments = version.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        // Java 8 and earlier are written 1.8.0; the release line is "8".
        if (tool == ToolKind.Jdk && segments.Count > 1 && segments[0] == "1") segments.RemoveAt(0);

        return cycles
            .Select(cycle => (Cycle: cycle, Parts: cycle.Cycle.Split('.')))
            .Where(candidate => candidate.Parts.Length <= segments.Count &&
                                candidate.Parts.Select((part, index) => segments[index] == part).All(same => same))
            .OrderByDescending(candidate => candidate.Parts.Length)
            .Select(candidate => candidate.Cycle)
            .FirstOrDefault();
    }

    public static EolAssessment Assess(IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>> data, Toolchain toolchain, DateOnly today) =>
        Assess(data.GetValueOrDefault(toolchain.Tool), toolchain.Tool, toolchain.Version, today);
}

public sealed record EolRefreshResult(int Fetched, int FromCache, IReadOnlyList<string> Errors);

/// <summary>
/// Fetches release-cycle data from endoflife.date and keeps it in a small cache file. This is the only code
/// that contacts the network for end-of-life data, and callers decide when to run it. A failed fetch never
/// replaces data already cached.
/// </summary>
public sealed class EndOfLifeService
{
    public const string BaseUrl = "https://endoflife.date/api/";
    private const int BodyPreviewLength = 300;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _cachePath;
    private readonly HttpMessageHandler? _handler;
    private readonly TimeSpan _timeToLive;

    public EndOfLifeService(string cacheDirectory, HttpMessageHandler? handler = null, TimeSpan? timeToLive = null)
    {
        _cachePath = Path.Combine(cacheDirectory, "eol-cache.json");
        _handler = handler;
        _timeToLive = timeToLive ?? TimeSpan.FromDays(7);
    }

    private sealed record Entry(DateTimeOffset FetchedAt, List<EolCycle> Cycles);

    private sealed class CacheFile
    {
        public Dictionary<string, Entry> Products { get; set; } = [];
    }

    /// <summary>Cached data per tool. Missing or unreadable cache is an empty result.</summary>
    public IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>> Load()
    {
        var cache = ReadCache();
        var result = new Dictionary<ToolKind, IReadOnlyList<EolCycle>>();
        foreach (var tool in Enum.GetValues<ToolKind>())
        {
            if (EndOfLifeProducts.For(tool) is { } product && cache.Products.TryGetValue(product, out var entry))
                result[tool] = entry.Cycles;
        }

        return result;
    }

    public EolRefreshResult Refresh(IEnumerable<ToolKind> tools, DateTimeOffset now, bool force = false, CancellationToken cancellationToken = default)
    {
        var cache = ReadCache();
        var products = tools.Select(EndOfLifeProducts.For).OfType<string>().Distinct().ToList();
        var errors = new List<string>();
        int fetched = 0, cached = 0;
        var changed = false;

        using var client = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("devtools-manager");

        foreach (var product in products)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!force && cache.Products.TryGetValue(product, out var existing) && now - existing.FetchedAt < _timeToLive)
            {
                cached++;
                continue;
            }

            var url = BaseUrl + product + ".json";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = client.Send(request, cancellationToken);
                using var reader = new StreamReader(response.Content.ReadAsStream(cancellationToken));
                var body = reader.ReadToEnd();
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{product}: HTTP {(int)response.StatusCode} from {url}: {Preview(body)}");
                    continue;
                }

                var cycles = EndOfLifeParser.Parse(body);
                cache.Products[product] = new Entry(now, cycles.ToList());
                fetched++;
                changed = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
            {
                errors.Add($"{product}: {ex.Message} ({url})");
            }
        }

        if (changed) WriteCache(cache);
        return new EolRefreshResult(fetched, cached, errors);
    }

    private static string Preview(string body) => body.Length <= BodyPreviewLength ? body : body[..BodyPreviewLength] + "…";

    private CacheFile ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return new CacheFile();
            return JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath), Options) ?? new CacheFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged cache only costs a re-fetch.
            return new CacheFile();
        }
    }

    private void WriteCache(CacheFile cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temporary = _cachePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(cache, Options));
        File.Move(temporary, _cachePath, overwrite: true);
    }
}
