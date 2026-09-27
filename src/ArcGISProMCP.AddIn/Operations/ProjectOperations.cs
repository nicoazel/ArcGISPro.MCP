using System.IO;
using System.Text.Json;
using ArcGIS.Desktop.Core;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class ProjectGetOperation() : ProOperationBase(OperationDescriptor.Create(
    "project.get", "Get project",
    "Returns the current ArcGIS Pro project identity, path, dirty state, and revision.",
    JsonSchemas.EmptyObject,
    outputSchema: ProjectOperationSchemas.GetOutput,
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
    "Opens an existing ArcGIS Pro .aprx project, replacing the current project. Requires local approval because it discards the current session context. Unsaved changes are handled by ArcGIS Pro's normal project lifecycle.",
    ProjectOperationSchemas.OpenInput,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, executionTarget: ExecutionTarget.ArcGISUiThread,
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
    "Saves the current ArcGIS Pro project to disk. Requires local approval because it persists every pending change in the .aprx.",
    JsonSchemas.EmptyObject,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, executionTarget: ExecutionTarget.ArcGISUiThread,
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
        // ArcGIS can complete SaveAsync before its observable IsDirty transition reaches the
        // workspace snapshot. Publishing that transient revision makes the very next workflow
        // step fail optimistic concurrency even though no competing client changed the project.
        // Wait only for the documented saved state; a genuine later edit will still advance the
        // revision and be rejected by the next write.
        for (var attempt = 0; snapshot.Project.IsDirty && attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        if (snapshot.Project.IsDirty)
            throw new InvalidOperationException("ArcGIS Pro did not reach a clean project state after SaveAsync.");
        return OperationResult.Ok(Json(new { saved = true, snapshot.Project.Name }), snapshot.Revision);
    }
}
