using System.Globalization;

namespace DevTools.Scan;

public sealed record GitProjectInfo(string? Remote, string? Branch, DateTimeOffset? LastCommitAt, bool IsWorktree);

/// <summary>
/// Reads just enough of <c>.git</c> to name a project and date it. Deliberately not a git
/// library: no packfiles, no refs walking, no subprocess. Anything that would need the
/// object database is out of scope — we only want "which repo is this, and when did it
/// last change".
/// </summary>
public static class GitInfo
{
    public static GitProjectInfo Read(string projectDirectory, IProjectFileSystem fs)
    {
        var gitDirectoryRelative = ".git";
        var isWorktree = false;

        if (fs.FileExists(".git"))
        {
            // Worktree or submodule: ".git" is a file containing "gitdir: <path>".
            var pointer = fs.ReadText(".git", 8_192);
            if (pointer is not null && pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                isWorktree = true;
                var target = pointer["gitdir:".Length..].Trim().Replace('\\', '/');
                gitDirectoryRelative = target;
            }
        }
        else if (!fs.DirectoryExists(".git"))
        {
            return new GitProjectInfo(null, null, null, false);
        }

        var config = fs.ReadText(Combine(gitDirectoryRelative, "config"), 256_000);
        var remote = ParseOriginUrl(config);

        var head = fs.ReadText(Combine(gitDirectoryRelative, "HEAD"), 4_096)?.Trim();
        var branch = head is not null && head.StartsWith("ref: refs/heads/", StringComparison.Ordinal)
            ? head["ref: refs/heads/".Length..].Trim()
            : null;

        var lastCommit = ParseLastCommitDate(fs.ReadText(Combine(gitDirectoryRelative, "logs/HEAD"), 512_000));

        return new GitProjectInfo(remote, branch, lastCommit, isWorktree);
    }

    private static string Combine(string a, string b) =>
        a.Replace('\\', '/').TrimEnd('/') + "/" + b;

    /// <summary>Reads <c>url =</c> from the <c>[remote "origin"]</c> section.</summary>
    internal static string? ParseOriginUrl(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;

        var inOrigin = false;
        foreach (var rawLine in config.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith('['))
            {
                inOrigin = line.StartsWith("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim();
            if (!key.Equals("url", StringComparison.OrdinalIgnoreCase)) continue;

            return Normalize(line[(equals + 1)..].Trim());
        }

        return null;
    }

    /// <summary>git@github.com:acme/api.git → github.com/acme/api</summary>
    internal static string Normalize(string url)
    {
        var value = url.Trim();
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = value.IndexOf(':');
            if (colon > 4) value = value[4..colon] + "/" + value[(colon + 1)..];
        }
        else if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            value = value["ssh://".Length..];
            var at = value.IndexOf('@');
            if (at >= 0) value = value[(at + 1)..];
            var slash = value.IndexOf('/');
            if (slash > 0)
            {
                var host = value[..slash];
                var portColon = host.IndexOf(':');
                if (portColon > 0) host = host[..portColon];
                value = host + value[slash..];
            }
        }
        else if (value.Contains("://", StringComparison.Ordinal))
        {
            var scheme = value.IndexOf("://", StringComparison.Ordinal) + 3;
            value = value[scheme..];
            var at = value.IndexOf('@');
            if (at >= 0) value = value[(at + 1)..];
            value = value.TrimEnd('/');
        }

        return value;
    }

    /// <summary>Last line of <c>.git/logs/HEAD</c>: <c>&lt;old&gt; &lt;new&gt; name &lt;email&gt; &lt;epoch&gt; &lt;tz&gt; msg</c>.</summary>
    internal static DateTimeOffset? ParseLastCommitDate(string? log)
    {
        if (string.IsNullOrWhiteSpace(log)) return null;

        var lines = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = lines.Length - 1; i >= 0 && i >= lines.Length - 20; i--)
        {
            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6) continue;

            // The timestamp is the last all-digit token before the timezone.
            for (var p = parts.Length - 2; p >= 4; p--)
            {
                if (!long.TryParse(parts[p], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                    continue;

                // Plausible Unix time: 2000-01-01 .. 2100-01-01
                if (epoch is < 946684800 or > 4102444800) continue;

                return DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();
            }
        }

        return null;
    }
}
