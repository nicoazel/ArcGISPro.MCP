using ArcGIS.Desktop.Core;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProProjectService : IProjectService
{
    public bool HasEdits => Project.Current?.HasEdits ?? false;

    public bool IsDirty => Project.Current?.IsDirty ?? false;

    public Task OpenAsync(string path) => Project.OpenAsync(path);

    public Task<bool> SaveEditsAsync()
    {
        var project = Project.Current ?? throw new InvalidOperationException("No ArcGIS Pro project is open.");
        return project.SaveEditsAsync();
    }

    public Task SaveAsync()
    {
        var project = Project.Current ?? throw new InvalidOperationException("No ArcGIS Pro project is open.");
        return project.SaveAsync();
    }
}
