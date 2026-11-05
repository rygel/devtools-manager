using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using DevTools.Providers;
using DevTools.Store;

namespace DevTools.Desktop;

public sealed partial class MainWindow
{
    private readonly Button _prepareUpdate = Ui.Button("Prepare update options", null, "PrepareUpdate");
    private readonly Button _update = Ui.Button("Update selected", "primary", "UpdateSelected");
    private readonly Button _cancelUpdate = new() { Content = "Cancel update", IsEnabled = false, Name = "CancelUpdate" };
    private readonly CheckBox _updateOutdatedOnly = new() { Content = "Only outdated", Name = "UpdateOutdatedOnly" };
    private readonly ListBox _updateOptions = MakeList();
    private CancellationTokenSource? _updateCancellation;

    /// <summary>
    /// Lists the update command each installed toolchain's own manager documents (docs/UPDATING.md).
    /// Nothing runs until one option is selected and confirmed with the button.
    /// </summary>
    private StackPanel BuildUpdateSection()
    {
        _updateOptions.Name = "UpdateOptions";
        _updateOptions.Height = 190;
        _updateOptions.ItemTemplate = new FuncDataTemplate<UpdateRow>((row, _) => UpdateRowView(row));
        _update.IsEnabled = false;
        _updateOptions.SelectionChanged += (_, _) => _update.IsEnabled = !_installBusy && _updateOptions.SelectedItem is UpdateRow;
        _prepareUpdate.Click += (_, _) => PrepareUpdates();
        _update.Click += async (_, _) => await UpdateSelectedAsync();
        _cancelUpdate.Click += (_, _) => _updateCancellation?.Cancel();

        return new StackPanel { Spacing = 8, Children =
        {
            Ui.Divider(),
            Ui.Label("Updates"),
            Ui.Dim("Lists the update command each installed toolchain's own manager documents. Tools without one show the manual route.", 12),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _prepareUpdate, _updateOutdatedOnly } },
            Ui.Dim("Newer-release checks use the end-of-life data from Settings. They are off when that setting is off, and JDK and .NET SDK installs are not compared.", 11),
            _updateOptions,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _update, _cancelUpdate } }
        }};
    }

    private void PrepareUpdates()
    {
        _updateOptions.ItemsSource = Array.Empty<UpdateRow>();
        if (_view is null) { _status.Text = "Scan first, then prepare updates."; return; }

        var outdatedOnly = _updateOutdatedOnly.IsChecked == true;
        if (outdatedOnly && (!_checkEol || _eolData.Count == 0))
        {
            _status.Text = "No release data yet. Turn on end-of-life data in Settings and wait for it to load, or clear \"Only outdated\".";
            return;
        }

        var plan = UpdatePlanner.Build(_view, null, _checkEol ? _eolData : null, outdatedOnly);
        var rows = plan.Steps.Select(step => new UpdateRow(step,
            $"{step.Tool} {step.Version} · {(step.Manual ? "manual action" : "ready to run")}\n{step.Display}")).ToArray();
        _updateOptions.ItemsSource = rows;
        _status.Text = rows.Length == 0
            ? (outdatedOnly ? "Nothing is known to be outdated." : "No installed toolchain has a documented update route.")
            : $"{rows.Length} update options for this machine. WSL distributions are updated from inside the distribution.";
    }

    private async Task UpdateSelectedAsync()
    {
        if (_installBusy || _updateOptions.SelectedItem is not UpdateRow choice) return;
        if (choice.Step.Manual) { _status.Text = choice.Step.Hint ?? choice.Step.Description; return; }

        _installBusy = true;
        _updateCancellation = new CancellationTokenSource();
        _cancelUpdate.IsEnabled = true;
        _update.IsEnabled = false;
        _busy.IsVisible = true;
        try
        {
            _status.Text = "Updating " + choice.Step.Tool + "; the command may download files.";
            var token = _updateCancellation.Token;
            // Only the selected step runs; its token is rechecked against the live index before anything starts.
            var result = await Task.Run(() => UpdateExecution.Apply(_store, choice.Step.Tool, choice.Step.Plan, choice.Step.Plan, cancellationToken: token));
            _status.Text = result.Error ?? string.Join("; ", result.Items.Select(item => item.Tool + ": " + item.Message));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        { _status.Text = "Update stopped: " + ex.Message; }
        finally
        {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _cancelUpdate.IsEnabled = false;
            _installBusy = false;
            _busy.IsVisible = false;
            _update.IsEnabled = _updateOptions.SelectedItem is UpdateRow;
        }
    }

    private static Control UpdateRowView(UpdateRow? row)
    {
        if (row is null) return new Panel();
        var step = row.Step;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            Ui.Text(step.Tool, 13, bold: true, wrap: false),
            Ui.Dim(step.Version, 12, wrap: false),
            Ui.Pill(step.Manual ? "manual" : "ready", step.Manual ? Palette.Warn : Palette.Ok)
        }};
        if (step.Outdated == true) head.Children.Add(Ui.Pill("newer: " + step.Available, Palette.Accent));
        else if (step.Outdated == false) head.Children.Add(Ui.Dim("current", 11, wrap: false));
        return new StackPanel { Spacing = 2, Children = { head, Ui.Dim(step.Display, 11, mono: true) } };
    }
}
