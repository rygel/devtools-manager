using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DevTools.Core.Model;
using DevTools.Desktop;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class ProjectShortcutTests
{
    [Fact]
    public void ShortcutsAreHiddenWithoutASelectionAndEnabledForALocalProject()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["/r"], Initialised = true });
            store.Save(new IndexSnapshot { ScannedAt = DateTimeOffset.Now, Projects =
            [
                new ProjectRecord { Name = "demo", Path = path, State = ProjectState.Active,
                    Requirements = [Requirement.Pinned(ToolKind.Node, "20", Provenance.FileOf(".nvmrc", 1))] }
            ] });
            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Button Find(string name) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            Assert.False(Find("OpenFolder").IsEffectivelyVisible);

            var list = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "ProjectList");
            list.SelectedIndex = 0;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            Assert.True(Find("OpenFolder").IsEffectivelyVisible);
            Assert.True(Find("OpenFolder").IsEnabled);
            Assert.True(Find("CopyMise").IsEnabled);
            Assert.NotNull(Find("OpenTerminal"));
            Assert.NotNull(Find("OpenEditor"));
            window.Close();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
