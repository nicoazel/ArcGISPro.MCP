namespace ArcGISProMCP.Core.Geoprocessing;

/// <summary>
/// A small search prior for the general-purpose system toolboxes. ArcGIS Pro ships about 2,200 tools,
/// most of them in specialist toolboxes (Location Referencing, Business Analyst, Network Analyst, Raster
/// Analysis, Aviation, ...) whose long summaries reuse everyday words such as "feature class", "overlap"
/// or "nearest". A person who asks in general terms almost always means the core tool, so the core
/// toolboxes get a modest multiplier. It only reorders comparable matches: a specialist tool that
/// matches the query clearly better still ranks first, and nothing is filtered out.
/// </summary>
public static class GpToolboxPriority
{
    /// <summary>
    /// Toolboxes available at every license level and used for everyday data work: Analysis, Data
    /// Management, Conversion, Cartography, Editing and Spatial Statistics.
    /// </summary>
    public const double CoreFactor = 1.25;

    /// <summary>The two common analysis extensions: Spatial Analyst and 3D Analyst.</summary>
    public const double CommonExtensionFactor = 1.1;

    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "analysis", "management", "conversion", "cartography", "edit", "stats"
    };

    private static readonly HashSet<string> CommonExtensions = new(StringComparer.OrdinalIgnoreCase) { "sa", "3d" };

    /// <summary>The multiplier for <paramref name="tool"/>'s toolbox; 1 for user and specialist toolboxes.</summary>
    public static double Factor(GpToolSummary tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!tool.IsSystem) return 1;
        if (Core.Contains(tool.ToolboxAlias)) return CoreFactor;
        return CommonExtensions.Contains(tool.ToolboxAlias) ? CommonExtensionFactor : 1;
    }
}
