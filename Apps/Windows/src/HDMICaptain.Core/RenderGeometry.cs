namespace HDMICaptain.Core;

public readonly record struct Rect(double X, double Y, double Width, double Height);

/// <summary>
/// Port of CaptureCore's RenderGeometry (and the Kotlin one): where the picture goes in the drawable
/// (pixels, top-left origin) and which part of the source is visible.
/// Shared fixture: docs/fixtures/render-geometry.json.
/// </summary>
public readonly record struct RenderGeometry(Rect Viewport, Rect SourceRect)
{
    public static RenderGeometry Compute(double sourceWidth, double sourceHeight, double width, double height, ScaleMode mode)
    {
        var full = new Rect(0, 0, width, height);
        var unit = new Rect(0, 0, 1, 1);
        if (sourceWidth <= 0 || sourceHeight <= 0 || width <= 0 || height <= 0 || mode == ScaleMode.Stretch)
            return new RenderGeometry(full, unit);

        var sx = width / sourceWidth;
        var sy = height / sourceHeight;
        var scale = mode switch
        {
            ScaleMode.Fill => Math.Max(sx, sy),
            ScaleMode.Integer => Math.Floor(Math.Min(sx, sy)) is var s and >= 1 ? s : Math.Min(sx, sy),
            _ => Math.Min(sx, sy),
        };
        // Whole pixels, so float error can't push the origin to -1 and crop a column.
        var w = Math.Round(sourceWidth * scale, MidpointRounding.AwayFromZero);
        var h = Math.Round(sourceHeight * scale, MidpointRounding.AwayFromZero);
        var rect = new Rect(Math.Floor((width - w) / 2), Math.Floor((height - h) / 2), w, h);
        var x0 = Math.Max(rect.X, 0);
        var y0 = Math.Max(rect.Y, 0);
        var x1 = Math.Min(rect.X + w, width);
        var y1 = Math.Min(rect.Y + h, height);
        return new RenderGeometry(
            new Rect(x0, y0, x1 - x0, y1 - y0),
            new Rect((x0 - rect.X) / w, (y0 - rect.Y) / h, (x1 - x0) / w, (y1 - y0) / h));
    }
}
