using ScreenBlurGuard.Native;
using ScreenBlurGuard.Settings;

namespace ScreenBlurGuard.Tests;

public sealed class RectConversionTests
{
    [Fact]
    public void RECT_FromLeftTopWidthHeight_ComputesRightAndBottom()
    {
        var rect = RECT.FromLeftTopWidthHeight(left: 10, top: 20, width: 100, height: 50);

        Assert.Equal(10, rect.Left);
        Assert.Equal(20, rect.Top);
        Assert.Equal(110, rect.Right);
        Assert.Equal(70, rect.Bottom);
        Assert.Equal(100, rect.Width);
        Assert.Equal(50, rect.Height);
    }

    [Fact]
    public void SavedRegion_RoundTripsThroughRect()
    {
        var original = new SavedRegion { Left = 5, Top = 15, Width = 200, Height = 80 };

        var rect = original.ToRect();
        var roundTripped = SavedRegion.FromRect(rect);

        Assert.Equal(original.Left, roundTripped.Left);
        Assert.Equal(original.Top, roundTripped.Top);
        Assert.Equal(original.Width, roundTripped.Width);
        Assert.Equal(original.Height, roundTripped.Height);
    }
}
