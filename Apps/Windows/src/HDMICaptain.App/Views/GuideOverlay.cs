using HDMICaptain.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HDMICaptain.App.Views;

/// <summary>
/// Framing guides (GuideSettings: thirds, centre cross, 90/80 % safe areas, aspect masks) drawn as XAML shapes
/// over the video rectangle. They live above the swap chain, never in the source texture, so screenshots,
/// recordings and streams never contain them (CLAUDE.md conventions).
/// </summary>
public static class GuideOverlay
{
    private const double CrossArm = 18;

    /// <param name="video">The video's rectangle inside <paramref name="canvas"/>, in DIPs.</param>
    public static void Draw(Canvas canvas, GuideSettings guides, Rect video)
    {
        canvas.Children.Clear();
        if (guides.IsEmpty || video.Width <= 0 || video.Height <= 0) return;
        var stroke = new SolidColorBrush(Colors.White) { Opacity = Math.Clamp(guides.Opacity, 0.1, 1) };

        if (guides.Mask is { } mask)
        {
            var visible = GuideSettings.MaskRect(mask, video);
            var shade = new SolidColorBrush(Colors.Black) { Opacity = 0.85 };
            AddRect(canvas, video.X, video.Y, video.Width, visible.Y - video.Y, shade);
            AddRect(canvas, video.X, visible.Y + visible.Height, video.Width, video.Y + video.Height - visible.Y - visible.Height, shade);
            AddRect(canvas, video.X, visible.Y, visible.X - video.X, visible.Height, shade);
            AddRect(canvas, visible.X + visible.Width, visible.Y, video.X + video.Width - visible.X - visible.Width, visible.Height, shade);
        }
        if (guides.Thirds)
        {
            for (var i = 1; i <= 2; i++)
            {
                var x = video.X + video.Width * i / 3;
                var y = video.Y + video.Height * i / 3;
                AddLine(canvas, x, video.Y, x, video.Y + video.Height, stroke);
                AddLine(canvas, video.X, y, video.X + video.Width, y, stroke);
            }
        }
        if (guides.Center)
        {
            var cx = video.X + video.Width / 2;
            var cy = video.Y + video.Height / 2;
            AddLine(canvas, cx - CrossArm, cy, cx + CrossArm, cy, stroke);
            AddLine(canvas, cx, cy - CrossArm, cx, cy + CrossArm, stroke);
        }
        if (guides.SafeAreas)
        {
            foreach (var inset in new[] { 0.9, 0.8 })
            {
                var w = video.Width * inset;
                var h = video.Height * inset;
                var outline = new Rectangle { Width = w, Height = h, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = inset < 0.85 ? [4, 3] : null };
                Canvas.SetLeft(outline, video.X + (video.Width - w) / 2);
                Canvas.SetTop(outline, video.Y + (video.Height - h) / 2);
                canvas.Children.Add(outline);
            }
        }
    }

    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush stroke) =>
        canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1 });

    private static void AddRect(Canvas canvas, double x, double y, double w, double h, Brush fill)
    {
        if (w <= 0 || h <= 0) return;
        var rect = new Rectangle { Width = w, Height = h, Fill = fill };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }
}
