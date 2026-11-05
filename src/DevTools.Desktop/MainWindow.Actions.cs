using Avalonia.Controls;
using Avalonia.Input.Platform;
using DevTools.Core.Model;
using DevTools.Providers;
using DevTools.Store;

namespace DevTools.Desktop;

public sealed partial class MainWindow
{
    private void PopulateLists()
    {
        var environments = new List<MigrationEnvironment>();
        if (_view is not null)
            environments.Add(new MigrationEnvironment("Local" + LocalSuffix(_view), _view.Projects.Count + " projects", null));
        environments.AddRange(_wslViews.Select(pair => new MigrationEnvironment("WSL: " + pair.Key,
            pair.Value.Projects.Count + " projects  ·  roots: " + string.Join(", ", pair.Value.Snapshot.ConfiguredRoots), pair.Key)));
        var previouslySelected = (_migrationEnvironments.SelectedItem as MigrationEnvironment)?.Distribution;
        _migrationEnvironments.ItemsSource = environments;
        _migrationEnvironments.SelectedItem = environments.FirstOrDefault(environment => environment.Distribution == previouslySelected);
        PopulateProjects();

        _eolData = _checkEol ? _eol.Load() : new Dictionary<ToolKind, IReadOnlyList<EolCycle>>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var toolRows = Machines().SelectMany(machine => machine.View.Snapshot.Toolchains.Select(t =>
        {
            var used = machine.View.Projects.Count(p => p.Resolution.Tools.Any(r => r.Toolchain?.Path == t.Path));
            var plain = $"[{machine.View.Snapshot.EnvironmentName}] {ToolKinds.Display(t.Tool)}  {t.Version}  ·  {t.Manager ?? "unknown manager"}  ·  {(t.Stale ? "stale scan" : t.Health.ToString())}  ·  used by {used} projects{(t.IsGlobalDefault ? "  ·  default" : "")}\n{t.Path}" + (t.HealthDetail is null ? "" : "\n" + t.HealthDetail);
            var eol = _checkEol ? EndOfLifeAssessor.Assess(_eolData, t, today) : null;
            if (eol is { State: not EolState.Unknown }) plain += "\nEnd of life: " + eol.Describe();
            return new ToolRow(t, machine.View, machine.IsWsl, used, plain, eol);
        })).ToArray();
        _allToolRows = toolRows;
        PopulateTools();

        var diskProjects = Machines().SelectMany(machine => machine.View.Snapshot.Projects
            .Where(p => p.State == ProjectState.Active).Select(p => (Project: p, Machine: machine))).ToList();
        var largest = diskProjects.Max(x => x.Project.SizeBytes) ?? 0;
        _allDiskRows = diskProjects.OrderByDescending(x => x.Project.SizeBytes).Select(x =>
        {
            var p = x.Project;
            var plain = $"[{x.Machine.View.Snapshot.EnvironmentName}] {p.DisplayName}  ·  {FormatBytes(p.SizeBytes)}  ·  artifacts {FormatBytes(p.ArtifactBytes)}{(p.Stale ? "  ·  stale scan" : "")}\n{p.Path}";
            return new DiskRow(p, x.Machine.View, x.Machine.IsWsl, largest > 0 ? (p.SizeBytes ?? 0) / (double)largest : 0, plain);
        }).ToArray();
        PopulateDisk();
        _cleanupTargets.ItemsSource = Array.Empty<CleanupRow>();
        SetNavigation();
        ShowProject();
        ShowTool();
        ShowDisk();
    }

    private static string LocalName(MachineView view) => view.Snapshot.EnvironmentName == "Unknown" ? "Local" : view.Snapshot.EnvironmentName;

    private static string LocalSuffix(MachineView view) => view.Snapshot.EnvironmentName == "Unknown" ? "" : ": " + view.Snapshot.EnvironmentName;

    private void PopulateTools()
    {
        var query = _toolSearch.Text?.Trim() ?? "";
        var rows = _allToolRows.Where(row => query.Length == 0 ||
            (row.Plain + " " + ToolIcons.Title(row.Toolchain.Tool)).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = _tools.SelectedItem as ToolRow;
        _tools.ItemsSource = ToolGroups.Build(rows);
        // Keep the open installation selected when the filter still shows it.
        if (selected is not null)
            _tools.SelectedItem = rows.FirstOrDefault(row => row.Toolchain.Tool == selected.Toolchain.Tool && row.Toolchain.Path == selected.Toolchain.Path && row.Machine == selected.Machine);
        _toolCount.Text = query.Length == 0
            ? _allToolRows.Length.ToString("N0") + (_allToolRows.Length == 1 ? " installation" : " installations")
            : rows.Length.ToString("N0") + " of " + _allToolRows.Length.ToString("N0");
        _toolsEmpty.Text = _allToolRows.Length == 0
            ? "No toolchains yet. Scan roots to find installed Node, JDK, Maven, Gradle and more."
            : "No toolchains match the search.";
        _toolsEmpty.IsVisible = rows.Length == 0;
    }

    private void PopulateProjects()
    {
        var query = _search.Text?.Trim() ?? "";
        var rows = Machines().SelectMany(machine => machine.View.Projects.Where(p => p.Project.State == ProjectState.Active)
            .Where(p => _problemsOnly.IsChecked != true || p.HasProblems)
            .Where(p => query.Length == 0 || (p.Project.DisplayName + " " + p.Project.Path + " " + string.Join(" ", p.Project.Ecosystems) + " " + string.Join(" ", p.Project.Frameworks.Select(f => f.Name)))
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var project = p.Project;
                var status = project.Stale ? "stale scan" : project.Incomplete ? "incomplete" : project.HasCiDrift ? "CI drift" : p.HasProblems ? "needs attention" : "ok";
                return new ProjectRow(p, machine.View, machine.IsWsl, status);
            })).ToArray();
        _projects.ItemsSource = rows;
        _projectCount.Text = rows.Length.ToString("N0") + (rows.Length == 1 ? " project" : " projects");
        _projectsEmpty.Text = _view is null && _wslViews.Count == 0
            ? "No projects yet. Add scan roots in Settings, then press Scan roots."
            : "No projects match the current filters.";
        _projectsEmpty.IsVisible = rows.Length == 0;
    }

    /// <summary>Folder, terminal and editor shortcuts act on a local path, so they are off for projects inside WSL.</summary>
    private void UpdateShortcuts()
    {
        var local = _projects.SelectedItem is ProjectRow { IsWsl: false } row ? row.View.Project.Path : null;
        _openFolder.IsEnabled = local is not null && ProjectLaunch.Plan(LaunchKind.Folder, local) is not null;
        _openTerminal.IsEnabled = local is not null && ProjectLaunch.Plan(LaunchKind.Terminal, local) is not null;
        _openEditor.IsEnabled = local is not null && ProjectLaunch.Plan(LaunchKind.Editor, local) is not null;
        ToolTip.SetTip(_openTerminal, local is not null && !_openTerminal.IsEnabled ? "No portable terminal command on this platform" : null);
        ToolTip.SetTip(_openEditor, local is not null && !_openEditor.IsEnabled ? "VS Code (the `code` command) was not found on PATH" : null);
        _copyMise.IsEnabled = _projects.SelectedItem is ProjectRow;
    }

    private void Launch(LaunchKind kind, string label)
    {
        if (_projects.SelectedItem is not ProjectRow { IsWsl: false } row) return;
        var command = ProjectLaunch.Plan(kind, row.View.Project.Path);
        if (command is null) { _status.Text = "Cannot open " + label + " for this project here."; return; }
        _status.Text = ProjectLaunch.Start(command) ?? "Opened " + label + " for " + row.View.Project.DisplayName + ".";
    }

    private async Task CopyMiseAsync()
    {
        if (_projects.SelectedItem is not ProjectRow selected) return;
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { _status.Text = "Clipboard is unavailable."; return; }
        var export = MiseExport.Build(selected.View.Project);
        await clipboard.SetTextAsync(export.Text);
        _status.Text = export.Skipped.Count == 0
            ? "mise.toml copied."
            : $"mise.toml copied; {export.Skipped.Count} tool{(export.Skipped.Count == 1 ? "" : "s")} not exported (listed as comments).";
    }

    private async Task CopyEnvironmentAsync()
    {
        if (_projects.SelectedItem is not ProjectRow selected)
        {
            _status.Text = "Select a project before copying its environment.";
            return;
        }
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { _status.Text = "Clipboard is unavailable."; return; }
        string text;
        if (selected.IsWsl)
        {
            var distribution = selected.Machine.Snapshot.EnvironmentName["WSL: ".Length..];
            var (script, error) = await Task.Run(() => new WslSnapshots().Environment(distribution, selected.View.Project.Path));
            if (script is null) { _status.Text = "Could not read the WSL environment: " + error; return; }
            text = script;
        }
        else
        {
            var patch = EnvComposer.Compose(selected.View, new DevTools.Core.Resolution.ResolutionEngine(selected.Machine.Snapshot.Toolchains));
            text = patch.Render(OperatingSystem.IsWindows() ? "pwsh" : "sh");
        }
        if (text.Length == 0) { _status.Text = "No environment changes are available for this project."; return; }
        await clipboard.SetTextAsync(text);
        _status.Text = selected.IsWsl ? "WSL shell environment copied." : "Environment copied.";
    }

    private async Task PrepareInstallAsync()
    {
        _installOptions.ItemsSource = Array.Empty<InstallRow>();
        var rows = new List<InstallRow>();
        void Add(string environment, string? distribution, InstallPlan plan)
        {
            foreach (var step in plan.Steps)
            {
                var plain = $"[{environment}] {step.Tool} {step.Version} · {(step.Manual ? "manual action" : "ready to install")} · {(step.RequiresNetwork ? "network required" : "network requirement not specified")}\n{step.Display}\nProjects: {string.Join(", ", step.Projects)}";
                rows.Add(new InstallRow(step, distribution, environment, plain));
            }
        }
        if (_view is not null) Add(LocalName(_view), null, InstallPlanner.Build(_view));
        var errors = new List<string>();
        foreach (var distribution in _wslViews.Keys.ToArray())
        {
            var result = await Task.Run(() => new WslSnapshots().InstallationPlan(distribution));
            if (result.Plan is not null) Add("WSL: " + distribution, distribution, result.Plan);
            else errors.Add(distribution + ": " + (result.Error ?? "No plan returned."));
        }
        _installOptions.ItemsSource = rows;
        _status.Text = errors.Count > 0 ? string.Join("; ", errors) : $"{rows.Count} installation options. Select one to install or see its manual instructions.";
    }

    private async Task InstallSelectedAsync()
    {
        if (_installBusy || _installOptions.SelectedItem is not InstallRow choice) return;
        if (choice.Step.Manual) { _status.Text = choice.Step.Hint ?? choice.Step.Description; return; }
        _installBusy = true;
        _installCancellation = new CancellationTokenSource();
        _cancelInstall.IsEnabled = choice.Distribution is null;
        _busy.IsVisible = true;
        try
        {
            _status.Text = "Installing; the selected command may download files.";
            if (choice.Distribution is null)
            {
                var token = _installCancellation.Token;
                var result = await Task.Run(() => InstallExecution.Apply(_store, choice.Step.Tool, choice.Step.Version, choice.Step.Plan, token));
                _status.Text = result.Error ?? string.Join("; ", result.Items.Select(item => item.Message));
            }
            else
            {
                var result = await Task.Run(() => new WslSnapshots().Install(choice.Distribution, choice.Step));
                _status.Text = result.Message;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        { _status.Text = "Installation stopped: " + ex.Message; }
        finally
        {
            _installCancellation.Dispose();
            _installCancellation = null;
            _cancelInstall.IsEnabled = false;
            _installBusy = false;
            _busy.IsVisible = false;
        }
    }

    private async Task FindWslAsync()
    {
        _status.Text = "Finding WSL distributions…";
        var (names, error) = await Task.Run(() => new WslSnapshots().List());
        _wslNames.ItemsSource = names.Select(name => new WslChoice(name,
            _wslStatus.GetValueOrDefault(name, "index not checked"))).ToArray();
        _status.Text = error ?? (names.Count == 0 ? "No WSL distributions found." : "Select a distribution and load its index.");
    }

    private async Task VerifyToolAsync()
    {
        if (_tools.SelectedItem is not ToolRow selected)
        { _status.Text = "Select a toolchain to verify."; return; }
        if (selected.Toolchain.Health != ToolchainHealth.Unverified)
        { _status.Text = "Only unverified installations can be checked here."; return; }
        _status.Text = "Running the selected executable's version probe…";
        if (selected.IsWsl)
        {
            var distribution = selected.Machine.Snapshot.EnvironmentName["WSL: ".Length..];
            var service = new WslSnapshots();
            var (succeeded, message) = await Task.Run(() => service.Verify(distribution, selected.Toolchain.Path));
            if (!succeeded) { _status.Text = message; return; }
            var (snapshot, error) = await Task.Run(() => service.Load(distribution));
            if (snapshot is null) { _status.Text = "Verified, but refresh failed: " + error; return; }
            _wslViews[distribution] = MachineView.Build(snapshot);
            PopulateLists();
            _status.Text = message;
        }
        else
        {
            var result = await Task.Run(() => ToolchainVerification.Run(_store, selected.Toolchain.Path));
            if (result.Succeeded) LoadSnapshot();
            _status.Text = result.Message;
        }
    }

    private async Task InspectCleanupAsync()
    {
        _cleanupTargets.ItemsSource = Array.Empty<CleanupRow>();
        if (_disk.SelectedItem is not DiskRow selected)
        { _status.Text = "Select a project to inspect its cleanup targets."; return; }
        if (selected.Project.Stale) { _status.Text = "Rescan this project before planning cleanup."; return; }
        _status.Text = "Measuring cleanup targets…";
        try
        {
            if (selected.IsWsl)
            {
                var distribution = selected.Machine.Snapshot.EnvironmentName["WSL: ".Length..];
                var (remote, error) = await Task.Run(() => new WslSnapshots().CleanupPlan(distribution, selected.Project.Path));
                if (error is not null) { _status.Text = error; return; }
                _cleanupTargets.ItemsSource = remote.Select(target => MakeCleanup(null, target, selected.Project.Path, distribution,
                    target.Path, target.SizeBytes, target.Available)).ToArray();
                _status.Text = remote.Count == 0 ? "No cleanup targets found." : "Select a target to move it to trash. Sizes are logical file bytes.";
                return;
            }
            var targets = await Task.Run(() => ArtifactCleanup.Plan([selected.Project], selected.Project.Path,
                measure: true, cancellationToken: _shutdown.Token));
            var rows = targets.Select(target => MakeCleanup(target, null, selected.Project.Path, null,
                target.Directory, target.SizeBytes, !ArtifactCleanup.HasUnsafePath(target.Directory))).ToArray();
            _cleanupTargets.ItemsSource = rows;
            _status.Text = rows.Length == 0 ? "No cleanup targets found." : "Select a target to move it to trash. Sizes are logical file bytes.";
        }
        catch (OperationCanceledException) { _status.Text = "Cleanup inspection cancelled."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _status.Text = "Cleanup inspection failed: " + ex.Message; }
    }

    private static CleanupRow MakeCleanup(ArtifactTarget? local, WslCleanupTarget? remote, string projectPath, string? distribution,
        string path, long? bytes, bool available)
    {
        var size = bytes is null ? "size unavailable" : FormatBytes(bytes);
        return new CleanupRow(local, remote, projectPath, distribution, path, size, available,
            path + "  ·  " + size + (available ? "  ·  available" : "  ·  unavailable: unsafe path"));
    }

    private async Task MoveCleanupAsync()
    {
        if (_cleanupTargets.SelectedItem is not CleanupRow choice)
        { _status.Text = "Select a cleanup target first."; return; }
        if (!choice.Available) { _status.Text = "The selected target is unavailable or unsafe."; return; }
        _status.Text = "Moving the selected target to trash…";
        try
        {
            if (choice.Remote is not null && choice.Distribution is not null)
            {
                var service = new WslSnapshots();
                var (success, message) = await Task.Run(() => service.Cleanup(choice.Distribution, choice.ProjectPath, choice.Remote));
                var (snapshot, error) = await Task.Run(() => service.Load(choice.Distribution));
                if (snapshot is null) { _wslViews.Remove(choice.Distribution); message += " Refresh failed: " + error; }
                else _wslViews[choice.Distribution] = MachineView.Build(snapshot);
                PopulateLists();
                _status.Text = message + (success ? " Rescan to refresh disk measurements." : "");
            }
            else if (choice.Local is { } target)
            {
                var token = ArtifactCleanup.Fingerprint([target]);
                var result = await Task.Run(() => CleanupExecution.Apply(_store, target.Project.Path, null,
                    Path.GetFileName(target.Directory), token));
                if (result.Items.Any(item => item.Moved)) LoadSnapshot();
                _status.Text = (result.Error ?? string.Join(" ", result.Items.Select(item => item.Message))) +
                    (result.Succeeded ? " Rescan to refresh disk measurements." : "");
            }
            _cleanupTargets.ItemsSource = Array.Empty<CleanupRow>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _status.Text = "Cleanup failed: " + ex.Message; }
    }

    private async Task LoadWslAsync()
    {
        if (_wslNames.SelectedItem is not WslChoice choice) { _status.Text = "Select a WSL distribution first."; return; }
        var name = choice.Name;
        _status.Text = $"Loading the {name} index…";
        var (snapshot, error) = await Task.Run(() => new WslSnapshots().Load(name));
        if (snapshot is null)
        {
            _wslViews.Remove(name);
            SetWslStatus(name, "unavailable");
            PopulateLists();
            _status.Text = error;
            return;
        }
        _wslViews[name] = MachineView.Build(snapshot);
        SetWslStatus(name, (snapshot.Stats.Partial ? "incomplete; roots: " : "roots: ") +
            (snapshot.ConfiguredRoots.Count == 0 ? "none configured" : string.Join(", ", snapshot.ConfiguredRoots)));
        PopulateLists();
        _status.Text = $"Loaded {snapshot.EnvironmentName}, last scanned {snapshot.ScannedAt:g}." + (snapshot.Stats.Partial ? " The scan was incomplete." : "");
    }

    private async Task ScanWslAsync()
    {
        if (_wslNames.SelectedItem is not WslChoice choice || !_wslViews.TryGetValue(choice.Name, out var view))
        { _status.Text = "Select a WSL distribution and load its index before scanning."; return; }
        if (view.Snapshot.ConfiguredRoots.Count == 0)
        { _status.Text = "No WSL roots are configured. Add roots inside the distribution first."; return; }
        _status.Text = "Scanning the configured WSL roots…";
        var measure = _measure.IsChecked == true;
        var (succeeded, message) = await Task.Run(() => new WslSnapshots().Scan(choice.Name, measure));
        if (!succeeded)
        {
            _wslViews.Remove(choice.Name);
            SetWslStatus(choice.Name, "scan failed; reload index");
            PopulateLists();
            _status.Text = message;
            return;
        }
        await LoadWslAsync();
    }

    private void SetWslStatus(string name, string status)
    {
        _wslStatus[name] = status;
        if (_wslNames.ItemsSource is not IEnumerable<WslChoice> choices) return;
        var updated = choices.Select(choice => choice.Name == name ? new WslChoice(name, status) : choice).ToArray();
        _wslNames.ItemsSource = updated;
        _wslNames.SelectedItem = updated.FirstOrDefault(choice => choice.Name == name);
    }
}
