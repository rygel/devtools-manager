using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Store;

namespace DevTools.Desktop;

public sealed partial class MainWindow : Window, IDisposable
{
    private static readonly string[] PageNames = OperatingSystem.IsWindows()
        ? ["Projects", "Toolchains", "Disk", "Migration", "Supported", "WSL"]
        : ["Projects", "Toolchains", "Disk", "Migration", "Supported"];

    private static readonly string[] PageHints =
    [
        "",
        "",
        "Disk used by each project, and the build artifacts that can be moved to the trash.",
        "Rewrite an existing version declaration across the projects of one environment.",
        SupportCatalog.Summary(),
        "Windows Subsystem for Linux distributions, each with its own index."
    ];

    private readonly SqliteStore _store;
    private readonly EndOfLifeService _eol;
    private bool _checkEol;
    private bool _eolRefreshing;
    private IReadOnlyDictionary<ToolKind, IReadOnlyList<EolCycle>> _eolData = new Dictionary<ToolKind, IReadOnlyList<EolCycle>>();
    private readonly TextBlock _status = Ui.Dim(null, 12);
    private readonly TextBlock _summary = Ui.Dim(null, 13);
    private readonly TextBlock _pageTitle = Ui.Text("Projects", 20, bold: true, wrap: false);
    private readonly TextBlock _pageHint = Ui.Dim(null, 13);
    private readonly TextBlock _lastScan = Ui.Dim(null, 11);
    private List<string> _rootList = [];
    private readonly Button _settings = Ui.Button("Settings", "ghost", "OpenSettings");
    private readonly TextBox _search = new() { Name = "ProjectSearch", PlaceholderText = "Search by name, path, ecosystem or framework" };
    private readonly CheckBox _problemsOnly = new() { Content = "Needs attention only" };
    private readonly TextBlock _projectCount = Ui.Dim(null, 12, wrap: false);
    private readonly ListBox _nav = MakeList();
    private readonly ListBox _projects = MakeList();
    private readonly ListBox _tools = MakeGroupedList();
    private readonly ListBox _installOptions = MakeList();
    private readonly ListBox _migrationEdits = MakeList();
    private readonly ListBox _migrationEnvironments = MakeList();
    private readonly ListBox _disk = MakeList();
    private readonly TextBox _diskSearch = new() { Name = "DiskSearch", PlaceholderText = "Search by name, path or environment", MinWidth = 260 };
    private readonly TextBlock _diskCount = Ui.Dim(null, 12);
    private DiskRow[] _allDiskRows = [];
    private readonly ListBox _cleanupTargets = MakeList();
    private readonly ListBox _wslNames = MakeList();
    private readonly ContentControl _projectDetail = new();
    private readonly ContentControl _toolDetail = new();
    private readonly ContentControl _diskDetail = new();
    private readonly Button _scan = Ui.Button("Scan roots", "primary");
    private readonly CheckBox _measure = new() { Content = "Measure disk usage" };
    private readonly ProgressBar _busy = new()
    {
        IsIndeterminate = true,
        IsVisible = false,
        Height = 2,
        MinHeight = 2,
        Foreground = Palette.Brush(Palette.Accent),
        Background = Brushes.Transparent
    };
    private readonly Control[] _pages = new Control[PageNames.Length];
    private readonly Dictionary<string, MachineView> _wslViews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _wslStatus = new(StringComparer.Ordinal);
    private MachineView? _view;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _cleanupBusy;
    private bool _wslBusy;
    private bool _installBusy;
    private bool _migrationBusy;
    private string? _migrationToken;
    private string? _migrationDistribution;
    private MigrationOptions? _migrationOptions;
    private CancellationTokenSource? _installCancellation;
    private int _activeProbes;

    public MainWindow(string? storeDirectory = null)
    {
        _store = new SqliteStore(storeDirectory ?? PlatformPaths.ApplicationData);
        _eol = new EndOfLifeService(_store.Root);
        _checkEol = ReadCheckEol();
        _nav.Name = "Navigation";
        _projects.Name = "ProjectList";
        _disk.Name = "DiskList";
        _cleanupTargets.Name = "CleanupTargets";
        _migrationEdits.Name = "MigrationEdits";
        _migrationEnvironments.Name = "MigrationEnvironments";
        _installOptions.Name = "InstallOptions";
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetName(_search, "Search projects");
        Title = "DevTools Manager";
        Icon = AppLogo.CreateWindowIcon();
        Width = 1240;
        Height = 800;
        MinWidth = 860;
        MinHeight = 560;
        Background = Palette.Brush(Palette.Surface);
        FontFamily = new FontFamily(AppTheme.Sans);
        FontSize = 13;
        Foreground = Palette.Brush(Palette.Text);

        var configLoaded = false;
        try
        {
            var config = _store.LoadConfig();
            _rootList = config.Roots.ToList();
            _measure.IsChecked = config.MeasureSizes;
            configLoaded = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _summary.Text = "The configuration could not be read.";
            _status.Text = ex.Message;
            _scan.IsEnabled = false;
            _settings.IsEnabled = false;
        }
        _scan.Click += async (_, _) => await ScanAsync();
        _settings.Click += async (_, _) => await OpenSettingsAsync();
        Closed += (_, _) => Dispose();
        Closing += (_, eventArgs) =>
        {
            if (!_cleanupBusy && !_wslBusy && !_installBusy && !_migrationBusy && _activeProbes == 0) return;
            eventArgs.Cancel = true;
            _status.Text = "Wait for the current operation to finish before closing.";
        };
        _search.TextChanged += (_, _) => PopulateProjects();
        _problemsOnly.IsCheckedChanged += (_, _) => PopulateProjects();

        _pages[0] = BuildProjectsPage();
        _pages[1] = BuildToolchainsPage();
        _pages[2] = BuildDiskPage();
        _pages[3] = BuildMigrationPage();
        _pages[4] = BuildSupportedPage();
        if (_pages.Length > 5) _pages[5] = BuildWslPage();

        var pageHost = new Grid();
        foreach (var page in _pages) pageHost.Children.Add(page);

        _nav.ItemTemplate = new FuncDataTemplate<NavItem>((item, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var icon = NavIcons.Create(item?.Label ?? "", Palette.Brush(Palette.TextDim));
            if (icon is not null)
            {
                icon.Margin = new Thickness(0, 0, 12, 0);
                icon.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(icon);
                // The selected page's icon takes the accent colour, so the current location reads at a glance.
                row.AttachedToVisualTree += (_, _) =>
                {
                    var host = row.FindAncestorOfType<ListBoxItem>();
                    if (host is null || icon is not Avalonia.Controls.Shapes.Path glyph) return;
                    void Paint() => glyph.Fill = Palette.Brush(host.IsSelected ? Palette.Accent : Palette.TextDim);
                    host.PropertyChanged += (_, e) => { if (e.Property == ListBoxItem.IsSelectedProperty) Paint(); };
                    Paint();
                };
            }
            row.Children.Add(WithColumn(Ui.Text(item?.Label, 13, wrap: false), 1));
            row.Children.Add(WithColumn(Ui.Dim(item?.Count, 12, wrap: false), 2));
            return row;
        });
        _nav.SelectionChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        SetNavigation();
        _nav.SelectedIndex = 0;

        var brandText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Text("DevTools Manager", 15, bold: true, wrap: false),
            Ui.Dim("Projects and their tooling", 11, wrap: false)
        }};
        var brand = new StackPanel { Margin = new Thickness(18, 20, 18, 16), Orientation = Orientation.Horizontal, Spacing = 10, Children =
        {
            AppLogo.Create(36),
            brandText
        }};
        _measure.FontSize = 12;
        _lastScan.TextWrapping = TextWrapping.Wrap;
        _scan.HorizontalAlignment = HorizontalAlignment.Stretch;
        _scan.HorizontalContentAlignment = HorizontalAlignment.Center;
        _settings.HorizontalAlignment = HorizontalAlignment.Stretch;
        _settings.HorizontalContentAlignment = HorizontalAlignment.Left;
        _settings.Padding = new Thickness(4, 8);
        _settings.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                NavIcons.Create("Settings", Palette.Brush(Palette.TextDim))!,
                new TextBlock { Text = "Settings", VerticalAlignment = VerticalAlignment.Center }
            }
        };
        AutomationProperties.SetName(_settings, "Settings");
        var sidebarActions = new StackPanel { Margin = new Thickness(14, 10, 14, 16), Spacing = 10, Children = { _scan, _measure, _lastScan, Ui.Divider(), _settings } };
        var sidebar = new Border
        {
            Width = 224,
            Background = Palette.Brush(Palette.Sidebar),
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children = { brand, WithRow(new Border { Padding = new Thickness(8, 0), Child = _nav }, 1), WithRow(sidebarActions, 2) }
            }
        };

        var header = new StackPanel { Margin = new Thickness(28, 22, 28, 14), Spacing = 4, Children = { _pageTitle, _summary, _pageHint } };
        var statusBar = new Border
        {
            Padding = new Thickness(28, 8),
            BorderBrush = Palette.Brush(Palette.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { _status, WithColumn(AppVersionLabel(), 1) }
            }
        };
        var main = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                header,
                WithRow(_busy, 1),
                WithRow(new Border { Padding = new Thickness(28, 0, 28, 20), Child = pageHost }, 2),
                WithRow(statusBar, 3)
            }
        };
        var shell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        shell.Children.Add(sidebar);
        Grid.SetColumn(main, 1);
        shell.Children.Add(main);
        Content = shell;

        if (configLoaded)
        {
            try { LoadSnapshot(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                _summary.Text = "The index could not be read.";
                _status.Text = ex.Message;
                _scan.IsEnabled = false;
            }
        }
    }

    /// <summary>The application version, read the same way as <c>devtools version</c> so both agree.</summary>
    public static string AppVersion =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private static TextBlock AppVersionLabel()
    {
        var label = Ui.Dim("v" + AppVersion, 12);
        label.Margin = new Thickness(16, 0, 0, 0);
        label.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        AutomationProperties.SetName(label, "Application version " + AppVersion);
        return label;
    }

    private static T WithRow<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static T WithColumn<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private static ListBox MakeList()
    {
        var list = new ListBox { Background = Brushes.Transparent };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        return list;
    }

    private static GroupedListBox MakeGroupedList()
    {
        var list = new GroupedListBox { Background = Brushes.Transparent };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        return list;
    }

    private void ShowPage(int index)
    {
        if (index < 0) return;
        for (var i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == index;
        _pageTitle.Text = PageNames[index];
        var overview = index <= 1;
        _summary.IsVisible = overview;
        _pageHint.IsVisible = !overview;
        _pageHint.Text = PageHints[index];
    }

    private void SetNavigation()
    {
        var selected = _nav.SelectedIndex;
        var projects = Machines().Sum(m => m.View.Projects.Count(p => p.Project.State == ProjectState.Active));
        var tools = Machines().Sum(m => m.View.Snapshot.Toolchains.Count);
        _nav.ItemsSource = new[]
        {
            new NavItem(PageNames[0], projects == 0 ? "" : projects.ToString("N0")),
            new NavItem(PageNames[1], tools == 0 ? "" : tools.ToString("N0")),
            new NavItem(PageNames[2], ""),
            new NavItem(PageNames[3], "")
        }.Concat(PageNames.Skip(4).Select(name => new NavItem(name, ""))).ToArray();
        _nav.SelectedIndex = selected < 0 ? 0 : selected;
    }

    private void LoadSnapshot()
    {
        var snapshot = _store.Load();
        _view = snapshot is null ? null : MachineView.Build(snapshot);
        if (snapshot is null)
        {
            _summary.Text = "No index yet. Add scan roots in Settings, then press Scan roots.";
            _lastScan.Text = "Never scanned";
        }
        else
        {
            var stats = _view!.Stats;
            var environment = snapshot.EnvironmentName == "Unknown" ? "" : snapshot.EnvironmentName + "  ·  ";
            _summary.Text = $"{environment}{stats.ActiveProjects:N0} projects  ·  {stats.ProjectsWithProblems:N0} need attention  ·  {stats.ToolchainsInstalled:N0} toolchains available  ·  {stats.UnverifiedToolchains:N0} unverified" +
                (_view.Conflicts.Count > 0 ? $"  ·  {_view.Conflicts.Count} conflicting version pairs (see devtools doctor)" : "");
            _lastScan.Text = $"Last scan {snapshot.ScannedAt:g}";
            _status.Text = snapshot.Stats.Partial ? "Incomplete scan: unavailable projects may have stale information." : "Ready";
        }
        PopulateLists();
        StartEolRefresh();
    }

    private bool ReadCheckEol()
    {
        try { return _store.LoadConfig(createIfMissing: false).CheckEndOfLife; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>
    /// Refreshes end-of-life data in the background when the setting is on. The fetch runs off the UI thread and only
    /// touches the network for tools the index actually contains; the result is shown by repopulating the lists.
    /// </summary>
    private void StartEolRefresh()
    {
        if (!_checkEol || _eolRefreshing || _view is null) return;
        var tools = Machines().SelectMany(machine => machine.View.Snapshot.Toolchains.Select(t => t.Tool)).Distinct().ToList();
        if (tools.Count == 0) return;
        _eolRefreshing = true;
        var thread = new Thread(() =>
        {
            EolRefreshResult? result = null;
            try { result = _eol.Refresh(tools, DateTimeOffset.UtcNow, cancellationToken: _shutdown.Token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result = new EolRefreshResult(0, 0, ["could not write the cache: " + ex.Message]); }
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _eolRefreshing = false;
                if (result is null) return;
                if (result.Errors.Count > 0) _status.Text = "End-of-life lookup failed: " + result.Errors[0];
                if (result.Fetched > 0) PopulateLists();
            });
        }) { Name = "eol-refresh", IsBackground = true };
        thread.Start();
    }

    private IEnumerable<(MachineView View, bool IsWsl)> Machines()
    {
        if (_view is not null) yield return (_view, false);
        foreach (var view in _wslViews.Values) yield return (view, true);
    }

    private async Task ScanAsync()
    {
        var roots = _rootList.ToList();
        if (roots.Count == 0)
        {
            _status.Text = "Add at least one scan root in Settings.";
            await OpenSettingsAsync();
            return;
        }
        using var scanLock = _store.TryAcquireScanLock();
        if (scanLock is null) { _status.Text = "Another scan is already using this index."; return; }
        _scan.IsEnabled = false;
        _settings.IsEnabled = false;
        _busy.IsVisible = true;
        _status.Text = "Scanning…";
        try
        {
            var config = _store.LoadConfig();
            var snapshot = await new ScanService().ScanAsync(new ScanRequest
            {
                Roots = roots, MaxDepth = config.MaxDepth, Excludes = config.Excludes,
                FollowSymlinks = config.FollowSymlinks, MeasureSizes = _measure.IsChecked == true,
                MeasureToolchains = _measure.IsChecked == true, CancellationToken = _shutdown.Token
            }, _store.Load());
            _store.Save(snapshot);
            config.Roots = roots;
            config.Initialised = true;
            _store.SaveConfig(config);
            LoadSnapshot();
        }
        catch (Exception ex) { _status.Text = "Scan failed: " + ex.Message; }
        finally { _scan.IsEnabled = true; _settings.IsEnabled = true; _busy.IsVisible = false; }
    }

    private async Task OpenSettingsAsync()
    {
        StoreConfig config;
        try { config = _store.LoadConfig(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _status.Text = "The configuration could not be read: " + ex.Message; return; }
        var dialog = new SettingsDialog(config);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } updated) return;
        try { _store.SaveConfig(updated); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "Settings could not be saved: " + ex.Message; return; }
        _rootList = updated.Roots.ToList();
        _measure.IsChecked = updated.MeasureSizes;
        _checkEol = updated.CheckEndOfLife;
        PopulateLists();
        StartEolRefresh();
        _status.Text = updated.Roots.Count == 0
            ? "Settings saved. Add a scan root before scanning."
            : "Settings saved. They apply to the next scan.";
    }

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null => "not measured",
        < 1024 => $"{bytes.Value:N0} B",
        < 1048576 => $"{bytes.Value / 1024.0:N1} KiB",
        < 1073741824 => $"{bytes.Value / 1048576.0:N1} MiB",
        _ => $"{bytes.Value / 1073741824.0:N1} GiB"
    };

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
