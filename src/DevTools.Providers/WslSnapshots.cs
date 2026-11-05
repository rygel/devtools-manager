using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevTools.Core.Model;

namespace DevTools.Providers;

public sealed record WslCleanupTarget(string Path, long? SizeBytes, string Plan, bool Available);
public sealed record WslMigrationEdit(string Project, string Path, string Change, string Before, string After);
public sealed record WslMigrationPlan(string Token, IReadOnlyList<WslMigrationEdit> Edits, IReadOnlyList<string> Skipped);

/// <summary>Read separately indexed WSL environments using fixed shell code and separately passed arguments.</summary>
public sealed class WslSnapshots(IProcessRunner? runner = null)
{
    private readonly IProcessRunner _runner = runner ?? ProcessRunner.Shared;
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public (IReadOnlyList<string> Names, string? Error) List()
    {
        var result = _runner.Run("wsl.exe", ["--list", "--quiet"], TimeSpan.FromSeconds(10), outputEncoding: Encoding.Unicode);
        if (!result.Succeeded) return ([], result.Error ?? (string.IsNullOrWhiteSpace(result.StdErr)
            ? $"WSL discovery failed with exit code {result.ExitCode}." : result.StdErr.Trim()));
        return (result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('\uFEFF', '\0')).Where(name => name.Length > 0).ToArray(), null);
    }

    public (IndexSnapshot? Snapshot, string? Error) Load(string distribution)
    {
        var result = RunInDistribution(distribution, ["snapshot", "--json"]);
        if (!result.Succeeded) return (null, "Could not read the WSL index. Install devtools in this distribution and run a scan. " + (result.Error ?? result.StdErr.Trim()));
        try
        {
            var snapshot = JsonSerializer.Deserialize<IndexSnapshot>(result.StdOut, JsonOptions);
            if (snapshot is null || snapshot.EnvironmentName != "WSL: " + distribution)
                return (null, "The index does not identify the selected WSL distribution.");
            return (snapshot, null);
        }
        catch (JsonException) { return (null, "The WSL command did not return a valid index."); }
    }

    public (string? Script, string? Error) Environment(string distribution, string projectPath)
    {
        var result = RunInDistribution(distribution, ["env", projectPath, "--shell", "sh"]);
        return result.Succeeded ? (result.StdOut, null) : (null, result.Error ?? result.StdErr.Trim());
    }

    public (bool Succeeded, string Message) Verify(string distribution, string toolchainPath)
    {
        var result = RunInDistribution(distribution, ["verify-toolchain", toolchainPath, "--yes"]);
        return (result.Succeeded, result.Succeeded ? result.StdOut.Trim() : result.Error ?? result.Combined.Trim());
    }

    public (bool Succeeded, string Message) Scan(string distribution, bool measure, bool noProbe = false)
    {
        var arguments = new List<string> { "scan", "--json" };
        if (measure) arguments.Add("--measure");
        if (noProbe) arguments.Add("--no-probe");
        var result = RunInDistribution(distribution, arguments, TimeSpan.FromMinutes(3));
        return (result.Succeeded, result.Succeeded ? "WSL scan completed." : result.Error ?? result.Combined.Trim());
    }

    public (InstallPlan? Plan, string? Error) InstallationPlan(string distribution)
    {
        var result = RunInDistribution(distribution, ["install", "--json"]);
        if (!result.Succeeded) return (null, result.Error ?? result.Combined.Trim());
        try { return (JsonSerializer.Deserialize<InstallPlan>(result.StdOut, JsonOptions), null); }
        catch (JsonException) { return (null, "The WSL installation plan could not be read."); }
    }

    public (bool Succeeded, string Message) Install(string distribution, PlannedStep step)
    {
        if (step.Manual) return (false, step.Hint ?? "This step requires manual action.");
        var result = RunInDistribution(distribution, ["install", "--tool", step.Tool, "--version", step.Version,
            "--plan", step.Plan, "--apply", "--yes"], TimeSpan.FromMinutes(11));
        return (result.Succeeded, result.Succeeded ? "WSL installation completed; rescan to refresh inventory." : result.Error ?? result.Combined.Trim());
    }

    public (WslMigrationPlan? Plan, string? Error) MigrationPlan(string distribution, MigrationOptions options)
    {
        var result = RunInDistribution(distribution, MigrationArguments(options, apply: false, token: null), TimeSpan.FromMinutes(3));
        if (!result.Succeeded) return (null, result.Error ?? result.Combined.Trim());
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var token = root.GetProperty("plan").GetString() ?? "";
            var edits = root.GetProperty("edits").EnumerateArray().Select(edit => new WslMigrationEdit(
                edit.GetProperty("project").GetString() ?? "", edit.GetProperty("path").GetString() ?? "",
                edit.GetProperty("change").GetString() ?? "", edit.GetProperty("before").GetString() ?? "",
                edit.GetProperty("after").GetString() ?? "")).ToArray();
            var skipped = root.GetProperty("skipped").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            return token.Length == 0 ? (null, "The WSL migration plan has no token.") : (new(token, edits, skipped), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return (null, "The WSL migration plan is incomplete; update the WSL CLI."); }
    }

    public (bool Succeeded, string Message) Migrate(string distribution, MigrationOptions options, string token)
    {
        var result = RunInDistribution(distribution, MigrationArguments(options, apply: true, token), TimeSpan.FromMinutes(3));
        if (!result.Succeeded) return (false, result.Error ?? result.Combined.Trim());
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var backup = document.RootElement.GetProperty("backupDirectory").GetString();
            var count = document.RootElement.GetProperty("items").GetArrayLength();
            return (true, $"Updated {count} WSL files; rescan to refresh the index. Backups: {backup}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { return (false, "The WSL command completed but did not return a valid result; check its audit log."); }
    }

    private static List<string> MigrationArguments(MigrationOptions options, bool apply, string? token)
    {
        var arguments = new List<string> { "migrate", ToolKinds.Display(options.Tool), "--to", options.Target, "--json" };
        if (options.From is not null) { arguments.Add("--from"); arguments.Add(options.From); }
        if (options.IncludeRanges) arguments.Add("--include-ranges");
        if (apply) { arguments.Add("--apply"); arguments.Add("--yes"); arguments.Add("--plan"); arguments.Add(token ?? ""); }
        return arguments;
    }

    public (IReadOnlyList<WslCleanupTarget> Targets, string? Error) CleanupPlan(string distribution, string projectPath)
    {
        var result = RunInDistribution(distribution, ["prune", "--project", projectPath, "--measure", "--json"], TimeSpan.FromMinutes(2));
        if (!result.Succeeded) return ([], result.Error ?? result.Combined.Trim());
        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var targets = document.RootElement.GetProperty("targets").EnumerateArray().Select(item => new WslCleanupTarget(
                item.GetProperty("path").GetString() ?? "",
                !item.TryGetProperty("sizeBytes", out var size) || size.ValueKind == JsonValueKind.Null ? null : size.GetInt64(),
                item.GetProperty("plan").GetString() ?? "",
                item.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True)).ToArray();
            if (targets.Any(t => t.Path.Length == 0 || t.Plan.Length == 0)) return ([], "The WSL cleanup plan is incomplete.");
            return (targets, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return ([], "The WSL cleanup plan could not be read."); }
    }

    public (bool Succeeded, string Message) Cleanup(string distribution, string projectPath, WslCleanupTarget target)
    {
        var name = target.Path.Split('/').LastOrDefault() ?? "";
        if (!target.Available || !ArtifactCleanup.IsKnownName(name)) return (false, "The WSL cleanup target is unavailable or unsupported.");
        var result = RunInDistribution(distribution, ["prune", "--project", projectPath, "--artifact", name,
            "--apply", "--yes", "--plan", target.Plan], TimeSpan.FromMinutes(3));
        return (result.Succeeded, result.Succeeded ? "Moved the selected WSL target to trash." : result.Error ?? result.Combined.Trim());
    }

    private ProcessResult RunInDistribution(string distribution, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        const string command = "if [ -z \"$DOTNET_ROOT\" ] && [ -x \"$HOME/.dotnet/dotnet\" ]; then export DOTNET_ROOT=\"$HOME/.dotnet\"; fi; seconds=$1; shift; if [ -n \"$DEVTOOLS_WSL_BINARY\" ]; then exec timeout \"$seconds\" \"$DEVTOOLS_WSL_BINARY\" \"$@\"; elif [ -x \"$HOME/.local/bin/devtools\" ]; then exec timeout \"$seconds\" \"$HOME/.local/bin/devtools\" \"$@\"; else exec timeout \"$seconds\" devtools \"$@\"; fi";
        var wslArguments = new List<string> { "--distribution", distribution, "--exec", "/bin/sh", "-c", command, "devtools", Math.Max(1, (int)limit.TotalSeconds - 5).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        wslArguments.AddRange(arguments);
        return _runner.Run("wsl.exe", wslArguments, limit);
    }
}
