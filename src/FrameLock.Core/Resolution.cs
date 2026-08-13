using System.Globalization;

namespace FrameLock.Core;

public readonly record struct Resolution(int Width, int Height)
{
    public const int MinimumDimension = 160;
    public const int MaximumDimension = 16_384;

    public static IReadOnlyList<Resolution> Presets { get; } =
    [
        new(3840, 2160),
        new(2560, 1440),
        new(1920, 1080),
        new(1600, 900),
        new(1280, 720),
        new(1080, 1920),
    ];

    public bool IsValid =>
        Width is >= MinimumDimension and <= MaximumDimension &&
        Height is >= MinimumDimension and <= MaximumDimension;

    public string DisplayName => $"{Width:N0} × {Height:N0}";

    public static bool TryParse(
        string? widthText,
        string? heightText,
        out Resolution resolution,
        out string? error)
    {
        resolution = default;
        error = null;

        if (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var width) ||
            !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var height))
        {
            error = "Enter a whole-number width and height.";
            return false;
        }

        resolution = new Resolution(width, height);
        if (!resolution.IsValid)
        {
            error = $"Width and height must be between {MinimumDimension:N0} and {MaximumDimension:N0} pixels.";
            resolution = default;
            return false;
        }

        return true;
    }

    public override string ToString() => DisplayName;
}
