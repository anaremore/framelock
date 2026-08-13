namespace FrameLock.Core;

public static class WindowSizingMath
{
    public static Resolution EstimateOuterSize(
        Resolution desiredClient,
        Resolution currentClient,
        Resolution currentOuter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desiredClient.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desiredClient.Height);

        var horizontalFrame = Math.Max(0, currentOuter.Width - currentClient.Width);
        var verticalFrame = Math.Max(0, currentOuter.Height - currentClient.Height);
        return new Resolution(
            checked(desiredClient.Width + horizontalFrame),
            checked(desiredClient.Height + verticalFrame));
    }

    public static Resolution CorrectOuterSize(
        Resolution desiredClient,
        Resolution measuredClient,
        Resolution measuredOuter)
    {
        var width = checked(measuredOuter.Width + desiredClient.Width - measuredClient.Width);
        var height = checked(measuredOuter.Height + desiredClient.Height - measuredClient.Height);
        return new Resolution(Math.Max(1, width), Math.Max(1, height));
    }
}
