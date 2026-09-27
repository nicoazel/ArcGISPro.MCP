using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal static class ProOperationCatalog
{
    public static IReadOnlyList<IOperation> Create(FileResourceStore resources)
    {
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
        new GeoprocessingRunOperation(),
            new ViewCaptureOperation(resources)
        };

        if (ArcPyCapabilityState.Settings is { } arcPySettings)
        {
            operations.Add(new ArcPyInspectScriptOperation(arcPySettings));
            operations.Add(new ArcPyRunScriptOperation(arcPySettings));
        }

        return operations;
    }
}
