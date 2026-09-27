using ArcGISProMCP.Core.Geoprocessing;

namespace ArcGISProMCP.Core.Tests;

public sealed class GpToolboxPriorityTests
{
    [Theory]
    [InlineData("analysis", true, GpToolboxPriority.CoreFactor)]
    [InlineData("management", true, GpToolboxPriority.CoreFactor)]
    [InlineData("sa", true, GpToolboxPriority.CommonExtensionFactor)]
    [InlineData("locref", true, 1d)]
    [InlineData("ba", true, 1d)]
    [InlineData("management", false, 1d)]
    public void Core_system_toolboxes_get_a_modest_boost(string alias, bool isSystem, double expected)
    {
        var tool = new GpToolSummary($"{alias}.Tool", "Tool", "Tool", null, "Toolbox", alias, null, "Function",
            GpRiskTier.Standard, false, isSystem);

        Assert.Equal(expected, GpToolboxPriority.Factor(tool));
        Assert.InRange(GpToolboxPriority.CoreFactor, 1, 1.5);
    }
}
