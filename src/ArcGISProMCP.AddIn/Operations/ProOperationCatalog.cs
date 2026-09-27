using System.Diagnostics;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProOperationCatalog
{
    public static IReadOnlyList<IOperation> Create(ProResourceStore resources)
    {
        // One toolbox catalog shared by every gp.* operation and the approval-card warnings.
        var toolboxes = ToolboxCatalog.Default;
        WarmUp(toolboxes);
        var operations = new List<IOperation>
        {
        new ProjectGetOperation(),
        new ProjectOpenOperation(),
        new ProjectSaveOperation(),
        new MapListOperation(),
        new MapEnsureOperation(),
        new MapActivateOperation(),
        new MapClearSelectionOperation(),
        new LayerListOperation(),
        new LayerAddOperation(),
        new LayerSetAppearanceOperation(),
        new LayerSetElevationOperation(),
        new BasemapSetOperation(),
        new StyleSearchOperation(),
        new SymbologySetSimpleOperation(),
        new SymbologySetUniqueValuesOperation(),
        new LabelConfigureOperation(),
        new TableQueryOperation(),
        new TableStatisticsOperation(),
        new FeatureLayerDescribeOperation(),
        new FeatureQueryOperation(),
        new FeatureSelectOperation(),
        new FeatureCreateOperation(),
        new FeatureUpdateOperation(),
        new FeatureDeleteOperation(),
        new MetadataGetOperation(),
        new MetadataUpdateOperation(),
        new LayoutListOperation(),
        new LayoutInspectOperation(),
        new LayoutEnsureOperation(),
        new LayoutAddMapFrameOperation(),
        new LayoutSetTextOperation(),
        new LayoutSetFrameExtentOperation(),
        new LayoutEnsureSurroundOperation(),
        new LayoutActivateOperation(),
        new GeoprocessingSearchOperation(toolboxes),
        new GeoprocessingDescribeOperation(toolboxes),
        new GeoprocessingQueryOperation(toolboxes),
        new GeoprocessingRunOperation(toolboxes),
            new ViewCaptureOperation(resources)
        };

        if (ArcPyCapabilityState.Settings is { } arcPySettings)
        {
            operations.Add(new ArcPyInspectScriptOperation(arcPySettings));
            operations.Add(new ArcPyRunScriptOperation(arcPySettings));
        }

        return operations;
    }

    /// <summary>Builds the toolbox index in the background so the first gp.* call or approval card does not pay for it.</summary>
    private static void WarmUp(ToolboxCatalog toolboxes) =>
        _ = Task.Run(() =>
        {
            try
            {
                _ = toolboxes.ToolCount;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Toolbox catalog warm-up failed: {0}", exception);
            }
        });
}
