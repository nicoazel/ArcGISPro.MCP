using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class ProjectGetOperation() : ProOperationBase(OperationDescriptor.Create(
    "project.get", "Get project",
    "Returns the current ArcGIS Pro project identity, path, dirty state, and revision.",
    JsonSchemas.EmptyObject,
    tags: ["project", "workspace", "state"], aliases: ["current project", "workspace state"],
    related: ["project.open", "project.save", "map.list"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { snapshot.Project, snapshot.Revision, snapshot.CapturedAt }), snapshot.Revision);
    }
}

internal sealed class ProjectOpenOperation() : ProOperationBase(OperationDescriptor.Create(
    "project.open", "Open project",
    "Opens an existing ArcGIS Pro .aprx project. Unsaved changes are handled by ArcGIS Pro's normal project lifecycle.",
    JsonSchemas.ObjectSchema("\"path\": {\"type\": \"string\", \"minLength\": 1}", "path"),
    risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.ArcGISUiThread,
    tags: ["project", "workspace", "open"], aliases: ["open aprx", "switch project"],
    related: ["project.get", "map.list"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(RequiredString(arguments, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("ArcGIS Pro project was not found.", path);
        if (!string.Equals(Path.GetExtension(path), ".aprx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Project path must reference an .aprx file.", nameof(arguments));

        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            await Project.OpenAsync(path).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { opened = true, path, snapshot.Project.Name }), snapshot.Revision);
    }
}

internal sealed class ProjectSaveOperation() : ProOperationBase(OperationDescriptor.Create(
    "project.save", "Save project",
    "Saves the current ArcGIS Pro project.",
    JsonSchemas.EmptyObject,
    risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.ArcGISUiThread,
    tags: ["project", "workspace", "save"], aliases: ["save aprx", "persist project"],
    related: ["project.get"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            var project = Project.Current ?? throw new InvalidOperationException("No ArcGIS Pro project is open.");
            await project.SaveAsync().ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { saved = true, snapshot.Project.Name }), snapshot.Revision);
    }
}
