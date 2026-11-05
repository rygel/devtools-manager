using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Providers;
using DevTools.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevTools.Server;

public sealed class UiOptions
{
    public int Port { get; init; } = 5757;

    public string StoreDirectory { get; init; } = "";

    public bool OpenBrowser { get; init; } = true;
}

/// <summary>
/// A local-only web surface over the same core the CLI uses. Loopback binding, no auth
/// token needed for v1 because nothing leaves the machine, and the UI ships embedded in
/// the assembly so a published single file has no missing assets.
/// </summary>
public static class UiServer
{
    public static bool IsAllowedRequest(string host, int? port, int expectedPort, string method, string path, string origin, string requestHeader)
    {
        if (port != expectedPort || !(string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1")) return false;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase)) return true;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)) return true;
        return string.Equals(origin, $"http://{host}:{port}", StringComparison.OrdinalIgnoreCase) && requestHeader == "1";
    }
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly SemaphoreSlim ScanGate = new(1, 1);

    public static async Task<int> RunAsync(UiOptions options)
    {
        var store = new SqliteStore(string.IsNullOrEmpty(options.StoreDirectory)
            ? PlatformPaths.ApplicationData
            : options.StoreDirectory);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = []
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(log => log.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            // Loopback alone does not prevent a hostile DNS name resolving to this server.
            var host = context.Request.Host;
            if (!IsAllowedRequest(host.Host, host.Port, options.Port, context.Request.Method,
                    context.Request.Path.Value ?? "", context.Request.Headers.Origin.ToString(), context.Request.Headers["X-DevTools-Request"].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var asset = EmbeddedAssets.Resolve(context.Request.Path.Value ?? "/");
            if (asset is null)
            {
                await next();
                return;
            }

            context.Response.ContentType = asset.Value.ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(asset.Value.Bytes);
        });

        // ---------------------------------------------------------------- API

        app.MapGet("/api/state", () =>
        {
            var snapshot = store.Load();
            if (snapshot is null)
            {
                return Results.Json(new
                {
                    machine = new { scannedAt = (DateTimeOffset?)null, roots = Array.Empty<string>(), empty = true },
                    projects = Array.Empty<object>(),
                    toolchains = Array.Empty<object>(),
                    missing = Array.Empty<object>(),
                    conflicts = Array.Empty<object>()
                }, Json);
            }

            return Results.Json(BuildState(store, snapshot), Json);
        });

        app.MapPost("/api/scan", async (ScanBody? body) =>
        {
            if (!await ScanGate.WaitAsync(0))
                return Results.Json(new { error = "a scan is already running" }, Json, statusCode: 409);

            try
            {
                using var scanLock = store.TryAcquireScanLock();
                if (scanLock is null)
                    return Results.Json(new { error = "another scan is already using this index" }, Json, statusCode: 409);
                var config = store.LoadConfig();
                var roots = body?.Roots is { Count: > 0 } ? body.Roots : config.Roots;
                if (roots.Count == 0) roots = StoreConfig.WithDefaults().Roots;

                if (roots.Count == 0)
                    return Results.Json(new { error = "no scan roots configured" }, Json, statusCode: 400);

                var snapshot = await new ScanService().ScanAsync(new ScanRequest
                {
                    Roots = roots,
                    MaxDepth = body?.Depth ?? config.MaxDepth,
                    Excludes = config.Excludes,
                    FollowSymlinks = config.FollowSymlinks,
                    MeasureSizes = body?.Measure ?? false,
                    MeasureToolchains = body?.Measure ?? false
                }, store.Load());

                store.Save(snapshot);
                config.Roots = roots;
                config.Initialised = true;
                store.SaveConfig(config);

                return Results.Json(new
                {
                    ok = true,
                    stats = snapshot.Stats,
                    projects = snapshot.Projects.Count,
                    toolchains = snapshot.Toolchains.Count
                }, Json);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, Json, statusCode: 500);
            }
            finally
            {
                ScanGate.Release();
            }
        });

        app.MapGet("/api/project", (string key) =>
        {
            var snapshot = store.Load();
            if (snapshot is null) return Results.NotFound();

            var project = snapshot.Projects.FirstOrDefault(p =>
                p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            if (project is null) return Results.NotFound();

            var engine = new ResolutionEngine(snapshot.Toolchains);
            var view = new ProjectView { Project = project, Resolution = engine.Resolve(project) };
            var patch = EnvComposer.Compose(view, engine);

            return Results.Json(new
            {
                project = ProjectDto(project, view.Resolution),
                env = new { set = patch.Set, pathPrepend = patch.PathPrepend, notes = patch.Notes },
                diagnostics = project.Diagnostics
            }, Json);
        });

        app.MapGet("/api/plan", () =>
        {
            var snapshot = store.Load();
            if (snapshot is null) return Results.NotFound();
            return Results.Json(InstallPlanner.Build(MachineView.Build(snapshot)), Json);
        });

        app.MapGet("/api/audit", () => Results.Json(store.ReadAudit(100), Json));

        // ---------------------------------------------------------------- lifecycle

        try
        {
            await app.StartAsync();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"could not bind port {options.Port}: {ex.Message}");
            return 2;
        }

        var url = $"http://localhost:{options.Port}";
        Console.WriteLine($"  listening on {url}");
        Console.WriteLine("  press Ctrl+C to stop");

        if (options.OpenBrowser) TryOpenBrowser(url);

        await app.WaitForShutdownAsync();
        return 0;
    }

    private static object BuildState(SqliteStore store, IndexSnapshot snapshot)
    {
        var view = MachineView.Build(snapshot);
        var config = store.LoadConfig();

        return new
        {
            machine = new
            {
                hostname = snapshot.HostName,
                platform = snapshot.Platform,
                environment = snapshot.EnvironmentName,
                scannedAt = snapshot.ScannedAt,
                roots = config.Roots,
                stats = view.Stats,
                durationMs = snapshot.Stats.DurationMs,
                dirsVisited = snapshot.Stats.DirsVisited,
                dirsPruned = snapshot.Stats.DirsPruned,
                scanErrors = snapshot.Stats.Errors,
                partial = snapshot.Stats.Partial
            },
            projects = view.Projects.Select(p => ProjectDto(p.Project, p.Resolution)),
            toolchains = snapshot.Toolchains.Select(t => new
            {
                tool = ToolKinds.Display(t.Tool),
                t.Version,
                t.Manager,
                t.Path,
                globalDefault = t.IsGlobalDefault,
                health = t.Health.ToString().ToLowerInvariant(),
                usedBy = view.Projects.Count(p => p.Resolution.Tools.Any(x =>
                    string.Equals(x.Toolchain?.Path, t.Path, StringComparison.OrdinalIgnoreCase)))
            }),
            missing = view.Stats.Missing,
            conflicts = view.Conflicts.Select(c => new { c.Tool, text = c.Describe() })
        };
    }

    private static object ProjectDto(ProjectRecord project, ProjectResolution resolution) => new
    {
        key = project.Key,
        name = project.DisplayName,
        path = project.Path,
        ecosystems = project.Ecosystems.Select(ToolKinds.DisplayEcosystem),
        state = project.State.ToString().ToLowerInvariant(),
        project.Stale,
        project.Incomplete,
        confidence = Math.Round(project.Confidence, 2),
        git = project.GitRemote,
        branch = project.GitBranch,
        lastActivity = project.LastActivityAt,
        lastScan = project.LastSeenAt,
        size = project.SizeBytes,
        artifacts = project.ArtifactBytes,
        problemCount = resolution.ProblemCount + (project.Incomplete || project.Stale ? 1 : 0),
        tools = resolution.Tools.Select(t => new
        {
            tool = ToolKinds.Display(t.Tool),
            required = t.ConstraintDisplay,
            status = t.Status.ToString().ToLowerInvariant(),
            installed = t.Toolchain?.Version,
            manager = t.Toolchain?.Manager,
            source = t.Requirement?.Provenance.Describe(),
            strength = t.Requirement?.Strength.ToString().ToLowerInvariant(),
            detail = t.Detail,
            nearest = t.Nearest?.Version
        })
    };

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // Headless session: the printed URL is enough.
        }
    }

    public sealed class ScanBody
    {
        public List<string>? Roots { get; set; }
        public bool? Measure { get; set; }
        public int? Depth { get; set; }
    }
}
