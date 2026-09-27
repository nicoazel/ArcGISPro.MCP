using ArcGISProMCP.Core.Execution;

namespace ArcGISProMCP.AddIn.Services;

internal static class ArcPyCapabilityState
{
    private static readonly Lazy<ArcPyExecutionSettings?> ConfiguredSettings = new(LoadSettings);

    public static bool Enabled => Settings is not null;

    public static ArcPyExecutionSettings? Settings => ConfiguredSettings.Value;

    private static ArcPyExecutionSettings? LoadSettings()
    {
        return ArcPyExecutionSettings.TryFromEnvironment(out var settings, out _) && settings.Enabled
            ? settings
            : null;
    }
}
