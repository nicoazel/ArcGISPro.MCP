using ArcGISProMCP.Core.Resources;

namespace ArcGISProMCP.AddIn.Services;

/// <summary>
/// ArcGIS Pro host facade for the portable Core resource store.
/// </summary>
internal sealed class FileResourceStore : ArcGISProMCP.Core.Resources.FileResourceStore
{
    public FileResourceStore(string root, ResourceRetentionOptions? retention = null)
        : base(root, retention ?? new ResourceRetentionOptions(TimeToLive: TimeSpan.FromHours(24)))
    {
    }
}
