using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerWindowBoundsPolicyTests
{
    [Fact]
    public void FitMainWindow_KeepsPreferredSizeWhenWorkAreaIsLargeEnough()
    {
        var fitted = ViewerWindowBoundsPolicy.FitMainWindow(1440, 900, 1920, 1040);

        Assert.Equal(1440, fitted.Width);
        Assert.Equal(900, fitted.Height);
    }

    [Theory]
    [InlineData(1092.8, 614.4)] // 1366 x 768 at 125 percent scaling
    [InlineData(910.7, 512.0)]  // 1366 x 768 at 150 percent scaling
    public void FitMainWindow_ConstrainsSavedSizeToScaledWorkArea(
        double workingWidth,
        double workingHeight)
    {
        var fitted = ViewerWindowBoundsPolicy.FitMainWindow(
            1440,
            900,
            workingWidth,
            workingHeight);

        Assert.Equal(workingWidth - ViewerWindowBoundsPolicy.WorkAreaMargin, fitted.Width, 3);
        Assert.Equal(workingHeight - ViewerWindowBoundsPolicy.WorkAreaMargin, fitted.Height, 3);
    }

    [Fact]
    public void FitMainWindow_InvalidSavedOrWorkAreaValuesRemainFinite()
    {
        var fitted = ViewerWindowBoundsPolicy.FitMainWindow(
            double.NaN,
            double.PositiveInfinity,
            0,
            double.NaN);

        Assert.Equal(1440, fitted.Width);
        Assert.Equal(900, fitted.Height);
    }

    [Fact]
    public void ClampMainWindowPosition_PreventsFittedWindowFromExtendingPastWorkArea()
    {
        var fitted = ViewerWindowBoundsPolicy.FitMainWindow(
            1440,
            900,
            1092.8,
            614.4);

        var position = ViewerWindowBoundsPolicy.ClampMainWindowPosition(
            900,
            500,
            fitted,
            0,
            0,
            1092.8,
            614.4);

        Assert.Equal(16, position.Left, 3);
        Assert.Equal(16, position.Top, 3);
        Assert.True(position.Left + fitted.Width <= 1092.8);
        Assert.True(position.Top + fitted.Height <= 614.4);
    }

    [Fact]
    public void ClampMainWindowPosition_PreservesValidNegativeMonitorCoordinate()
    {
        var position = ViewerWindowBoundsPolicy.ClampMainWindowPosition(
            -1500,
            100,
            new ViewerWindowSize(1200, 800),
            -1920,
            0,
            1920,
            1040);

        Assert.Equal(-1500, position.Left);
        Assert.Equal(100, position.Top);
    }

    [Fact]
    public void IsPositionInWorkArea_DistinguishesSecondaryNegativeCoordinate()
    {
        Assert.True(ViewerWindowBoundsPolicy.IsPositionInWorkArea(
            200,
            100,
            0,
            0,
            1920,
            1040));
        Assert.False(ViewerWindowBoundsPolicy.IsPositionInWorkArea(
            -1500,
            100,
            0,
            0,
            1920,
            1040));
    }
}
