using System.Reflection;

namespace DevTools.Server;

/// <summary>
/// Serves the UI from embedded resources. Avoids static-web-asset plumbing entirely, so
/// the same code path works when the app is a single published file.
/// </summary>
internal static class EmbeddedAssets
{
    private static readonly Assembly Assembly = typeof(EmbeddedAssets).Assembly;

    private static readonly Dictionary<string, (string Resource, string ContentType)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["/"] = ("DevTools.Server.wwwroot.index.html", "text/html; charset=utf-8"),
            ["/index.html"] = ("DevTools.Server.wwwroot.index.html", "text/html; charset=utf-8"),
            ["/app.js"] = ("DevTools.Server.wwwroot.app.js", "text/javascript; charset=utf-8"),
            ["/app.css"] = ("DevTools.Server.wwwroot.app.css", "text/css; charset=utf-8"),
            ["/favicon.svg"] = ("DevTools.Server.wwwroot.favicon.svg", "image/svg+xml")
        };

    public static (byte[] Bytes, string ContentType)? Resolve(string path)
    {
        if (!Map.TryGetValue(path, out var entry)) return null;

        using var stream = Assembly.GetManifestResourceStream(entry.Resource);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return (memory.ToArray(), entry.ContentType);
    }
}
