using System.ComponentModel;
using System.Diagnostics;

namespace DevTools.Providers;

public enum LaunchKind { Folder, Terminal, Editor }

public enum HostOs { Windows, MacOs, Linux }

public sealed record LaunchCommand(string File, IReadOnlyList<string> Arguments);

/// <summary>
/// Plans the command that opens a project folder in the file manager, a terminal or VS Code. Planning is pure,
/// so each platform's command can be tested without launching anything. Arguments are passed as a list and never
/// through a shell, except for Windows batch launchers, which are refused when the path contains shell metacharacters.
/// A platform with no portable command returns null instead of guessing.
/// </summary>
public static class ProjectLaunch
{
    private static readonly System.Buffers.SearchValues<char> ShellMetacharacters = System.Buffers.SearchValues.Create("&|<>^%!\"");

    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOs : HostOs.Linux;

    public static LaunchCommand? Plan(LaunchKind kind, string path, HostOs os, Func<string, string?> which)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        return kind switch
        {
            LaunchKind.Folder => os switch
            {
                HostOs.Windows => new LaunchCommand("explorer.exe", [path]),
                HostOs.MacOs => new LaunchCommand("open", [path]),
                _ => new LaunchCommand("xdg-open", [path])
            },
            LaunchKind.Terminal => os switch
            {
                HostOs.Windows => which("wt") is not null
                    ? new LaunchCommand("wt.exe", ["-d", path])
                    : new LaunchCommand("powershell.exe", ["-NoExit", "-Command", "Set-Location -LiteralPath '" + path.Replace("'", "''") + "'"]),
                HostOs.MacOs => new LaunchCommand("open", ["-a", "Terminal", path]),
                _ => null
            },
            LaunchKind.Editor => PlanEditor(path, os, which),
            _ => null
        };
    }

    public static LaunchCommand? Plan(LaunchKind kind, string path) => Plan(kind, path, CurrentOs, PathSolver.Which);

    private static LaunchCommand? PlanEditor(string path, HostOs os, Func<string, string?> which)
    {
        var code = which("code");
        if (code is null) return null;

        if (os != HostOs.Windows) return new LaunchCommand(code, [path]);

        // `code` is a .cmd shim on Windows and cannot be started without cmd.exe, which re-parses its arguments.
        var batch = code.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || code.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        if (!batch) return new LaunchCommand(code, [path]);
        return path.AsSpan().IndexOfAny(ShellMetacharacters) >= 0 ? null : new LaunchCommand("cmd.exe", ["/d", "/c", code, path]);
    }

    /// <summary>Starts the command detached. Returns an error message, or null when it started.</summary>
    public static string? Start(LaunchCommand command)
    {
        try
        {
            var info = new ProcessStartInfo(command.File) { UseShellExecute = false };
            foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            return process is null ? $"{command.File} did not start" : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return $"Could not start {command.File}: {ex.Message}";
        }
    }
}
