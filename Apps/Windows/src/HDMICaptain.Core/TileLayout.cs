namespace HDMICaptain.Core;

/// <summary>
/// Multi-View grid cells (MultiViewGrid.swift): one column for one card, two up to four, then three;
/// rows fill left to right, and a short last row is centred. Cells are whole pixels with <c>gap</c> between them.
/// </summary>
public static class TileLayout
{
    public static int Columns(int count) => count <= 1 ? 1 : count <= 4 ? 2 : 3;

    public static Rect[] Compute(int count, int width, int height, int gap = 2)
    {
        if (count <= 0 || width <= 0 || height <= 0) return [];
        var columns = Columns(count);
        var rows = (count + columns - 1) / columns;
        var cellWidth = Math.Max(1, (width - gap * (columns - 1)) / columns);
        var cellHeight = Math.Max(1, (height - gap * (rows - 1)) / rows);
        var cells = new Rect[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var inRow = Math.Min(columns, count - row * columns);
            var rowWidth = inRow * cellWidth + (inRow - 1) * gap;
            var left = (width - rowWidth) / 2;
            var column = i % columns;
            cells[i] = new Rect(left + column * (cellWidth + gap), row * (cellHeight + gap), cellWidth, cellHeight);
        }
        return cells;
    }

    /// <summary>The cell under (x, y), or -1 (gaps and empty space).</summary>
    public static int HitTest(Rect[] cells, double x, double y)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            var c = cells[i];
            if (x >= c.X && x < c.X + c.Width && y >= c.Y && y < c.Y + c.Height) return i;
        }
        return -1;
    }
}
