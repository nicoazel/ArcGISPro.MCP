namespace ArcGISProMCP.AddIn.Services;

internal readonly record struct RgbaColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

internal static class ColorParser
{
    public static RgbaColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
            return new RgbaColor(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4));
        if (hex.Length == 8)
            return new RgbaColor(Byte(hex, 2), Byte(hex, 4), Byte(hex, 6), Byte(hex, 0));
        throw new FormatException("Color must use #RRGGBB or #AARRGGBB hexadecimal form.");
    }

    private static byte Byte(string value, int offset) => Convert.ToByte(value.Substring(offset, 2), 16);
}
