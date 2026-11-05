using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using DevTools.Core.Model;
using DevTools.Core.Resolution;
using DevTools.Providers;
using DevTools.Store;

namespace DevTools.Desktop;

public sealed partial class MainWindow
{
    private readonly Button _copyEnvironment = Ui.Button("Copy environment", "primary", "CopyEnvironment");
    private WrapPanel? _projectShortcuts;
    private readonly Button _openFolder = Ui.Button("Open folder", "ghost", "OpenFolder");
    private readonly Button _openTerminal = Ui.Button("Terminal", "ghost", "OpenTerminal");
    private readonly Button _openEditor = Ui.Button("VS Code", "ghost", "OpenEditor");
    private readonly Button _copyMise = Ui.Button("Copy mise.toml", "ghost", "CopyMise");
    private readonly Button _verifyTool = Ui.Button("Verify toolchain");
    private readonly Button _inspectCleanup = Ui.Button("Inspect cleanup targets", null, "InspectCleanup");
    private readonly Button _moveToTrash = Ui.Button("Move selected target to trash", "danger");
    private readonly Button _install = Ui.Button("Install selected option", "primary");
    private readonly Button _cancelInstall = new() { Content = "Cancel local install", IsEnabled = false };
    private readonly Button _prepareInstall = Ui.Button("Prepare installation options");
    private readonly TextBlock _editCount = Ui.Dim(null, 12, wrap: false);
    private readonly TextBlock _projectsEmpty = new();
    private readonly TextBlock _toolsEmpty = new();
    private readonly TextBox _toolSearch = new() { Name = "ToolSearch", PlaceholderText = "Search by tool, version, manager or path", MinWidth = 260 };
    private readonly TextBlock _toolCount = Ui.Dim(null, 12, wrap: false);
    private ToolRow[] _allToolRows = [];

    // ───────────────────────── Projects ─────────────────────────

    private Grid BuildProjectsPage()
    {
        _projects.ItemTemplate = new FuncDataTemplate<ProjectRow>((row, _) => ProjectRowView(row));
        _projects.SelectionChanged += (_, _) => ShowProject();
        _copyEnvironment.Click += async (_, _) =>
        {
            if (_activeProbes > 0) return;
            _activeProbes++;
            _copyEnvironment.IsEnabled = false;
            try { await CopyEnvironmentAsync(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { _status.Text = "Could not copy the environment: " + ex.Message; }
            finally { _activeProbes--; _copyEnvironment.IsEnabled = true; }
        };
        ShowProject();

        _search.MinWidth = 260;
        _problemsOnly.Margin = new Thickness(16, 0, 0, 0);
        _projectCount.Margin = new Thickness(16, 0, 0, 0);
        _projectCount.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(_search);
        toolbar.Children.Add(WithColumn(_problemsOnly, 1));
        toolbar.Children.Add(WithColumn(_projectCount, 2));

        var list = new Grid { Children = { _projects, EmptyOverlay(_projectsEmpty) } };
        _openFolder.Click += (_, _) => Launch(LaunchKind.Folder, "folder");
        _openTerminal.Click += (_, _) => Launch(LaunchKind.Terminal, "terminal");
        _openEditor.Click += (_, _) => Launch(LaunchKind.Editor, "VS Code");
        _copyMise.Click += async (_, _) => await CopyMiseAsync();
        var shortcuts = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _openFolder, _openTerminal, _openEditor, _copyMise } };
        foreach (var shortcut in shortcuts.Children) shortcut.Margin = new Thickness(0, 0, 6, 6);
        var detail = DetailPane(new StackPanel { Spacing = 14, Children = { _projectDetail, _copyEnvironment, shortcuts } });
        _projectShortcuts = shortcuts;
        return new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { toolbar, WithRow(MasterDetail(list, detail), 1) }
        };
    }

    private static Control ProjectRowView(ProjectRow? row)
    {
        if (row is null) return new Panel();
        var project = row.View.Project;
        var ecosystems = string.Join(", ", project.Ecosystems.Select(ToolKinds.DisplayEcosystem));
        var name = Ui.Text(project.DisplayName, 13, bold: true, wrap: false);
        var path = Ui.Dim(project.Path, 11, mono: true, wrap: false);
        var text = new StackPanel { Spacing = 2, Children = { name, path } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        var dot = Ui.Dot(StatusColor(row.Status));
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 5, 0, 0);
        grid.Children.Add(dot);
        grid.Children.Add(WithColumn(text, 1));
        var eco = Ui.Dim(ecosystems, 11, wrap: false);
        eco.MaxWidth = 150;
        eco.TextTrimming = TextTrimming.CharacterEllipsis;
        eco.HorizontalAlignment = HorizontalAlignment.Right;
        var tags = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0), Children = { eco } };
        if (project.Frameworks.Count > 0)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var framework in project.Frameworks.Take(2)) chips.Children.Add(Ui.Chip(framework.Name));
            if (project.Frameworks.Count > 2) chips.Children.Add(Ui.Chip("+" + (project.Frameworks.Count - 2)));
            tags.Children.Add(chips);
        }
        grid.Children.Add(WithColumn(tags, 2));
        AutomationProperties.SetName(grid, row.Status == "ok" ? project.DisplayName : project.DisplayName + ", " + row.Status);
        return grid;
    }

    private void ShowProject()
    {
        _copyEnvironment.IsVisible = _projects.SelectedItem is ProjectRow;
        if (_projectShortcuts is not null) _projectShortcuts.IsVisible = _projects.SelectedItem is ProjectRow;
        UpdateShortcuts();
        if (_projects.SelectedItem is not ProjectRow row)
        {
            _projectDetail.Content = Ui.Empty("No project selected", "Choose a project to see what it requires and whether those versions are installed.");
            return;
        }
        var project = row.View.Project;
        var stack = new StackPanel { Spacing = 10 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        title.Children.Add(Ui.Text(project.DisplayName, 18, bold: true));
        title.Children.Add(WithColumn(Ui.Pill(row.Status, StatusColor(row.Status)), 1));
        stack.Children.Add(title);
        stack.Children.Add(new SelectableTextBlock
        {
            Text = project.Path, FontFamily = new FontFamily(AppTheme.Mono), FontSize = 11,
            Foreground = Palette.Brush(Palette.TextDim), TextWrapping = TextWrapping.Wrap
        });
        if (project.GitRemote is not null)
            stack.Children.Add(Ui.Dim("Git  " + project.GitRemote + (project.GitBranch is null ? "" : "  (" + project.GitBranch + ")"), 12));
        if (project.LastActivityAt is not null)
            stack.Children.Add(Ui.Dim("Last activity " + FormatAge(project.LastActivityAt.Value), 12));

        stack.Children.Add(Ui.Divider());
        stack.Children.Add(Ui.Label("Requirements"));
        var tools = row.View.Resolution.Tools;
        if (tools.Count == 0)
            stack.Children.Add(Ui.Dim(project.Incomplete ? "Requirements could not be fully determined." : "This project declares no tool versions.", 12));
        foreach (var tool in tools)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,Auto"), ColumnSpacing = 10 };
            line.Children.Add(Ui.Text(ToolKinds.Display(tool.Tool), 13, bold: true, wrap: false));
            var detail = new StackPanel { Spacing = 1 };
            detail.Children.Add(Ui.Text(tool.ConstraintDisplay + (tool.Toolchain is null ? "" : "  →  " + tool.Toolchain.Version), 13));
            var provenance = tool.Requirement?.Provenance.Describe();
            if (!string.IsNullOrEmpty(provenance)) detail.Children.Add(Ui.Dim(provenance, 11, mono: true));
            if (tool.Status is ResolutionStatus.Missing or ResolutionStatus.UnsatisfiedRange && tool.Nearest is not null)
                detail.Children.Add(Ui.Dim("Nearest installed: " + tool.Nearest.Version, 11));
            line.Children.Add(WithColumn(detail, 1));
            var pill = Ui.Pill(ResolutionLabel(tool.Status), ResolutionColor(tool.Status));
            pill.VerticalAlignment = VerticalAlignment.Top;
            line.Children.Add(WithColumn(pill, 2));
            stack.Children.Add(line);
        }
        if (project.Frameworks.Count > 0)
        {
            stack.Children.Add(Ui.Divider());
            stack.Children.Add(Ui.Label("Frameworks"));
            stack.Children.Add(Ui.Dim("Declared by the project. They are not installed or managed here.", 11));
            foreach (var framework in project.Frameworks)
            {
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
                var detail = new StackPanel { Spacing = 1 };
                detail.Children.Add(Ui.Text(framework.Name, 13, bold: true));
                var source = framework.Provenance.Describe();
                if (!string.IsNullOrEmpty(source)) detail.Children.Add(Ui.Dim(source, 11, mono: true));
                line.Children.Add(detail);
                line.Children.Add(WithColumn(Ui.Text(framework.Version ?? "no version declared", 12,
                    framework.Version is null ? Palette.TextFaint : Palette.TextDim, mono: framework.Version is not null, wrap: false), 1));
                stack.Children.Add(line);
            }
        }
        var diagnostics = project.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).ToList();
        if (diagnostics.Count > 0)
        {
            stack.Children.Add(Ui.Divider());
            stack.Children.Add(Ui.Label("Scan notes"));
            foreach (var d in diagnostics)
                stack.Children.Add(Ui.Text(d.Code + ": " + d.Message, 12, Palette.Warn));
        }
        _projectDetail.Content = stack;
    }

    // ───────────────────────── Toolchains ─────────────────────────

    private Grid BuildToolchainsPage()
    {
        _tools.ItemTemplate = new FuncDataTemplate<IRow>((item, _) => item switch
        {
            ToolGroupHeader header => ToolHeaderView(header),
            ToolRow row => ToolRowView(row),
            _ => new Panel()
        });
        _tools.SelectionChanged += (_, _) =>
        {
            if (_tools.SelectedItem is ToolGroupHeader header) { SkipHeader(header); return; }
            ShowTool();
        };
        _verifyTool.Click += async (_, _) =>
        {
            if (_activeProbes > 0) return;
            _activeProbes++;
            _verifyTool.IsEnabled = false;
            try { await VerifyToolAsync(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { _status.Text = "Verification failed: " + ex.Message; }
            finally { _activeProbes--; _verifyTool.IsEnabled = true; ShowTool(); }
        };
        _prepareInstall.Click += async (_, _) =>
        {
            if (_installBusy) return;
            _installBusy = true;
            try { await PrepareInstallAsync(); }
            finally { _installBusy = false; }
        };
        _cancelInstall.Click += (_, _) => _installCancellation?.Cancel();
        _install.Click += async (_, _) => await InstallSelectedAsync();
        _installOptions.ItemTemplate = new FuncDataTemplate<InstallRow>((row, _) => InstallRowView(row));
        _installOptions.Height = 190;
        _install.IsEnabled = false;
        _installOptions.SelectionChanged += (_, _) => _install.IsEnabled = !_installBusy && _installOptions.SelectedItem is InstallRow;
        ShowTool();

        var installSection = new StackPanel { Spacing = 8, Children =
        {
            Ui.Divider(),
            Ui.Label("Missing toolchains"),
            Ui.Dim("Compare what projects declare with what is installed, then pick an install route.", 12),
            _prepareInstall,
            _installOptions,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _install, _cancelInstall } }
        }};
        var detail = DetailPane(new StackPanel { Spacing = 12, Children =
        {
            _toolDetail,
            _verifyTool,
            BuildUpdateSection(),
            installSection
        }});
        AutomationProperties.SetName(_toolSearch, "Search toolchains");
        _toolSearch.TextChanged += (_, _) => PopulateTools();
        _toolCount.Margin = new Thickness(16, 0, 0, 0);
        _toolCount.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(_toolSearch);
        toolbar.Children.Add(WithColumn(_toolCount, 1));
        var list = new Grid { Children = { _tools, EmptyOverlay(_toolsEmpty) } };
        return new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { toolbar, WithRow(MasterDetail(list, detail), 1) }
        };
    }

    /// <summary>Keyboard navigation can land on a header; move to the nearest real installation instead.</summary>
    private void SkipHeader(ToolGroupHeader header)
    {
        var items = _tools.ItemsSource?.Cast<IRow>().ToList() ?? [];
        var at = items.IndexOf(header);
        var target = items.Skip(at + 1).OfType<ToolRow>().FirstOrDefault() ?? items.Take(Math.Max(at, 0)).OfType<ToolRow>().LastOrDefault();
        _tools.SelectedItem = target;
    }

    private static StackPanel ToolHeaderView(ToolGroupHeader header)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            ToolIcons.Tile(header.Tool, 24),
            Ui.Text(ToolIcons.Title(header.Tool), 14, bold: true, wrap: false),
            Ui.Dim(header.CountText, 12, wrap: false)
        }};
        foreach (var child in title.Children) child.VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(0, header.First ? 0 : 6, 0, 0) };
        if (!header.First) stack.Children.Add(Ui.Divider());
        stack.Children.Add(title);
        AutomationProperties.SetName(stack, header.Plain);
        return stack;
    }

    /// <summary>One line per installation, indented under the group header's title.</summary>
    private static Control ToolRowView(ToolRow? row)
    {
        if (row is null) return new Panel();
        var tool = row.Toolchain;
        var health = tool.Stale ? ("stale scan", Palette.Warn) : HealthStyle(tool.Health);
        var path = Ui.Dim(tool.Path, 11, mono: true, wrap: false);
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        var manager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Ui.Dim(tool.Manager ?? "unknown manager", 12, wrap: false) } };
        if (row.IsWsl) manager.Children.Add(Ui.Chip(LocalName(row.Machine)));
        var projects = new TextBlock { Text = row.UsedBy == 1 ? "1 project" : row.UsedBy + " projects", FontSize = 11,
            Foreground = Palette.Brush(Palette.TextFaint), MinWidth = 56, TextAlignment = TextAlignment.Right };
        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (EolPill(row.Eol) is { } eolPill) pills.Children.Add(eolPill);
        pills.Children.Add(Ui.Pill(health.Item1, health.Item2));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("78,96,*,Auto,Auto"), ColumnSpacing = 10, Margin = new Thickness(32, 0, 0, 0) };
        var version = Ui.Text(tool.Version, 13, bold: true, wrap: false);
        Control[] cells = [version, manager, path, pills, projects];
        for (var column = 0; column < cells.Length; column++)
        {
            cells[column].VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(WithColumn(cells[column], column));
        }
        AutomationProperties.SetName(grid, ToolIcons.Title(tool.Tool) + " " + tool.Version);
        return grid;
    }

    /// <summary>A pill only for lines that have ended or are about to; supported and unknown stay quiet.</summary>
    private static Border? EolPill(EolAssessment? eol) => eol?.State switch
    {
        EolState.Ended => Ui.Pill("end of life", Palette.Bad),
        EolState.EndingSoon => Ui.Pill(eol.Describe(), Palette.Warn),
        _ => null
    };

    private void ShowTool()
    {
        if (_tools.SelectedItem is not ToolRow row)
        {
            _toolDetail.Content = Ui.Empty("No toolchain selected", "Choose an installation to see where it lives and which projects use it.");
            _verifyTool.IsVisible = false;
            return;
        }
        var tool = row.Toolchain;
        var health = tool.Stale ? ("stale scan", Palette.Warn) : HealthStyle(tool.Health);
        var stack = new StackPanel { Spacing = 8 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        title.Children.Add(ToolIcons.Tile(tool.Tool, 40));
        var heading = Ui.Text(ToolIcons.Title(tool.Tool) + " " + tool.Version, 18, bold: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(WithColumn(heading, 1));
        title.Children.Add(WithColumn(Ui.Pill(health.Item1, health.Item2), 2));
        stack.Children.Add(title);
        stack.Children.Add(new SelectableTextBlock
        {
            Text = tool.Path, FontFamily = new FontFamily(AppTheme.Mono), FontSize = 11,
            Foreground = Palette.Brush(Palette.TextDim), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(Ui.Dim("Installed by  " + (tool.Manager ?? "unknown manager") + "   ·   " + LocalName(row.Machine), 12));
        stack.Children.Add(Ui.Dim("Used by " + (row.UsedBy == 1 ? "1 project" : row.UsedBy + " projects") + (tool.IsGlobalDefault ? "   ·   global default" : ""), 12));
        if (row.Eol is { State: not EolState.Unknown } eol)
            stack.Children.Add(Ui.Text("Release line " + eol.Cycle + ": " + eol.Describe(), 12,
                eol.State == EolState.Ended ? Palette.Bad : eol.State == EolState.EndingSoon ? Palette.Warn : Palette.TextDim));
        if (_checkEol && !row.IsWsl && FreshnessAssessor.Assess(_eolData, tool) is { State: not FreshnessState.Unknown } fresh)
            stack.Children.Add(Ui.Text("Release data: " + fresh.Describe(), 12,
                fresh.State == FreshnessState.Outdated ? Palette.Accent : Palette.TextDim));
        if (tool.HealthDetail is not null) stack.Children.Add(Ui.Text(tool.HealthDetail, 12, Palette.Warn));
        var unverified =tool.Health == ToolchainHealth.Unverified && !tool.Stale;
        if (unverified)
            stack.Children.Add(Ui.Dim("This installation has not been confirmed. Verifying runs its executable once to read the version.", 12));
        _verifyTool.IsVisible = unverified;
        _toolDetail.Content = stack;
    }

    private static Control InstallRowView(InstallRow? row)
    {
        if (row is null) return new Panel();
        var step = row.Step;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            Ui.Text(step.Tool + " " + step.Version, 13, bold: true, wrap: false),
            Ui.Dim(row.Environment, 12, wrap: false),
            Ui.Pill(step.Manual ? "manual" : "ready", step.Manual ? Palette.Warn : Palette.Ok)
        }};
        var stack = new StackPanel { Spacing = 2, Children = { head } };
        stack.Children.Add(Ui.Dim(step.Display, 11, mono: true));
        stack.Children.Add(Ui.Dim("For " + string.Join(", ", step.Projects) + (step.RequiresNetwork ? "  ·  needs network" : ""), 11));
        return stack;
    }

    // ───────────────────────── Disk ─────────────────────────

    private Grid BuildDiskPage()
    {
        _disk.ItemTemplate = new FuncDataTemplate<DiskRow>((row, _) => DiskRowView(row));
        _disk.SelectionChanged += (_, _) => ShowDisk();
        AutomationProperties.SetName(_diskSearch, "Search disk usage");
        _diskSearch.TextChanged += (_, _) => PopulateDisk();
        _diskSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || string.IsNullOrEmpty(_diskSearch.Text)) return;
            _diskSearch.Text = "";
            e.Handled = true;
        };
        _diskCount.Margin = new Thickness(16, 0, 0, 0);
        _diskCount.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(_diskSearch);
        toolbar.Children.Add(WithColumn(_diskCount, 1));
        _cleanupTargets.ItemTemplate = new FuncDataTemplate<CleanupRow>((row, _) => CleanupRowView(row));
        _cleanupTargets.Height = 170;
        _inspectCleanup.Click += async (_, _) =>
        {
            if (_cleanupBusy) return;
            _inspectCleanup.IsEnabled = false;
            _cleanupBusy = true;
            try { await InspectCleanupAsync(); } finally { _cleanupBusy = false; _inspectCleanup.IsEnabled = true; }
        };
        _moveToTrash.Click += async (_, _) =>
        {
            if (_cleanupBusy) return;
            _moveToTrash.IsEnabled = false;
            _cleanupBusy = true;
            try { await MoveCleanupAsync(); } finally { _cleanupBusy = false; _moveToTrash.IsEnabled = true; }
        };
        ShowDisk();
        var detail = DetailPane(new StackPanel { Spacing = 10, Children =
        {
            _diskDetail,
            _inspectCleanup,
            _cleanupTargets,
            _moveToTrash,
            Ui.Dim("Targets go to the system trash, not straight to deletion. Sizes are logical file bytes.", 11)
        }});
        return new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children = { toolbar, WithRow(MasterDetail(_disk, detail), 1) }
        };
    }

    /// <summary>Filters the disk list by name, path or environment. The selection survives while the filter still shows it.</summary>
    private void PopulateDisk()
    {
        var query = _diskSearch.Text?.Trim() ?? "";
        var rows = _allDiskRows.Where(row => query.Length == 0 ||
            row.Plain.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = _disk.SelectedItem as DiskRow;
        _disk.ItemsSource = rows;
        if (selected is not null)
            _disk.SelectedItem = rows.FirstOrDefault(row => row.Project.Path == selected.Project.Path && row.Machine == selected.Machine);
        _diskCount.Text = query.Length == 0
            ? _allDiskRows.Length.ToString("N0") + (_allDiskRows.Length == 1 ? " project" : " projects")
            : rows.Length.ToString("N0") + " of " + _allDiskRows.Length.ToString("N0");
    }

    private static Control DiskRowView(DiskRow? row)
    {
        if (row is null) return new Panel();
        var project = row.Project;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        top.Children.Add(Ui.Text(project.DisplayName, 13, bold: true, wrap: false));
        top.Children.Add(WithColumn(Ui.Text(FormatBytes(project.SizeBytes), 12, Palette.TextDim, wrap: false), 1));
        var share = Math.Clamp(row.Share, 0, 1);
        var bar = new Grid
        {
            Height = 3,
            ColumnDefinitions = [new ColumnDefinition(share, GridUnitType.Star), new ColumnDefinition(1 - share, GridUnitType.Star)],
            Children = { new Border { Background = new SolidColorBrush(Palette.Accent, 0.8), CornerRadius = new CornerRadius(2) } }
        };
        var track = new Border { Background = Palette.Brush(Palette.Line), CornerRadius = new CornerRadius(2), Height = 3, Child = bar };
        return new StackPanel { Spacing = 5, Children =
        {
            top,
            Ui.Dim(project.Path, 11, mono: true, wrap: false),
            track
        }};
    }

    private void ShowDisk()
    {
        var hasSelection = _disk.SelectedItem is DiskRow;
        _inspectCleanup.IsVisible = hasSelection;
        _moveToTrash.IsVisible = hasSelection;
        _cleanupTargets.IsVisible = hasSelection;
        if (_disk.SelectedItem is not DiskRow row)
        {
            _diskDetail.Content = Ui.Empty("No project selected", "Choose a project to inspect its build artifacts such as node_modules, target and bin.");
            return;
        }
        var project = row.Project;
        _diskDetail.Content = new StackPanel { Spacing = 6, Children =
        {
            Ui.Text(project.DisplayName, 18, bold: true),
            new SelectableTextBlock { Text = project.Path, FontFamily = new FontFamily(AppTheme.Mono), FontSize = 11,
                Foreground = Palette.Brush(Palette.TextDim), TextWrapping = TextWrapping.Wrap },
            Ui.Dim("Project " + FormatBytes(project.SizeBytes) + "   ·   build artifacts " + FormatBytes(project.ArtifactBytes), 12)
        }};
    }

    private static Control CleanupRowView(CleanupRow? row)
    {
        if (row is null) return new Panel();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(Ui.Text(row.Path, 12, mono: true, wrap: false));
        grid.Children.Add(WithColumn(Ui.Text(row.Size, 12, Palette.TextDim, wrap: false), 1));
        grid.Children.Add(WithColumn(Ui.Pill(row.Available ? "available" : "unsafe path", row.Available ? Palette.Ok : Palette.Bad), 2));
        return grid;
    }

    // ───────────────────────── Migration ─────────────────────────

    private Grid BuildMigrationPage()
    {
        var tools = MakeList();
        tools.Name = "MigrationTools";
        tools.Height = 214;
        tools.ItemsSource = new ToolChoice[]
        {
            new("node", "Node.js", ".nvmrc, package.json and version managers"),
            new("deno", "Deno", ".dvmrc and version managers"),
            new("python", "Python", ".python-version, pyproject.toml and version managers"),
            new("jdk", "JDK", ".java-version and version managers"),
            new("gradle", "Gradle", "wrapper distribution URL; checksum files need manual handling"),
            new("ruby", "Ruby", ".ruby-version and version managers"),
            new("dotnet", ".NET SDK", "global.json")
        };
        tools.ItemTemplate = new FuncDataTemplate<ToolChoice>((choice, _) => new StackPanel { Spacing = 1, Children =
        {
            Ui.Text(choice?.Name, 13, bold: true, wrap: false), Ui.Dim(choice?.Detail, 11, wrap: false)
        }});
        _migrationEnvironments.MaxHeight = 112;
        _migrationEnvironments.ItemTemplate = new FuncDataTemplate<MigrationEnvironment>((environment, _) => new StackPanel { Spacing = 1, Children =
        {
            Ui.Text(environment?.Label, 13, bold: true, wrap: false), Ui.Dim(environment?.Detail, 11, wrap: false)
        }});
        _migrationEdits.ItemTemplate = new FuncDataTemplate<EditRow>((edit, _) => EditRowView(edit));
        var target = new TextBox { Name = "MigrationTarget", PlaceholderText = "Target version, for example 24" };
        var ranges = new CheckBox { Content = "Include compatibility ranges" };
        var prepare = Ui.Button("Review migration", "primary", "ReviewMigration");
        var apply = Ui.Button("Apply reviewed migration", "danger");
        apply.IsEnabled = false;
        void Invalidate()
        {
            _migrationToken = null;
            _migrationDistribution = null;
            _migrationOptions = null;
            _migrationEdits.ItemsSource = Array.Empty<EditRow>();
            _editCount.Text = "";
            apply.IsEnabled = false;
        }
        tools.SelectionChanged += (_, _) => Invalidate();
        _migrationEnvironments.SelectionChanged += (_, _) => Invalidate();
        target.TextChanged += (_, _) => Invalidate();
        ranges.IsCheckedChanged += (_, _) => Invalidate();
        void SetEnabled(bool enabled, bool includeApply = false)
        {
            prepare.IsEnabled = tools.IsEnabled = target.IsEnabled = ranges.IsEnabled = _migrationEnvironments.IsEnabled = enabled;
            if (includeApply) apply.IsEnabled = enabled;
        }
        prepare.Click += async (_, _) =>
        {
            if (_migrationBusy) return;
            Invalidate();
            if (_migrationEnvironments.SelectedItem is not MigrationEnvironment environment)
            { _status.Text = "Select an indexed environment."; return; }
            if (tools.SelectedItem is not ToolChoice choice || !ToolKinds.TryParse(choice.Id, out var tool))
            { _status.Text = "Select a tool to migrate."; return; }
            var version = target.Text?.Trim() ?? "";
            if (!MigrationPlanner.IsValidTarget(version)) { _status.Text = "Enter a valid target version identifier."; return; }
            var options = new MigrationOptions { Tool = tool, Target = version, IncludeRanges = ranges.IsChecked == true };
            _migrationBusy = true;
            SetEnabled(false);
            try
            {
                EditRow[] rows;
                int count;
                if (environment.Distribution is null)
                {
                    if (_view is null) { _status.Text = "Scan local roots before planning a migration."; return; }
                    var projects = _view.Snapshot.Projects.Where(project => !project.Stale).ToArray();
                    var plan = await Task.Run(() => MigrationPlanner.Plan(projects, options));
                    _migrationToken = plan.Fingerprint;
                    count = plan.Edits.Count;
                    rows = plan.Edits.Select(edit => MakeEdit(edit.ProjectName, edit.FullPath, edit.Change, edit.Before, edit.After))
                        .Concat(plan.Skipped.Select(MakeSkipped)).ToArray();
                }
                else
                {
                    var remote = await Task.Run(() => new WslSnapshots().MigrationPlan(environment.Distribution, options));
                    if (remote.Plan is null) { _status.Text = remote.Error ?? "Could not read the WSL plan."; return; }
                    _migrationToken = remote.Plan.Token;
                    _migrationDistribution = environment.Distribution;
                    count = remote.Plan.Edits.Count;
                    rows = remote.Plan.Edits.Select(edit => MakeEdit(edit.Project, edit.Path, edit.Change, edit.Before, edit.After))
                        .Concat(remote.Plan.Skipped.Select(MakeSkipped)).ToArray();
                }
                _migrationOptions = options;
                _migrationEdits.ItemsSource = rows;
                _editCount.Text = count == 1 ? "1 file will change" : count + " files will change";
                apply.IsEnabled = count > 0;
                _status.Text = $"{count} files to change. Review all edits and skipped files before applying.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { _status.Text = "Could not plan migration: " + ex.Message; }
            finally { _migrationBusy = false; SetEnabled(true); }
        };
        apply.Click += async (_, _) =>
        {
            if (_migrationBusy || _migrationToken is null || _migrationOptions is null) return;
            var token = _migrationToken;
            var distribution = _migrationDistribution;
            var options = _migrationOptions;
            _migrationBusy = true;
            SetEnabled(false, includeApply: true);
            apply.IsEnabled = false;
            try
            {
                if (distribution is null)
                {
                    var result = await Task.Run(() => MigrationExecution.Apply(_store, options, token));
                    _status.Text = (result.Error ?? $"Updated {result.Items.Count} files; rescan to refresh the index.") + " Backups: " + result.BackupDirectory;
                }
                else
                {
                    var result = await Task.Run(() => new WslSnapshots().Migrate(distribution, options, token));
                    _status.Text = result.Message;
                }
                Invalidate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { _status.Text = "Migration failed: " + ex.Message; }
            finally { _migrationBusy = false; SetEnabled(true); apply.IsEnabled = _migrationToken is not null; }
        };

        var controls = DetailPane(new StackPanel { Spacing = 8, Children =
        {
            Ui.Dim("Rewrites declarations that already exist in the projects of one environment. Nothing changes until you apply a reviewed plan.", 12),
            Ui.Label("Environment"),
            _migrationEnvironments,
            Ui.Label("Tool"),
            tools,
            Ui.Label("Target version"),
            target,
            ranges,
            prepare
        }});
        var editsHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 0, 2, 8) };
        editsHeader.Children.Add(Ui.Label("Planned edits"));
        editsHeader.Children.Add(WithColumn(_editCount, 1));
        var edits = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children =
        {
            editsHeader,
            WithRow(new Grid { Children = { _migrationEdits, EmptyOverlay(MigrationEmpty) } }, 1),
            WithRow(new Border { Margin = new Thickness(0, 12, 0, 0), Child = apply }, 2)
        }};
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("360,*"), ColumnSpacing = 20 };
        grid.Children.Add(controls);
        grid.Children.Add(WithColumn(edits, 1));
        MigrationEmpty.Text = "Choose an environment, a tool and a target version, then review the migration to see every file it would change.";
        return grid;
    }

    private readonly TextBlock MigrationEmpty = new();

    private static EditRow MakeEdit(string project, string path, string change, string before, string after) =>
        new(project, path, change, before, after, false, $"{project} — {path}\n{change}\nBEFORE:\n{before}\nAFTER:\n{after}");

    private static EditRow MakeSkipped(string skipped) => new("Skipped", "", skipped, "", "", true, "SKIPPED: " + skipped);

    private static Control EditRowView(EditRow? edit)
    {
        if (edit is null) return new Panel();
        if (edit.Skipped) return Ui.Text("Skipped — " + edit.Change, 12, Palette.Warn);
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Text(edit.Title, 13, bold: true, wrap: false));
        stack.Children.Add(Ui.Dim(edit.Path, 11, mono: true, wrap: false));
        stack.Children.Add(Ui.Dim(edit.Change, 12));
        stack.Children.Add(DiffBlock("−  ", edit.Before, Palette.Bad));
        stack.Children.Add(DiffBlock("+  ", edit.After, Palette.Ok));
        return stack;
    }

    private static Border DiffBlock(string marker, string text, Color color) => new Border
    {
        Background = new SolidColorBrush(color, 0.12),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 4),
        Child = Ui.Text(marker + text.TrimEnd('\r', '\n').Replace("\n", "\n   "), 12, color, mono: true)
    };

    // ───────────────────────── WSL ─────────────────────────

    private ScrollViewer BuildWslPage() => new()
    {
        Content = new StackPanel { MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Children = { BuildWslSection() } },
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private StackPanel BuildWslSection()
    {
        var find = Ui.Button("Find");
        var load = Ui.Button("Load index");
        var rescan = Ui.Button("Rescan");
        AutomationProperties.SetName(find, "Find WSL distributions");
        AutomationProperties.SetName(load, "Load selected WSL index");
        AutomationProperties.SetName(rescan, "Rescan selected WSL roots");
        void Wire(Button button, Func<Task> action) => button.Click += async (_, _) =>
        {
            if (_wslBusy) return;
            _wslBusy = true;
            find.IsEnabled = load.IsEnabled = rescan.IsEnabled = false;
            try { await action(); } finally { _wslBusy = false; find.IsEnabled = load.IsEnabled = rescan.IsEnabled = true; }
        };
        Wire(find, FindWslAsync);
        Wire(load, LoadWslAsync);
        Wire(rescan, ScanWslAsync);
        _wslNames.Height = 110;
        _wslNames.ItemTemplate = new FuncDataTemplate<WslChoice>((choice, _) => new StackPanel { Spacing = 1, Children =
        {
            Ui.Text(choice?.Name, 13, bold: true, wrap: false), Ui.Dim(choice?.Status, 11, wrap: false)
        }});
        return new StackPanel { Spacing = 10, Children =
        {
            Ui.Label("WSL distributions"),
            Ui.Dim("Each distribution keeps its own index. The devtools executable must already be installed there and scanned.", 12),
            _wslNames,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { find, load, rescan } }
        }};
    }

    // ───────────────────────── Shared layout ─────────────────────────

    private static Grid MasterDetail(Control list, Control detail)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 20 };
        grid.Children.Add(list);
        detail.MinWidth = 320;
        grid.Children.Add(WithColumn(detail, 1));
        return grid;
    }

    private static Border DetailPane(Control content) => Ui.Panel(new ScrollViewer
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    }, new Thickness(18, 16));

    private static TextBlock EmptyOverlay(TextBlock holder)
    {
        holder.IsVisible = false;
        holder.TextWrapping = TextWrapping.Wrap;
        holder.TextAlignment = TextAlignment.Center;
        holder.FontSize = 13;
        holder.Foreground = Palette.Brush(Palette.TextFaint);
        holder.MaxWidth = 340;
        holder.HorizontalAlignment = HorizontalAlignment.Center;
        holder.VerticalAlignment = VerticalAlignment.Center;
        holder.IsHitTestVisible = false;
        return holder;
    }

    private static Color StatusColor(string status) => status switch
    {
        "ok" => Palette.Ok,
        _ => Palette.Warn
    };

    private static Color ResolutionColor(ResolutionStatus status) => status switch
    {
        ResolutionStatus.Satisfied => Palette.Ok,
        ResolutionStatus.Missing => Palette.Bad,
        ResolutionStatus.UnsatisfiedRange or ResolutionStatus.Conflict or ResolutionStatus.Unknown => Palette.Warn,
        _ => Palette.TextFaint
    };

    private static string ResolutionLabel(ResolutionStatus status) => status switch
    {
        ResolutionStatus.Satisfied => "satisfied",
        ResolutionStatus.Missing => "not installed",
        ResolutionStatus.UnsatisfiedRange => "version mismatch",
        ResolutionStatus.Conflict => "conflict",
        ResolutionStatus.Unknown => "unreadable",
        ResolutionStatus.NotManaged => "not managed",
        ResolutionStatus.Ignored => "muted",
        _ => status.ToString().ToLowerInvariant()
    };

    private static (string, Color) HealthStyle(ToolchainHealth health) => health switch
    {
        ToolchainHealth.Ok => ("ok", Palette.Ok),
        ToolchainHealth.Unverified => ("unverified", Palette.Warn),
        ToolchainHealth.MissingBinary => ("missing binary", Palette.Bad),
        ToolchainHealth.Broken => ("broken", Palette.Bad),
        ToolchainHealth.Stub => ("stub", Palette.TextFaint),
        _ => (health.ToString().ToLowerInvariant(), Palette.TextFaint)
    };

    private static string Plural(int count, string unit) => count + " " + unit + (count == 1 ? "" : "s");

    private static string FormatAge(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalDays < 1) return "today";
        if (span.TotalDays < 31) return Plural((int)span.TotalDays, "day") + " ago";
        if (span.TotalDays < 365) return Plural((int)(span.TotalDays / 30), "month") + " ago";
        return Plural((int)(span.TotalDays / 365), "year") + " ago";
    }
}
