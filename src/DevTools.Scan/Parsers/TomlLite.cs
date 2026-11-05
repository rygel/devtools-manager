using System.Text.Json;

namespace DevTools.Scan.Parsers;

/// <summary>
/// A deliberately small TOML reader: tables, dotted keys, and scalar values. Enough for
/// <c>pyproject.toml</c>, <c>rust-toolchain.toml</c> and <c>mise.toml</c>, which is all
/// this application needs. Arrays and inline tables are skipped rather than half-parsed.
/// </summary>
public sealed class TomlLite
{
    private readonly Dictionary<string, (string Value, int Line)> _values = new(StringComparer.Ordinal);

    public static TomlLite Parse(string? text)
    {
        var toml = new TomlLite();
        if (string.IsNullOrWhiteSpace(text)) return toml;

        var table = "";
        foreach (var (rawLine, number) in ParserText.Lines(text))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                if (line.StartsWith('[') && !line.StartsWith("[["))
                {
                    table = line.Trim('[', ']').Trim();
                }

                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim().Trim('"');
            var value = line[(equals + 1)..].Trim();

            // Multiline arrays / inline tables: not needed by any supported file.
            if (value.StartsWith('[') || value.StartsWith('{')) continue;

            value = StripComment(value);

            value = ParserText.Unquote(value);
            if (value.Length == 0) continue;

            var fullKey = table.Length == 0 ? key : table + "." + key;
            toml._values[fullKey] = (value, number);
        }

        return toml;
    }

    private static string StripComment(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            if (quote == '"' && current == '\\' && !escaped) { escaped = true; continue; }
            if (!escaped && quote != '\0' && current == quote) quote = '\0';
            else if (quote == '\0' && current is '"' or '\'') quote = current;
            else if (quote == '\0' && current == '#') return value[..i].TrimEnd();
            escaped = false;
        }
        return value;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v.Value : null;

    public (string Value, int Line)? Find(params string[] keys)
    {
        foreach (var key in keys)
        {
            if (_values.TryGetValue(key, out var v)) return v;
        }

        return null;
    }

    public string? GetBySuffix(string suffix)
    {
        foreach (var (key, value) in _values)
        {
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return value.Value;
        }

        return null;
    }

    public IReadOnlyDictionary<string, (string Value, int Line)> All => _values;
}

public static class JsonHelper
{
    public static JsonDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks a dotted path ("engines.node") and returns the raw string value.</summary>
    public static string? GetText(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out current)) return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            _ => null
        };
    }

    public static bool Exists(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object) return false;
            if (!current.TryGetProperty(segment, out current)) return false;
        }

        return true;
    }
}
