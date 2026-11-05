using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DevTools.Desktop;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class SupportedPageTests
{
    private static string Texts(Avalonia.Visual root) =>
        string.Join("\n", root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));

    [Fact]
    public void SupportedPageShowsEveryToolAndFilters()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["/r"], Initialised = true });
            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var nav = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "Navigation");
            var index = nav.ItemsSource!.Cast<NavItem>().ToList().FindIndex(item => item.Label == "Supported");
            Assert.True(index >= 0, "Supported must be in the sidebar");
            nav.SelectedIndex = index;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var shown = Texts(window);
            foreach (var expected in new[] { "Node.js", "Maven", "Bazel", "Leiningen", "Spring Boot", "React", "Detected by" })
                Assert.Contains(expected, shown);
            Assert.Contains("version read from project files", shown);
            Assert.Contains("found on this machine", shown);
            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_SUPPORTED_SCREENSHOT") is { Length: > 0 } shot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(shot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

            var filter = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "SupportFilter");
            filter.Text = "react";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var filtered = Texts(window);
            Assert.Contains("React", filtered);
            Assert.DoesNotContain("Maven", filtered);

            filter.Text = "no-such-thing-xyz";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains("Nothing matches", Texts(window));
            window.Close();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }
}
