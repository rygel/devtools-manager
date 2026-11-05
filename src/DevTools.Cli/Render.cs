using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevTools.Cli;

public sealed class Args
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "full", "explain", "apply", "yes", "measure", "scan", "no-inventory", "no-open",
        "unused", "dry-run", "no-probe", "no-git", "quiet", "verbose", "help", "all",
        "follow-symlinks"
    };

    public string Command { get; private init; } = "help";

    public List<string> Positional { get; } = [];

    public Dictionary<string, string?> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] argv)
    {
        var args = new Args();
        var index = 0;

        if (argv.Length > 0 && !argv[0].StartsWith('-'))
        {
            args = new Args { Command = argv[0].ToLowerInvariant() };
            index = 1;
        }

        while (index < argv.Length)
        {
            var token = argv[index];

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var name = token[2..];
                string? value = null;

                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }
                else if (!BooleanFlags.Contains(name) &&
                         index + 1 < argv.Length &&
                         !argv[index + 1].StartsWith('-'))
                {
                    value = argv[index + 1];
                    index++;
                }
                else if (!BooleanFlags.Contains(name))
                {
                    value = "true";
                }

                args.Flags[name] = value;
            }
            else if (token.StartsWith('-') && token.Length > 1 && token[1] != '-')
            {
                // Short flags: -h, -j. Treated as booleans.
                foreach (var character in token[1..])
                {
                    args.Flags[character switch { 'h' => "help", 'j' => "json", var c => c.ToString() }] = null;
                }
            }
            else
            {
                args.Positional.Add(token);
            }

            index++;
        }

        return args;
    }

    public bool Has(string name) => Flags.ContainsKey(name);

    public string? Value(string name) => Flags.TryGetValue(name, out var value) ? value : null;

    public int GetNumber(string name, int fallback) =>
        int.TryParse(Value(name), out var parsed) ? parsed : fallback;

    public bool Json => Has("json");
}

public static class Out
{
    private static bool? _colorEnabled;

    public static bool Color
    {
        get
        {
            _colorEnabled ??= !Console.IsOutputRedirected &&
                              Environment.GetEnvironmentVariable("NO_COLOR") is null;
            return _colorEnabled.Value;
        }
    }

    private const string Reset = "\u001b[0m";
    private const string Dim = "\u001b[2m";
    private const string Bold = "\u001b[1m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Yellow = "\u001b[33m";
    private const string Cyan = "\u001b[36m";
    private const string Magenta = "\u001b[35m";

    public static string Style(string text, string color) => Color ? color + text + Reset : text;

    public static string DimText(string text) => Style(text, Dim);

    public static string BoldText(string text) => Style(text, Bold);

    public static string GreenText(string text) => Style(text, Green);

    public static string RedText(string text) => Style(text, Red);

    public static string YellowText(string text) => Style(text, Yellow);

    public static string CyanText(string text) => Style(text, Cyan);

    public static string MagentaText(string text) => Style(text, Magenta);

    public static void Line(string text = "") => Console.WriteLine(text);

    public static void Blank() => Console.WriteLine();

    public static string Glyph(string status) => status switch
    {
        "satisfied" => GreenText("✔"),
        "missing" => RedText("✘"),
        "unsatisfied_range" => RedText("✘"),
        "conflict" => YellowText("⚠"),
        "unknown" => YellowText("?"),
        "not_managed" => DimText("·"),
        "ignored" => DimText("·"),
        _ => DimText("·")
    };

    public static string StatusLabel(string status) => status switch
    {
        "satisfied" => GreenText("ok"),
        "missing" => RedText("missing"),
        "unsatisfied_range" => RedText("wrong version"),
        "conflict" => YellowText("conflict"),
        "unknown" => YellowText("unknown"),
        "not_managed" => DimText("not managed"),
        "ignored" => DimText("ignored"),
        _ => status
    };
}

public sealed class Table
{
    private readonly List<string> _headers = [];
    private readonly List<string[]> _rows = [];

    public Table(params string[] headers) => _headers.AddRange(headers);

    public void Add(params string[] cells) => _rows.Add(cells);

    public void Write()
    {
        var columns = _headers.Count;
        var widths = new int[columns];

        for (var i = 0; i < columns; i++)
        {
            widths[i] = VisibleLength(_headers[i]);
            foreach (var row in _rows)
            {
                if (i < row.Length) widths[i] = Math.Max(widths[i], VisibleLength(row[i]));
            }
        }

        // Header
        Console.WriteLine(string.Join("  ",
            _headers.Select((header, i) => Out.DimText(Pad(header, widths[i])))));

        foreach (var row in _rows)
        {
            var cells = new string[columns];
            for (var i = 0; i < columns; i++)
            {
                var cell = i < row.Length ? row[i] : "";
                cells[i] = Pad(cell, widths[i]);
            }

            Console.WriteLine(string.Join("  ", cells));
        }
    }

    /// <summary>ANSI escapes must not count toward column width.</summary>
    private static int VisibleLength(string text)
    {
        var length = 0;
        var inEscape = false;

        foreach (var character in text)
        {
            if (character == '\u001b') { inEscape = true; continue; }
            if (inEscape) { if (character == 'm') inEscape = false; continue; }
            length++;
        }

        return length;
    }

    private static string Pad(string text, int width)
    {
        var padding = width - VisibleLength(text);
        return padding > 0 ? text + new string(' ', padding) : text;
    }
}

public static class JsonOut
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Write<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
}
