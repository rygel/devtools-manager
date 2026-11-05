using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DevTools.Desktop;
using DevTools.Store;
using Xunit;

namespace DevTools.Tests;

[Collection("Avalonia")]
public sealed class SettingsDialogTests
{
    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(x => x.Name == name);

    private static SettingsDialog Open(StoreConfig config)
    {
        AvaloniaTestHost.Ensure();
        var dialog = new SettingsDialog(config);
        dialog.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return dialog;
    }

    [Fact]
    public void ShowsTheCurrentConfigurationAndReturnsEditedValues()
    {
        var config = new StoreConfig { Roots = ["C:\\Develop", "D:\\Code"], Excludes = ["vendor"], MaxDepth = 8, FollowSymlinks = false, MeasureSizes = false, Initialised = true };
        var dialog = Open(config);
        Assert.Equal("C:\\Develop" + Environment.NewLine + "D:\\Code", Find<TextBox>(dialog, "SettingsRoots").Text);
        Assert.Equal("vendor", Find<TextBox>(dialog, "SettingsExcludes").Text);
        Assert.Equal(8, (int)Find<NumericUpDown>(dialog, "SettingsMaxDepth").Value!);

        if (Environment.GetEnvironmentVariable("DEVTOOLS_TEST_SETTINGS_SCREENSHOT") is { Length: > 0 } shot)
            Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(dialog.CaptureRenderedFrame())
                .Save(shot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Find<TextBox>(dialog, "SettingsRoots").Text = " C:\\One \r\n\r\n  C:\\Two\r\n";
        Find<TextBox>(dialog, "SettingsExcludes").Text = "node_modules\n  .cache  \n";
        Find<NumericUpDown>(dialog, "SettingsMaxDepth").Value = 5;
        Find<CheckBox>(dialog, "SettingsFollowSymlinks").IsChecked = true;
        Find<CheckBox>(dialog, "SettingsMeasure").IsChecked = true;
        Find<Button>(dialog, "SettingsSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var result = Assert.IsType<StoreConfig>(dialog.Result);
        Assert.Equal(["C:\\One", "C:\\Two"], result.Roots);
        Assert.Equal(["node_modules", ".cache"], result.Excludes);
        Assert.Equal(5, result.MaxDepth);
        Assert.True(result.FollowSymlinks);
        Assert.True(result.MeasureSizes);
        Assert.True(result.Initialised);
    }

    [Fact]
    public void CancelReturnsNothingAndLeavesTheConfigurationUntouched()
    {
        var config = new StoreConfig { Roots = ["C:\\Develop"], MaxDepth = 12 };
        var dialog = Open(config);
        Find<TextBox>(dialog, "SettingsRoots").Text = "C:\\Elsewhere";
        Find<Button>(dialog, "SettingsCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Null(dialog.Result);
        Assert.Equal(["C:\\Develop"], config.Roots);
    }

    [Fact]
    public void SaveIsRefusedWhileTheMaximumDepthIsMissing()
    {
        var dialog = Open(new StoreConfig { Roots = ["C:\\Develop"], MaxDepth = 12 });
        Find<NumericUpDown>(dialog, "SettingsMaxDepth").Value = null;
        Find<Button>(dialog, "SettingsSave").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Null(dialog.Result);
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is { } s && s.Contains("depth", StringComparison.OrdinalIgnoreCase) && t.Name == "SettingsError");
    }

    [Fact]
    public void EndOfLifeLookupIsOffByDefaultAndRoundTrips()
    {
        var dialog = Open(new StoreConfig { Roots = ["/r"], Initialised = true });
        var box = Find<CheckBox>(dialog, "SettingsEndOfLife");
        Assert.False(box.IsChecked);
        box.IsChecked = true;
        Find<Button>(dialog, "SettingsSave").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(dialog.Result!.CheckEndOfLife);
    }
}
