using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DevTools.Store;

namespace DevTools.Desktop;

/// <summary>
/// Edits the scan configuration the CLI also reads. The dialog works on a copy: nothing is
/// written anywhere until the owner receives <see cref="Result"/> and saves it.
/// </summary>
public sealed class SettingsDialog : Window
{
    public const int MinDepth = 1;
    public const int MaxDepthLimit = 64;

    private readonly StoreConfig _source;
    private readonly TextBox _roots = new()
    {
        Name = "SettingsRoots",
        AcceptsReturn = true,
        Height = 92,
        PlaceholderText = "One directory per line",
        FontFamily = new FontFamily(AppTheme.Mono),
        FontSize = 12
    };
    private readonly TextBox _excludes = new()
    {
        Name = "SettingsExcludes",
        AcceptsReturn = true,
        Height = 68,
        PlaceholderText = "node_modules, .git, …",
        FontFamily = new FontFamily(AppTheme.Mono),
        FontSize = 12
    };
    private readonly NumericUpDown _depth = new()
    {
        Name = "SettingsMaxDepth",
        Minimum = MinDepth,
        Maximum = MaxDepthLimit,
        Increment = 1,
        FormatString = "0",
        Width = 140,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly CheckBox _symlinks = new() { Name = "SettingsFollowSymlinks", Content = "Follow symbolic links" };
    private readonly CheckBox _measure = new() { Name = "SettingsMeasure", Content = "Measure disk usage on every scan" };
    private readonly CheckBox _endOfLife = new() { Name = "SettingsEndOfLife", Content = "Look up end-of-life dates" };
    private readonly TextBlock _error = Ui.Text(null, 12, Palette.Bad);

    /// <summary>The edited configuration after Save; null when cancelled or closed.</summary>
    public StoreConfig? Result { get; private set; }

    public SettingsDialog(StoreConfig config)
    {
        _source = config;
        _error.Name = "SettingsError";
        _error.IsVisible = false;
        Title = "Settings";
        Width = 600;
        Height = 720;
        MinWidth = 480;
        MinHeight = 420;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Brush(Palette.Surface);
        FontFamily = new FontFamily(AppTheme.Sans);
        FontSize = 13;
        Foreground = Palette.Brush(Palette.Text);

        _roots.Text = string.Join(Environment.NewLine, config.Roots);
        _excludes.Text = string.Join(Environment.NewLine, config.Excludes);
        _depth.Value = config.MaxDepth;
        _symlinks.IsChecked = config.FollowSymlinks;
        _measure.IsChecked = config.MeasureSizes;
        _endOfLife.IsChecked = config.CheckEndOfLife;
        AutomationProperties.SetName(_roots, "Scan roots, one directory per line");
        AutomationProperties.SetName(_excludes, "Excluded directory names, one per line");
        AutomationProperties.SetName(_depth, "Maximum directory depth");

        var save = Ui.Button("Save", "primary", "SettingsSave");
        var cancel = Ui.Button("Cancel", "ghost", "SettingsCancel");
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };

        var form = new StackPanel { Spacing = 8, Margin = new Thickness(28, 24, 28, 12), Children =
        {
            Ui.Text("Settings", 20, bold: true),
            Ui.Dim("Shared with the devtools command line. Changes apply to the next scan.", 12),
            Section("Scan roots", "Directories searched for projects and toolchains."),
            _roots,
            Section("Excluded directory names", "Folders with these names are skipped at any depth, ignoring case."),
            _excludes,
            Section("Maximum depth", "How many directory levels below each root to search."),
            _depth,
            Section("Scanning behaviour", null),
            _symlinks,
            Ui.Dim("Linked directories are skipped unless this is on.", 11),
            _measure,
            Ui.Dim("Adds a size for every project and toolchain. Noticeably slower on large drives.", 11),
            _endOfLife,
            Ui.Dim("Asks endoflife.date for the release lines of your installed tools and caches the answer for 7 days. Only product names are requested.", 11),
            _error
        }};
        var footer = new Border
        {
            Padding = new Thickness(28, 14),
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, save }
            }
        };
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer { Content = form, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                WithRow(footer, 1)
            }
        };
    }

    private static StackPanel Section(string title, string? hint)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
        stack.Children.Add(Ui.Text(title, 13, bold: true));
        if (hint is not null) stack.Children.Add(Ui.Dim(hint, 12));
        return stack;
    }

    private static T WithRow<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static List<string> Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Save()
    {
        if (_depth.Value is not { } depth || depth < MinDepth || depth > MaxDepthLimit || depth != Math.Floor(depth))
        {
            _error.Text = $"Enter a maximum depth between {MinDepth} and {MaxDepthLimit}.";
            _error.IsVisible = true;
            return;
        }
        Result = new StoreConfig
        {
            SchemaVersion = _source.SchemaVersion,
            Initialised = _source.Initialised,
            Roots = Lines(_roots.Text),
            Excludes = Lines(_excludes.Text),
            MaxDepth = (int)depth,
            FollowSymlinks = _symlinks.IsChecked == true,
            MeasureSizes = _measure.IsChecked == true,
            CheckEndOfLife = _endOfLife.IsChecked == true
        };
        Close();
    }
}
