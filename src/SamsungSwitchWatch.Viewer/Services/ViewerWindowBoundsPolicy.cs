namespace SamsungSwitchWatch.Viewer.Services;

internal readonly record struct ViewerWindowSize(double Width, double Height);
internal readonly record struct ViewerWindowPosition(double Left, double Top);

internal static class ViewerWindowBoundsPolicy
{
    internal const double WorkAreaMargin = 16;

    public static ViewerWindowSize FitMainWindow(
        double desiredWidth,
        double desiredHeight,
        double workingAreaWidth,
        double workingAreaHeight)
    {
        var width = FitDimension(
            desiredWidth,
            workingAreaWidth,
            fallback: 1440);
        var height = FitDimension(
            desiredHeight,
            workingAreaHeight,
            fallback: 900);
        return new ViewerWindowSize(width, height);
    }

    public static ViewerWindowPosition ClampMainWindowPosition(
        double desiredLeft,
        double desiredTop,
        ViewerWindowSize window,
        double workingAreaLeft,
        double workingAreaTop,
        double workingAreaWidth,
        double workingAreaHeight) =>
        new(
            ClampCoordinate(
                desiredLeft,
                window.Width,
                workingAreaLeft,
                workingAreaWidth),
            ClampCoordinate(
                desiredTop,
                window.Height,
                workingAreaTop,
                workingAreaHeight));

    public static bool IsPositionInWorkArea(
        double left,
        double top,
        double workingAreaLeft,
        double workingAreaTop,
        double workingAreaWidth,
        double workingAreaHeight) =>
        IsFinite(left)
        && IsFinite(top)
        && IsFinite(workingAreaLeft)
        && IsFinite(workingAreaTop)
        && IsFinitePositive(workingAreaWidth)
        && IsFinitePositive(workingAreaHeight)
        && left >= workingAreaLeft
        && left < workingAreaLeft + workingAreaWidth
        && top >= workingAreaTop
        && top < workingAreaTop + workingAreaHeight;

    private static double FitDimension(
        double desired,
        double workingArea,
        double fallback)
    {
        var safeDesired = IsFinitePositive(desired) ? desired : fallback;
        if (!IsFinitePositive(workingArea)) return safeDesired;

        // WPF reports SystemParameters.WorkArea in device-independent units,
        // so this also covers Windows display scaling without guessing DPI.
        var available = Math.Max(1, workingArea - WorkAreaMargin);
        return Math.Min(safeDesired, available);
    }

    private static double ClampCoordinate(
        double desired,
        double windowSize,
        double workingAreaOrigin,
        double workingAreaSize)
    {
        if (!IsFinite(desired)
            || !IsFinite(workingAreaOrigin)
            || !IsFinitePositive(windowSize)
            || !IsFinitePositive(workingAreaSize))
        {
            return desired;
        }

        var maximum = workingAreaOrigin + Math.Max(0, workingAreaSize - windowSize);
        return Math.Clamp(desired, workingAreaOrigin, maximum);
    }

    private static bool IsFinitePositive(double value) =>
        IsFinite(value) && value > 0;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
