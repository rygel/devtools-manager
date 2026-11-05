namespace DevTools.Scan;

/// <summary>
/// The file access a parser needs, scoped to one project directory and rooted so that
/// no relative path can escape it. Exists as an interface so parser tests need no disk.
/// </summary>
public interface IProjectFileSystem
{
    bool FileExists(string relativePath);

    bool DirectoryExists(string relativePath);

    /// <summary>Reads at most <paramref name="maxBytes"/>. Returns null when unreadable.</summary>
    string? ReadText(string relativePath, int maxBytes = 1_000_000);

    /// <summary>File names (not paths) inside a relative directory, matching a simple glob.</summary>
    IReadOnlyList<string> ListFiles(string relativeDirectory, string pattern, bool recursive = false);

    /// <summary>Sub-directory names inside a relative directory.</summary>
    IReadOnlyList<string> ListDirectories(string relativeDirectory);
}

/// <summary>Rooted on-disk implementation with traversal and size guards.</summary>
public sealed class PhysicalProjectFileSystem : IProjectFileSystem
{
    private readonly string _root;

    public PhysicalProjectFileSystem(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    private string Resolve(string relativePath)
    {
        var combined = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        // Defense in depth: a parser must never read outside its project.
        if (!combined.StartsWith(_root, StringComparison.Ordinal) &&
            !string.Equals(combined, _root, StringComparison.Ordinal))
            throw new InvalidOperationException($"path escapes project root: {relativePath}");

        return combined;
    }

    public bool FileExists(string relativePath)
    {
        try { return File.Exists(Resolve(relativePath)); }
        catch { return false; }
    }

    public bool DirectoryExists(string relativePath)
    {
        try { return Directory.Exists(Resolve(relativePath)); }
        catch { return false; }
    }

    public string? ReadText(string relativePath, int maxBytes = 1_000_000)
    {
        try
        {
            var path = Resolve(relativePath);
            if (!File.Exists(path)) return null;

            // Cloud placeholders would trigger a network download if opened.
            if (AttributeProbe.IsCloudPlaceholder(path)) return null;

            var info = new FileInfo(path);
            if (info.Length > maxBytes) return null;

            using var full = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(full, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListFiles(string relativeDirectory, string pattern, bool recursive = false)
    {
        try
        {
            var dir = Resolve(relativeDirectory);
            if (!Directory.Exists(dir)) return [];

            return Directory.EnumerateFiles(dir, pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<string> ListDirectories(string relativeDirectory)
    {
        try
        {
            var dir = Resolve(relativeDirectory);
            if (!Directory.Exists(dir)) return [];

            return Directory.EnumerateDirectories(dir)
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}

public static class AttributeProbe
{
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS — opening the file downloads it.
    private const int RecallOnDataAccess = 0x400000;
    // FILE_ATTRIBUTE_RECALL_ON_OPEN — same, at open time.
    private const int RecallOnOpen = 0x00040000;

    /// <summary>True for OneDrive/Dropbox-style placeholders that must never be read.</summary>
    public static bool IsCloudPlaceholder(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var attributes = File.GetAttributes(path);
            var raw = (int)attributes;
            if ((raw & RecallOnDataAccess) != 0 || (raw & RecallOnOpen) != 0) return true;
            return (attributes & FileAttributes.Offline) != 0;
        }
        catch
        {
            return false;
        }
    }
}
