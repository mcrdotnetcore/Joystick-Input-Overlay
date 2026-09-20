namespace WinWingOverlay;

/// <summary>
/// A percentage range that paints the collective reading a chosen colour. Ranges are
/// inclusive at both ends, and the first band containing a value wins.
/// </summary>
internal sealed class ColourBand
{
    public int Min { get; set; }
    public int Max { get; set; }
    public string Colour { get; set; } = "Red";
}

/// <summary>The four selectable colours.</summary>
internal static class BandColours
{
    public const int Max = 6;

    public static readonly string[] Names = { "White", "Green", "Yellow", "Red" };

    /// <summary>The colour for a name, or <see cref="Color.Empty"/> if it is not one of the four.</summary>
    public static Color Value(string? name) => (name ?? "").Trim().ToUpperInvariant() switch
    {
        "WHITE" => Color.FromArgb(245, 247, 250),
        "GREEN" => Color.FromArgb(78, 219, 118),
        "YELLOW" => Color.FromArgb(243, 201, 74),
        "RED" => Color.FromArgb(235, 82, 82),
        _ => Color.Empty
    };

    public static bool IsName(string? candidate, string name) =>
        string.Equals(candidate?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cycles White, Green, Yellow, Red. Anything unrecognised starts at White.</summary>
    public static string Next(string? current)
    {
        int index = Array.FindIndex(Names, n => IsName(current, n));
        return Names[(index + 1) % Names.Length];
    }
}
