using DevTools.Core.Model;

namespace DevTools.Cli;

public static class Program
{
    public static async Task<int> Main(string[] argv)
    {
        // Windows terminals default to the OEM code page, which mangles the status glyphs.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* redirected or unsupported host */ }

        var args = Args.Parse(argv);

        try
        {
            return args.Command switch
            {
                "scan" => await Commands.Scan(args),
                "list" or "ls" => Commands.List(args),
                "show" => Commands.Show(args),
                "doctor" => Commands.Doctor(args),
                "toolchains" or "tools" => Commands.Toolchains(args),
                "frameworks" => Commands.Frameworks(args),
                "eol" => Commands.Eol(args),
                "audit" => Commands.Audit(args),
                "changes" => Commands.Changes(args),
                "verify-toolchain" => Commands.VerifyToolchain(args),
                "ignore" => Commands.Ignore(args, true),
                "unignore" => Commands.Ignore(args, false),
                "probe-effective" => Commands.ProbeEffective(args),
                "env" => Commands.Env(args),
                "shell" => Commands.Shell(args),
                "install" => Commands.Install(args),
                "update" => Commands.Update(args),
                "outdated" => Commands.Outdated(args),
                "prune" => Commands.Prune(args),
                "migrate" => Commands.Migrate(args),
                "disk" => Commands.Disk(args),
                "export" => Commands.Export(args),
                "roots" => Commands.Roots(args),
                "support" => Commands.Support(args),
                "snapshot" => Commands.Snapshot(),
                "environments" => Commands.Environments(args),
                "version" => Commands.Version(),
                "help" or "-h" or "--help" => Commands.Help(),
                _ => Commands.Unknown(args.Command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Out.RedText("error: ") + ex.Message);
            if (args.Has("verbose")) Console.Error.WriteLine(ex);
            return 2;
        }
    }
}

public static class Format
{
    public static string Relative(DateTimeOffset? when)
    {
        if (when is null) return "never";

        var span = DateTimeOffset.Now - when.Value;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 31) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    /// <summary>Middle-truncates a path so both the drive and the project name stay readable.</summary>
    public static string Path(string path, int width = 58)
    {
        if (path.Length <= width) return path;

        var tail = path[^(width - 12)..];
        return "…" + tail;
    }

    public static string Environments(IEnumerable<Ecosystem> ecosystems) =>
        string.Join(",", ecosystems.Select(ToolKinds.DisplayEcosystem));
}
