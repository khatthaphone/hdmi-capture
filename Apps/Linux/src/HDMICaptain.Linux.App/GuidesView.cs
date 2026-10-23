using HDMICaptain.Core;

namespace HDMICaptain.Linux.App;

/// <summary>
/// Framing guides (thirds, centre cross, 90/80 % safe areas, aspect masks) drawn with cairo over the picture's
/// rectangle (the same <see cref="RenderGeometry"/> the renderer uses). A GTK widget above the GL area, so guides
/// never reach screenshots, recordings or Save Recent (they read pass 1).
/// </summary>
public sealed class GuidesView
{
    private const double CrossArm = 18;
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private GuideSettings _guides = new();
    private (int Width, int Height) _source;
    private ScaleMode _scale = ScaleMode.Fit;

    public GuidesView()
    {
        _area.SetCanTarget(false); // clicks go to the video underneath
        _area.SetDrawFunc((_, cr, w, h) => Draw(cr, w, h));
    }

    public Gtk.Widget Widget => _area;

    public void Update(GuideSettings guides, int sourceWidth, int sourceHeight, ScaleMode scale)
    {
        if (guides == _guides && _source == (sourceWidth, sourceHeight) && scale == _scale) return;
        (_guides, _source, _scale) = (guides, (sourceWidth, sourceHeight), scale);
        _area.SetVisible(!guides.IsEmpty);
        _area.QueueDraw();
    }

    private void Draw(Cairo.Context cr, int width, int height)
    {
        if (_guides.IsEmpty || _source.Width <= 0 || _source.Height <= 0) return;
        var g = RenderGeometry.Compute(_source.Width, _source.Height, width, height, _scale);
        var v = g.Viewport;
        var alpha = Math.Clamp(_guides.Opacity, 0.1, 1);
        cr.LineWidth = 1;

        if (_guides.Mask is { } mask)
        {
            var visible = GuideSettings.MaskRect(mask, v);
            cr.SetSourceRgba(0, 0, 0, 0.85);
            cr.Rectangle(v.X, v.Y, v.Width, visible.Y - v.Y);
            cr.Rectangle(v.X, visible.Y + visible.Height, v.Width, v.Y + v.Height - visible.Y - visible.Height);
            cr.Rectangle(v.X, visible.Y, visible.X - v.X, visible.Height);
            cr.Rectangle(visible.X + visible.Width, visible.Y, v.X + v.Width - visible.X - visible.Width, visible.Height);
            cr.Fill();
        }
        cr.SetSourceRgba(1, 1, 1, alpha);
        if (_guides.Thirds)
        {
            for (var i = 1; i <= 2; i++)
            {
                Line(cr, Snap(v.X + v.Width * i / 3), v.Y, Snap(v.X + v.Width * i / 3), v.Y + v.Height);
                Line(cr, v.X, Snap(v.Y + v.Height * i / 3), v.X + v.Width, Snap(v.Y + v.Height * i / 3));
            }
            cr.Stroke();
        }
        if (_guides.Center)
        {
            var (cx, cy) = (Snap(v.X + v.Width / 2), Snap(v.Y + v.Height / 2));
            Line(cr, cx - CrossArm, cy, cx + CrossArm, cy);
            Line(cr, cx, cy - CrossArm, cx, cy + CrossArm);
            cr.Stroke();
        }
        if (_guides.SafeAreas)
        {
            foreach (var inset in new[] { 0.9, 0.8 })
            {
                var (w, h) = (v.Width * inset, v.Height * inset);
                cr.SetDash(inset < 0.85 ? [4, 3] : [], 0);
                cr.Rectangle(Snap(v.X + (v.Width - w) / 2), Snap(v.Y + (v.Height - h) / 2), Math.Round(w), Math.Round(h));
                cr.Stroke();
            }
            cr.SetDash([], 0);
        }
    }

    private static void Line(Cairo.Context cr, double x0, double y0, double x1, double y1)
    {
        cr.MoveTo(x0, y0);
        cr.LineTo(x1, y1);
    }

    // Half-pixel positions keep 1 px lines crisp.
    private static double Snap(double v) => Math.Floor(v) + 0.5;
}
