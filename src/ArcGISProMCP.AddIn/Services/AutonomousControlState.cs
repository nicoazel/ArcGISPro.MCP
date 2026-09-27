namespace ArcGISProMCP.AddIn.Services;

internal static class AutonomousControlState
{
    public static bool Enabled { get; } = IsEnabled(
        Environment.GetEnvironmentVariable("ARCGIS_PRO_MCP_AUTONOMOUS_MODE"));

    private static bool IsEnabled(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal) ||
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
