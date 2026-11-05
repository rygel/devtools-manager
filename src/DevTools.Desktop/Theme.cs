using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace DevTools.Desktop;

/// <summary>
/// One restrained dark palette: neutrals tinted toward the accent hue, one blue accent for
/// primary actions and selection, and three status colours that only ever mark state.
/// </summary>
internal static class Palette
{
    public static readonly Color Sidebar = Color.Parse("#12151A");
    public static readonly Color Surface = Color.Parse("#181B21");
    public static readonly Color Raised = Color.Parse("#1F232B");
    public static readonly Color Selected = Color.Parse("#262C37");
    public static readonly Color Line = Color.Parse("#2B313C");
    public static readonly Color Text = Color.Parse("#E7E9ED");
    public static readonly Color TextDim = Color.Parse("#A0A7B4");
    public static readonly Color TextFaint = Color.Parse("#858D9B");
    public static readonly Color Accent = Color.Parse("#5AA9E6");
    public static readonly Color AccentInk = Color.Parse("#08111C");
    public static readonly Color Ok = Color.Parse("#4CC38A");
    public static readonly Color Warn = Color.Parse("#E5B14B");
    public static readonly Color Bad = Color.Parse("#F07474");

    public static IBrush Brush(Color color) => new SolidColorBrush(color);
}

internal static class AppTheme
{
    public const string Sans = "Segoe UI Variable Text, Segoe UI, Inter, system-ui, sans-serif";
    public const string Mono = "Cascadia Mono, Consolas, Menlo, monospace";

    public static FluentTheme Create() => new()
    {
        Palettes =
        {
            [ThemeVariant.Dark] = new ColorPaletteResources
            {
                Accent = Palette.Accent,
                RegionColor = Palette.Surface,
                ErrorText = Palette.Bad
            }
        }
    };

    /// <summary>Control-level tweaks layered over Fluent: list rows, buttons, inputs.</summary>
    /// <summary>Quiet input chrome: hairline borders instead of Fluent's bright strokes.</summary>
    public static void Resources(IResourceDictionary resources)
    {
        var line = Palette.Brush(Palette.Line);
        var strong = Palette.Brush(Color.Parse("#4A5262"));
        resources["TextControlBorderBrush"] = line;
        resources["TextControlBorderBrushPointerOver"] = strong;
        resources["CheckBoxCheckBackgroundStrokeUnchecked"] = strong;
        resources["CheckBoxCheckBackgroundStrokeUncheckedPointerOver"] = Palette.Brush(Palette.TextFaint);
        resources["CheckBoxCheckBackgroundFillUnchecked"] = Brushes.Transparent;
    }

    public static Styles Styles()
    {
        var styles = new Styles();
        styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 8)),
                new Setter(Layoutable.MarginProperty, new Thickness(0, 1)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(Layoutable.MinHeightProperty, 0.0)
            }
        });
        styles.Add(PresenterStyle(x => x.OfType<ListBoxItem>().Class(":pointerover"), Palette.Raised, null));
        styles.Add(PresenterStyle(x => x.OfType<ListBoxItem>().Class(":selected"), Palette.Selected, null));
        styles.Add(PresenterStyle(x => x.OfType<ListBoxItem>().Class(":selected").Class(":pointerover"), Palette.Selected, null));

        foreach (var (name, fill, hover, ink) in new[]
        {
            ("primary", Palette.Accent, Color.Parse("#7BBDF0"), Palette.AccentInk),
            ("danger", Color.Parse("#7A2F34"), Color.Parse("#933A40"), Palette.Text)
        })
        {
            styles.Add(PresenterStyle(x => x.OfType<Button>().Class(name), fill, ink));
            styles.Add(PresenterStyle(x => x.OfType<Button>().Class(name).Class(":pointerover"), hover, ink));
            styles.Add(PresenterStyle(x => x.OfType<Button>().Class(name).Class(":pressed"), fill, ink));
        }
        styles.Add(PresenterStyle(x => x.OfType<Button>().Class("ghost"), Colors.Transparent, null));
        styles.Add(PresenterStyle(x => x.OfType<Button>().Class("ghost").Class(":pointerover"), Palette.Raised, null));
        styles.Add(new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(14, 7)),
                new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Left)
            }
        });
        styles.Add(new Style(x => x.OfType<TextBox>())
        {
            Setters = { new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)) }
        });
        return styles;
    }

    private static Style PresenterStyle(Func<Selector?, Selector> selector, Color background, Color? foreground)
    {
        var style = new Style(x => selector(x).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"));
        style.Setters.Add(new Setter(ContentPresenter.BackgroundProperty, Palette.Brush(background)));
        if (foreground is { } ink) style.Setters.Add(new Setter(ContentPresenter.ForegroundProperty, Palette.Brush(ink)));
        return style;
    }
}

/// <summary>Small factories so every screen speaks the same visual vocabulary.</summary>
internal static class Ui
{
    public static TextBlock Text(string? text, double size = 13, Color? color = null, bool bold = false, bool mono = false, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Palette.Brush(color ?? Palette.Text),
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        FontFamily = new FontFamily(mono ? AppTheme.Mono : AppTheme.Sans),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };

    public static TextBlock Dim(string? text, double size = 12, bool mono = false, bool wrap = true) =>
        Text(text, size, Palette.TextDim, mono: mono, wrap: wrap);

    /// <summary>Section label: sentence case, medium weight. Not an eyebrow.</summary>
    public static TextBlock Label(string text) => Text(text, 12, Palette.TextDim, bold: true);

    public static Border Pill(string text, Color color) => new()
    {
        Background = new SolidColorBrush(color, 0.16),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 11, color, bold: true, wrap: false)
    };

    /// <summary>Neutral label for a fact about a project (a framework). Colour is reserved for status.</summary>
    public static Border Chip(string text) => new()
    {
        BorderBrush = Palette.Brush(Palette.Line),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Text(text, 11, Palette.TextDim, wrap: false)
    };

    public static Border Dot(Color color) => new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        Background = Palette.Brush(color),
        VerticalAlignment = VerticalAlignment.Center
    };

    public static Border Divider() => new() { Height = 1, Background = Palette.Brush(Palette.Line), Margin = new Thickness(0, 4) };

    public static Border Panel(Control child, Thickness? padding = null) => new()
    {
        Background = Palette.Brush(Palette.Raised),
        BorderBrush = Palette.Brush(Palette.Line),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(16),
        Child = child
    };

    public static Button Button(string content, string? kind = null, string? name = null)
    {
        var button = new Button { Content = content };
        if (kind is not null) button.Classes.Add(kind);
        if (name is not null) button.Name = name;
        return button;
    }

    public static Control Empty(string title, string hint) => new StackPanel
    {
        Spacing = 4,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24),
        Children =
        {
            new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Brush(Palette.TextDim),
                HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = hint, FontSize = 12, Foreground = Palette.Brush(Palette.TextFaint), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, MaxWidth = 320 }
        }
    };
}
