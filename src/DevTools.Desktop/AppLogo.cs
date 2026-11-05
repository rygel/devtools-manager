using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DevTools.Desktop;

/// <summary>
/// The application mark, drawn from the same geometry as <c>DevTools.Server/wwwroot/favicon.svg</c>
/// (a 64 by 64 tile with two chevrons and a dot), so the desktop app, taskbar and web UI show one logo.
/// </summary>
public static class AppLogo
{
    private const double Size = 64;

    private static Canvas Draw()
    {
        var canvas = new Canvas { Width = Size, Height = Size };

        canvas.Children.Add(new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Color.Parse("#2c3542")),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#182536"), 0),
                    new GradientStop(Color.Parse("#101419"), 1)
                }
            }
        });
        Canvas.SetLeft(canvas.Children[0], 2);
        Canvas.SetTop(canvas.Children[0], 2);

        foreach (var data in new[] { "M27,18 L14,32 L27,46", "M37,18 L50,32 L37,46" })
        {
            canvas.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Stroke = new SolidColorBrush(Color.Parse("#4f9dff")),
                StrokeThickness = 6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round
            });
        }

        var dot = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.Parse("#3fb950")) };
        Canvas.SetLeft(dot, 29.5);
        Canvas.SetTop(dot, 29.5);
        canvas.Children.Add(dot);
        return canvas;
    }

    /// <summary>The logo scaled to a square of <paramref name="size"/> device-independent pixels.</summary>
    public static Control Create(double size) =>
        new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = Draw() };

    /// <summary>A window icon rendered from the logo, or null when rendering is unavailable.</summary>
    public static WindowIcon? CreateWindowIcon(int pixels = 256)
    {
        try
        {
            var root = new Viewbox { Width = pixels, Height = pixels, Stretch = Stretch.Uniform, Child = Draw() };
            root.Measure(new Size(pixels, pixels));
            root.Arrange(new Rect(0, 0, pixels, pixels));

            var bitmap = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
            bitmap.Render(root);
            return new WindowIcon(bitmap);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}
