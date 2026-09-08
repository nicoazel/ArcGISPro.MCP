using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProOperationCatalog
{
    public static IReadOnlyList<IOperation> Create(FileResourceStore resources) =>
    [
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
        new BasemapSetOperation(),
        new StyleSearchOperation(),
        new SymbologySetSimpleOperation(),
        new SymbologySetUniqueValuesOperation(),
        new LabelConfigureOperation(),
        new TableQueryOperation(),
        new TableStatisticsOperation(),
        new LayoutListOperation(),
        new LayoutEnsureOperation(),
        new LayoutAddMapFrameOperation(),
        new LayoutSetTextOperation(),
        new LayoutSetFrameExtentOperation(),
        new LayoutEnsureSurroundOperation(),
        new LayoutActivateOperation(),
        new GeoprocessingRunOperation(),
        new ViewCaptureOperation(resources),
        new RhinoPeerStateOperation(),
        new RhinoHandoffOperation(),
        new RhinoStartOperation(),
        new RhinoMcpReconnectOperation(),
        new RhinoPullOperation(),
        new RhinoPreviewOperation(),
        new RhinoSyncOperation()
    ];
}
