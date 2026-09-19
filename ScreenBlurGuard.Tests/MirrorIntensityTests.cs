using ScreenBlurGuard.Overlay;

namespace ScreenBlurGuard.Tests;

/// <summary>
/// Tests the pure 0–100 "농도"(intensity) → real-unit mapping behind the slider, independent
/// of any WPF window or actual image rendering.
/// </summary>
public sealed class MirrorIntensityTests
{
    [Fact]
    public void ComputeBlurRadius_AtZero_ReturnsMinimum()
    {
        Assert.Equal(MirrorPreviewWindow.MinBlurRadius, MirrorPreviewWindow.ComputeBlurRadius(0));
    }

    [Fact]
    public void ComputeBlurRadius_AtHundred_ReturnsMaximum()
    {
        Assert.Equal(MirrorPreviewWindow.MaxBlurRadius, MirrorPreviewWindow.ComputeBlurRadius(100));
    }

    [Fact]
    public void ComputeBlurRadius_AtFifty_ReturnsMidpoint()
    {
        double expected = (MirrorPreviewWindow.MinBlurRadius + MirrorPreviewWindow.MaxBlurRadius) / 2;
        Assert.Equal(expected, MirrorPreviewWindow.ComputeBlurRadius(50), precision: 6);
    }

    [Fact]
    public void ComputeBlurRadius_ClampsOutOfRangeInput()
    {
        Assert.Equal(MirrorPreviewWindow.MinBlurRadius, MirrorPreviewWindow.ComputeBlurRadius(-50));
        Assert.Equal(MirrorPreviewWindow.MaxBlurRadius, MirrorPreviewWindow.ComputeBlurRadius(500));
    }

    [Fact]
    public void ComputeMosaicBlockPixels_AtZero_ReturnsMinimum()
    {
        Assert.Equal(MirrorPreviewWindow.MinMosaicBlockPixels, MirrorPreviewWindow.ComputeMosaicBlockPixels(0));
    }

    [Fact]
    public void ComputeMosaicBlockPixels_AtHundred_ReturnsMaximum()
    {
        Assert.Equal(MirrorPreviewWindow.MaxMosaicBlockPixels, MirrorPreviewWindow.ComputeMosaicBlockPixels(100));
    }

    [Fact]
    public void ComputeMosaicBlockPixels_IsMonotonicallyIncreasing()
    {
        int previous = MirrorPreviewWindow.ComputeMosaicBlockPixels(0);
        for (int percent = 10; percent <= 100; percent += 10)
        {
            int current = MirrorPreviewWindow.ComputeMosaicBlockPixels(percent);
            Assert.True(current >= previous, $"Block size decreased going from a lower to a higher intensity ({percent}%).");
            previous = current;
        }
    }
}
