namespace ProjectFileHub.Core.Services;

public readonly record struct PreviewZoomView(
    double HorizontalOffset,
    double VerticalOffset,
    float ZoomFactor);

public static class PreviewZoomMath
{
    public static PreviewZoomView CalculateCenteredView(
        double horizontalOffset,
        double verticalOffset,
        double viewportWidth,
        double viewportHeight,
        double contentWidth,
        double contentHeight,
        float currentZoomFactor,
        float requestedZoomFactor,
        float minimumZoomFactor,
        float maximumZoomFactor)
    {
        var currentZoom = Math.Max(currentZoomFactor, float.Epsilon);
        var targetZoom = Math.Clamp(requestedZoomFactor, minimumZoomFactor, maximumZoomFactor);
        var safeViewportWidth = Math.Max(0, viewportWidth);
        var safeViewportHeight = Math.Max(0, viewportHeight);
        var safeContentWidth = Math.Max(0, contentWidth);
        var safeContentHeight = Math.Max(0, contentHeight);

        var centerX = safeContentWidth * currentZoom <= safeViewportWidth
            ? safeContentWidth / 2
            : (Math.Max(0, horizontalOffset) + (safeViewportWidth / 2)) / currentZoom;
        var centerY = safeContentHeight * currentZoom <= safeViewportHeight
            ? safeContentHeight / 2
            : (Math.Max(0, verticalOffset) + (safeViewportHeight / 2)) / currentZoom;
        var maximumHorizontalOffset = Math.Max(0, (safeContentWidth * targetZoom) - safeViewportWidth);
        var maximumVerticalOffset = Math.Max(0, (safeContentHeight * targetZoom) - safeViewportHeight);
        var targetHorizontalOffset = Math.Clamp(
            (centerX * targetZoom) - (safeViewportWidth / 2),
            0,
            maximumHorizontalOffset);
        var targetVerticalOffset = Math.Clamp(
            (centerY * targetZoom) - (safeViewportHeight / 2),
            0,
            maximumVerticalOffset);

        return new PreviewZoomView(targetHorizontalOffset, targetVerticalOffset, targetZoom);
    }

    public static double CalculatePanOffset(double startOffset, double pointerDelta, double maximumOffset) =>
        Math.Clamp(startOffset - pointerDelta, 0, Math.Max(0, maximumOffset));
}
