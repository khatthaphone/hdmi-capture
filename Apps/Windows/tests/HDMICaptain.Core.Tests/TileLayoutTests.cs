using HDMICaptain.Core;

namespace HDMICaptain.Core.Tests;

public class TileLayoutTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    public void ColumnsMatchTheMacGrid(int count, int columns) => Assert.Equal(columns, TileLayout.Columns(count));

    [Fact]
    public void OneCardFillsTheWindow() =>
        Assert.Equal([new Rect(0, 0, 1280, 720)], TileLayout.Compute(1, 1280, 720));

    [Fact]
    public void TwoCardsSitSideBySideWithAGap()
    {
        var cells = TileLayout.Compute(2, 1282, 720);

        Assert.Equal(new Rect(0, 0, 640, 720), cells[0]);
        Assert.Equal(new Rect(642, 0, 640, 720), cells[1]);
    }

    [Fact]
    public void AShortLastRowIsCentred()
    {
        var cells = TileLayout.Compute(3, 1282, 722);

        Assert.Equal(new Rect(0, 0, 640, 360), cells[0]);
        Assert.Equal(new Rect(642, 0, 640, 360), cells[1]);
        Assert.Equal(new Rect(321, 362, 640, 360), cells[2]);
    }

    [Fact]
    public void HitTestFindsTheCellAndMissesTheGap()
    {
        var cells = TileLayout.Compute(2, 1282, 720);

        Assert.Equal(0, TileLayout.HitTest(cells, 10, 10));
        Assert.Equal(-1, TileLayout.HitTest(cells, 641, 10));
        Assert.Equal(1, TileLayout.HitTest(cells, 700, 700));
    }

    [Fact]
    public void NothingToLayOut() => Assert.Empty(TileLayout.Compute(0, 100, 100));
}
