using System.Diagnostics;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProOperationCatalog
{
    public static IReadOnlyList<IOperation> Create(ProResourceStore resources, ArcGisServices services)
    {
        // One toolbox catalog shared by every gp.* operation and the approval-card warnings.
        var toolboxes = ToolboxCatalog.Default;
        WarmUp(toolboxes);
        var operations = new List<IOperation>
        {
        new ProjectGetOperation(),
        new ProjectOpenOperation(services.Project),
        new ProjectSaveOperation(services.Project),
        new MapListOperation(services.Maps),
        new MapEnsureOperation(services.Maps),
        new MapActivateOperation(services.Maps),
        new MapClearSelectionOperation(services.Maps),
        new LayerListOperation(services.Maps, services.Layers),
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
        new FeatureLayerDescribeOperation(services.Features),
        new FeatureQueryOperation(services.Features),
        new FeatureSelectOperation(services.Features),
        new FeatureCreateOperation(services.Features),
        new FeatureUpdateOperation(services.Features),
        new FeatureDeleteOperation(services.Features),
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
            new ViewCaptureOperation(resources, services.Views)
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
