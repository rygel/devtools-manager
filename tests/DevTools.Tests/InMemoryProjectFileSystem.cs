using DevTools.Scan;

namespace DevTools.Tests;

/// <summary>An in-memory project root so parser tests need no fixture directories.</summary>
public sealed class InMemoryProjectFileSystem : IProjectFileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase) { "" };

    public InMemoryProjectFileSystem File(string path, string content)
    {
        _files[Normalize(path)] = content;

        var directory = Path.GetDirectoryName(Normalize(path).Replace('/', Path.DirectorySeparatorChar));
        while (!string.IsNullOrEmpty(directory))
        {
            _directories.Add(directory.Replace('\\', '/'));
            directory = Path.GetDirectoryName(directory);
        }

        return this;
    }

    public InMemoryProjectFileSystem Dir(string path)
    {
        _directories.Add(Normalize(path));
        return this;
    }

    private static string Normalize(string path)
    {
        var value = path.Replace('\\', '/').Trim('/');
        return value == "." ? "" : value;
    }

    public bool FileExists(string relativePath) => _files.ContainsKey(Normalize(relativePath));

    public bool DirectoryExists(string relativePath) => _directories.Contains(Normalize(relativePath));

    public string? ReadText(string relativePath, int maxBytes = 1_000_000) =>
        _files.TryGetValue(Normalize(relativePath), out var content) ? content : null;

    public IReadOnlyList<string> ListFiles(string relativeDirectory, string pattern, bool recursive = false)
    {
        var directory = Normalize(relativeDirectory);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var extension = pattern.Replace("*", "");

        return _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(path => recursive || !path[prefix.Length..].Contains('/'))
            .Where(path => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ListDirectories(string relativeDirectory)
    {
        var directory = Normalize(relativeDirectory);
        var prefix = directory.Length == 0 ? "" : directory + "/";

        return _directories
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length)
            .Select(path => path[prefix.Length..])
            .Where(rest => !rest.Contains('/'))
            .ToList();
    }
}

public static class ProjectContextFactory
{
    public static ProjectContext Create(InMemoryProjectFileSystem fileSystem, string directory = "/project") =>
        new() { Directory = directory, Fs = fileSystem };
}
