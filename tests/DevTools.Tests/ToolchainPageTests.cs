using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using DevTools.Core.Model;
using DevTools.Desktop;
using DevTools.Providers;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class ToolchainPageTests
{
    [Fact]
    public void ToolchainsPageGroupsByToolAndNeverSelectsAHeader()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["/r"], Initialised = true });
            store.Save(new IndexSnapshot { ScannedAt = DateTimeOffset.Now, Toolchains =
            [
                Tc(ToolKind.Python, "3.12.1", "pyenv"), Tc(ToolKind.Uv, "0.5.4", "system"), Tc(ToolKind.Python, "3.9.18", "pyenv"),
                Tc(ToolKind.Node, "20.11.0", "fnm"), Tc(ToolKind.Python, "3.13.0", "system"), Tc(ToolKind.Jdk, "21.0.2", "sdkman")
            ] });
            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "Navigation");
            tabs.SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var list = window.GetVisualDescendants().OfType<GroupedListBox>().Single();
            var items = list.ItemsSource!.Cast<IRow>().ToList();
            Assert.Equal(4, items.OfType<ToolGroupHeader>().Count());
            Assert.Equal(["Python", "Python", "Python"], items.OfType<ToolRow>().Where(r => r.Toolchain.Tool == ToolKind.Python).Select(_ => "Python"));

            var header = items.OfType<ToolGroupHeader>().First(h => h.Tool == ToolKind.Python);
            list.SelectedItem = header;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<ToolRow>(list.SelectedItem);

            if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_GROUPS_SCREENSHOT") is { Length: > 0 } shot)
                Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(window.CaptureRenderedFrame())
                    .Save(shot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            window.Close();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void ToolchainSearchFiltersByToolVersionManagerAndPath()
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["/r"], Initialised = true });
            store.Save(new IndexSnapshot { ScannedAt = DateTimeOffset.Now, Toolchains =
            [
                Tc(ToolKind.Python, "3.12.1", "pyenv"), Tc(ToolKind.Python, "3.9.18", "pyenv"),
                Tc(ToolKind.Node, "20.11.0", "fnm"), Tc(ToolKind.Jdk, "21.0.2", "sdkman")
            ] });
            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "Navigation").SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "ToolSearch");
            var list = window.GetVisualDescendants().OfType<GroupedListBox>().Single();
            List<ToolRow> Rows() => list.ItemsSource!.Cast<IRow>().OfType<ToolRow>().ToList();

            Assert.Equal(4, Rows().Count);
            foreach (var (query, expected) in new[] { ("python", 2), ("3.9", 1), ("FNM", 1), ("node.js", 1), ("/opt/jdk", 1), ("nothing-like-this", 0) })
            {
                search.Text = query;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected, Rows().Count);
            }

            search.Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(4, Rows().Count);
            window.Close();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void EndOfLifePillsAppearOnlyWhenTheSettingIsOn(bool enabled, bool expectPill)
    {
        AvaloniaTestHost.Ensure();
        var path = Path.Combine(Path.GetTempPath(), "devtools-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteStore(path);
            store.SaveConfig(new StoreConfig { Roots = ["/r"], Initialised = true, CheckEndOfLife = enabled });
            store.Save(new IndexSnapshot { ScannedAt = DateTimeOffset.Now, Toolchains = [Tc(ToolKind.Node, "20.11.0", "fnm")] });
            // Seed a fresh cache through the real service with a canned response, so the window never touches the network.
            var json = "[{\"cycle\":\"20\",\"eol\":\"2026-04-30\"}]";
            new EndOfLifeService(path, new CannedHandler(json)).Refresh([ToolKind.Node], DateTimeOffset.UtcNow);

            var window = new MainWindow(path);
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "Navigation").SelectedIndex = 1;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var row = window.GetVisualDescendants().OfType<GroupedListBox>().Single().ItemsSource!.Cast<IRow>().OfType<ToolRow>().Single();
            Assert.Equal(expectPill, row.Eol is { State: EolState.Ended });
            Assert.Equal(expectPill, row.Plain.Contains("End of life:", StringComparison.Ordinal));
            var texts = string.Join(" | ", window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Equal(expectPill, texts.Contains("end of life", StringComparison.Ordinal));
            window.Close();
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class CannedHandler(string json) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private static Toolchain Tc(ToolKind tool, string version, string manager) =>
        new() { Tool = tool, Version = version, Manager = manager, Path = "/opt/" + tool.ToString().ToLowerInvariant() + "/" + version, Health = ToolchainHealth.Ok };
}
