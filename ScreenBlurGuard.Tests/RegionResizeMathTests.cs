using System.Windows;
using ScreenBlurGuard.Overlay;

namespace ScreenBlurGuard.Tests;

/// <summary>
/// Tests the pure resize-anchor math behind the region selection window's drag handles,
/// independent of any WPF window/mouse capture (which isn't practical to unit test).
/// </summary>
public sealed class RegionResizeMathTests
{
    private static readonly Rect Start = new(x: 100, y: 100, width: 200, height: 150); // Right=300, Bottom=250

    [Fact]
    public void SECorner_MovesRightAndBottomEdges_KeepsLeftAndTopFixed()
    {
        var result = RegionSelectionWindow.ComputeResizedRect(Start, RegionSelectionWindow.HandleDirection.SE, new Point(400, 300), minSize: 10);

        Assert.Equal(100, result.Left);
        Assert.Equal(100, result.Top);
        Assert.Equal(400, result.Right);
        Assert.Equal(300, result.Bottom);
    }

    [Fact]
    public void NWCorner_MovesLeftAndTopEdges_KeepsRightAndBottomFixed()
    {
        var result = RegionSelectionWindow.ComputeResizedRect(Start, RegionSelectionWindow.HandleDirection.NW, new Point(50, 60), minSize: 10);

        Assert.Equal(50, result.Left);
        Assert.Equal(60, result.Top);
        Assert.Equal(300, result.Right);
        Assert.Equal(250, result.Bottom);
    }

    // Plain Facts rather than [Theory]/[InlineData] — a public test method can't take a
    // parameter of the internal HandleDirection enum (CS0051), even within the same assembly.
    [Fact]
    public void NHandle_OnlyMovesVerticalEdge_LeavesLeftAndRightUnchanged() =>
        AssertOnlyVerticalEdgeMoves(RegionSelectionWindow.HandleDirection.N);

    [Fact]
    public void SHandle_OnlyMovesVerticalEdge_LeavesLeftAndRightUnchanged() =>
        AssertOnlyVerticalEdgeMoves(RegionSelectionWindow.HandleDirection.S);

    [Fact]
    public void EHandle_OnlyMovesHorizontalEdge_LeavesTopAndBottomUnchanged() =>
        AssertOnlyHorizontalEdgeMoves(RegionSelectionWindow.HandleDirection.E);

    [Fact]
    public void WHandle_OnlyMovesHorizontalEdge_LeavesTopAndBottomUnchanged() =>
        AssertOnlyHorizontalEdgeMoves(RegionSelectionWindow.HandleDirection.W);

    private static void AssertOnlyVerticalEdgeMoves(RegionSelectionWindow.HandleDirection handle)
    {
        var result = RegionSelectionWindow.ComputeResizedRect(Start, handle, new Point(9999, 9999), minSize: 10);

        Assert.Equal(Start.Left, result.Left);
        Assert.Equal(Start.Right, result.Right);
    }

    private static void AssertOnlyHorizontalEdgeMoves(RegionSelectionWindow.HandleDirection handle)
    {
        var result = RegionSelectionWindow.ComputeResizedRect(Start, handle, new Point(9999, 9999), minSize: 10);

        Assert.Equal(Start.Top, result.Top);
        Assert.Equal(Start.Bottom, result.Bottom);
    }

    [Fact]
    public void DraggingPastOppositeEdge_ClampsToMinimumSize_InsteadOfInverting()
    {
        // Dragging the SE handle far up/left, past the NW corner, must not flip the rect inside out.
        var result = RegionSelectionWindow.ComputeResizedRect(Start, RegionSelectionWindow.HandleDirection.SE, new Point(0, 0), minSize: 10);

        Assert.Equal(10, result.Width);
        Assert.Equal(10, result.Height);
        Assert.True(result.Right > result.Left);
        Assert.True(result.Bottom > result.Top);
    }

    [Fact]
    public void EHandle_DraggingPastLeftEdge_ClampsToMinimumSize()
    {
        var result = RegionSelectionWindow.ComputeResizedRect(Start, RegionSelectionWindow.HandleDirection.E, new Point(0, 0), minSize: 10);

        Assert.Equal(10, result.Width);
        Assert.Equal(Start.Left + 10, result.Right);
    }

    [Fact]
    public void ComputeMovedRect_OffsetsPositionBySameDeltaAsMouseMovement_AndKeepsSize()
    {
        var result = RegionSelectionWindow.ComputeMovedRect(Start, startMouseLocal: new Point(50, 50), currentMouseLocal: new Point(70, 35));

        Assert.Equal(Start.Left + 20, result.Left);
        Assert.Equal(Start.Top - 15, result.Top);
        Assert.Equal(Start.Width, result.Width);
        Assert.Equal(Start.Height, result.Height);
    }

    [Fact]
    public void ComputeMovedRect_WhenMouseHasNotMoved_ReturnsSameRect()
    {
        var samePoint = new Point(123, 456);

        var result = RegionSelectionWindow.ComputeMovedRect(Start, samePoint, samePoint);

        Assert.Equal(Start, result);
    }
}
