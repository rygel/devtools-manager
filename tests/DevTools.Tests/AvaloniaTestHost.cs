using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace DevTools.Tests;

/// <summary>Avalonia can be set up once per process, and its UI thread is single-threaded.</summary>
[CollectionDefinition("Avalonia")]
public sealed class AvaloniaSerialGroup;

internal static class AvaloniaTestHost
{
    private static readonly object Gate = new();
    private static bool _started;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_started) return;
            AppBuilder.Configure<DevTools.Desktop.App>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            _started = true;
        }
    }
}
