using ArcGIS.Desktop.Core;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProProjectService : IProjectService
{
    public Task OpenAsync(string path) => Project.OpenAsync(path);

    public Task SaveAsync()
    {
        var project = Project.Current ?? throw new InvalidOperationException("No ArcGIS Pro project is open.");
        return project.SaveAsync();
    }
}
