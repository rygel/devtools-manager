using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DevTools.Core.Model;
using DevTools.Desktop;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class DesktopTests
{
    [Fact]
    public void ProjectListShowsRequirementsAndFiltersWithoutOpeningDetails()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            var projectPath = Path.Combine(path, "sample");
            var artifactPath = Path.Combine(projectPath, "node_modules");
            Directory.CreateDirectory(artifactPath);
            File.WriteAllText(Path.Combine(artifactPath, "fixture"), "fixture");
            File.WriteAllText(Path.Combine(projectPath, ".nvmrc"), "20\n");
            store.SaveConfig(new StoreConfig { Roots = ["/sample"], Initialised = true });
            store.Save(new IndexSnapshot { ScannedAt = DateTimeOffset.Now, Projects =
            [
                new ProjectRecord { Name = "sample", Path = projectPath, State = ProjectState.Active,
                    Requirements = [Requirement.Pinned(ToolKind.Node, "20", Provenance.FileOf(".nvmrc", 1))],
                    Frameworks = [new FrameworkUse { Id = "react", Name = "React", Ecosystem = Ecosystem.Node, Version = "^18.2.0",
                        Provenance = Provenance.FileOf("package.json", 4, "react") }] },
                new ProjectRecord { Name = "healthy", Path = "/healthy", State = ProjectState.Active }
            ] });
            var window = new MainWindow(path);
            if (double.TryParse(Environment.GetEnvironmentVariable("DEVTOOLS_TEST_WIDTH"), out var width)) window.Width = width;
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_SCREENSHOT") is { Length: > 0 } screenshot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "ProjectList");
            var row = Assert.Single(list.ItemsSource!.Cast<IRow>().Select(r => r.Plain), value => value.Contains("sample", StringComparison.Ordinal));
            Assert.Contains("sample", row);
            Assert.Contains(".nvmrc:1", row);
            Assert.Contains("Frameworks: React ^18.2.0", row);
            var problems = window.GetVisualDescendants().OfType<CheckBox>().Single(x => Equals(x.Content, "Needs attention only"));
            problems.IsChecked = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Single(list.ItemsSource!.Cast<IRow>().Select(r => r.Plain));
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "ProjectSearch");
            search.Text = "react";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Single(list.ItemsSource!.Cast<IRow>().Select(r => r.Plain), value => value.Contains("sample", StringComparison.Ordinal));
            search.Text = "no-match";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(list.ItemsSource!.Cast<IRow>().Select(r => r.Plain));

            var tabs = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "Navigation");
            tabs.SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var prepare = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Prepare installation options"));
            prepare.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var options = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "InstallOptions");
            var installRow = Assert.Single(options.ItemsSource!.Cast<IRow>().Select(r => r.Plain));
            Assert.Contains("node", installRow, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sample", installRow);
            Assert.Contains("Projects:", installRow);
            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_TOOLS_SCREENSHOT") is { Length: > 0 } toolsScreenshot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(toolsScreenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            tabs.SelectedIndex = 2;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var disk = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "DiskList");
            var diskRow = Assert.Single(disk.ItemsSource!.Cast<IRow>().Select(r => r.Plain), value => value.Contains(projectPath, StringComparison.Ordinal));
            disk.SelectedItem = disk.ItemsSource!.Cast<DiskRow>().Single(r => r.Plain == diskRow);
            var inspect = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "InspectCleanup");
            inspect.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var targets = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "CleanupTargets");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return targets.ItemsSource?.Cast<IRow>().Any() == true;
            }, TimeSpan.FromSeconds(3)));
            var cleanupRow = Assert.Single(targets.ItemsSource!.Cast<IRow>().Select(r => r.Plain));
            Assert.Contains(artifactPath, cleanupRow);
            Assert.Contains("7 B", cleanupRow);
            Assert.Contains("available", cleanupRow);
            Assert.True(File.Exists(Path.Combine(artifactPath, "fixture")));
            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_DISK_SCREENSHOT") is { Length: > 0 } diskScreenshot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(diskScreenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            tabs.SelectedIndex = 3;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var migrationTools = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "MigrationTools");
            migrationTools.SelectedIndex = 0;
            var migrationTarget = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "MigrationTarget");
            migrationTarget.Text = "24";
            var review = window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ReviewMigration");
            review.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var edits = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "MigrationEdits");
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                return edits.ItemsSource?.Cast<IRow>().Any() == true;
            }, TimeSpan.FromSeconds(3)));
            var migrationRow = Assert.Single(edits.ItemsSource!.Cast<IRow>().Select(r => r.Plain));
            Assert.Contains("BEFORE:\n20", migrationRow);
            Assert.Contains("AFTER:\n24", migrationRow);
            Assert.Equal("20\n", File.ReadAllText(Path.Combine(projectPath, ".nvmrc")));
            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_MIGRATION_SCREENSHOT") is { Length: > 0 } migrationScreenshot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(migrationScreenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();

            using (var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + store.DatabasePath + ";Pooling=False"))
            {
                database.Open();
                using var corrupt = database.CreateCommand();
                corrupt.CommandText = "UPDATE config SET json = '{invalid' WHERE id = 1";
                Assert.Equal(1, corrupt.ExecuteNonQuery());
            }
            var errorWindow = new MainWindow(path);
            errorWindow.Show();
            Assert.Contains(errorWindow.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "The configuration could not be read.");
            Assert.Contains(errorWindow.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Scan roots") && !button.IsEnabled);
            errorWindow.Close();
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void DiskSearchNarrowsTheListByName()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.Save(new IndexSnapshot
            {
                Projects =
                [
                    new ProjectRecord { Name = "alpha-api", Path = "/code/alpha-api", State = ProjectState.Active, SizeBytes = 900 },
                    new ProjectRecord { Name = "beta-web", Path = "/code/beta-web", State = ProjectState.Active, SizeBytes = 100 }
                ]
            });
            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var list = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "DiskList");
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "DiskSearch");
            Assert.Equal(2, list.ItemsSource!.Cast<DiskRow>().Count());

            search.Text = "beta";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal("beta-web", Assert.Single(list.ItemsSource!.Cast<DiskRow>()).Project.Name);

            search.Text = "no-match";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Empty(list.ItemsSource!.Cast<DiskRow>());

            search.Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, list.ItemsSource!.Cast<DiskRow>().Count());
            window.Close();
        }
        finally { Directory.Delete(path, true); }
    }
}
