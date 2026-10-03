using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

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

internal sealed class ProjectOpenOperation(IProjectService project) : ProOperationBase(OperationDescriptor.Create(
    "project.open", "Open project",
    "Opens an existing ArcGIS Pro .aprx project, replacing the current project. Requires local approval because it discards the current session context. Refuses while the current project has unsaved feature edits; save them first with project.save.",
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
            // With pending feature edits ArcGIS Pro asks "Save all edits?" in a modal dialog before
            // closing the current project, and the call blocks until a person answers. Refuse
            // instead, checked on the same UI turn that opens the project. The project dirty flag is
            // not checked: it is set even on an untouched, freshly opened project, and live runs
            // never observed a prompt for project changes alone.
            if (project.HasEdits) throw PendingEdits();
            await project.OpenAsync(path).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { opened = true, path, snapshot.Project.Name }), snapshot.Revision);
    }

    internal static OperationException PendingEdits() =>
        new(OperationErrorCodes.PendingEdits,
            "The current project has unsaved feature edits, so ArcGIS Pro would stop to ask whether to save them. " +
            "Nothing was opened. Save the edits with an approved project.save (it saves pending edits and the project), " +
            "or save or discard them in ArcGIS Pro, then request a new approval for project.open.");
}

internal sealed class ProjectSaveOperation(IProjectService project) : ProOperationBase(OperationDescriptor.Create(
    "project.save", "Save project",
    "Saves the current ArcGIS Pro project to disk, first saving any pending feature edits. Requires local approval because it persists every pending change: unsaved data edits and the .aprx.",
    JsonSchemas.EmptyObject,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, executionTarget: ExecutionTarget.ArcGISUiThread,
    tags: ["project", "workspace", "save"], aliases: ["save aprx", "persist project"],
    related: ["project.get"]))
{
    /// <summary>Snapshots re-sampled while waiting for the saved (clean) project state.</summary>
    internal const int CleanStateAttempts = 40;

    /// <summary>Delay between those samples.</summary>
    internal static readonly TimeSpan CleanStateInterval = TimeSpan.FromMilliseconds(50);

    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var editsSaved = await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            // Project.SaveAsync writes only the .aprx; feature edits stay pending in the edit
            // session (and make a later project.open prompt) until they are saved separately.
            // Save them first so a failed edit save leaves the project file untouched.
            var savedEdits = false;
            if (project.HasEdits)
            {
                if (!await project.SaveEditsAsync().ConfigureAwait(true))
                    throw new OperationException(OperationErrorCodes.EditsNotSaved,
                        "ArcGIS Pro could not save the pending feature edits, so the project was not saved either. " +
                        "Check the edited layers in ArcGIS Pro (for example a locked or read-only data source) and retry.");
                savedEdits = true;
            }
            await project.SaveAsync().ConfigureAwait(true);
            return savedEdits;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        // ArcGIS can complete SaveAsync before its observable IsDirty transition reaches the
        // workspace snapshot. Publishing that transient revision makes the very next workflow
        // step fail optimistic concurrency even though no competing client changed the project.
        // Wait only for the documented saved state; a genuine later edit will still advance the
        // revision and be rejected by the next write.
        for (var attempt = 0; snapshot.Project.IsDirty && attempt < CleanStateAttempts; attempt++)
        {
            await Task.Delay(CleanStateInterval, cancellationToken).ConfigureAwait(false);
            snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        if (snapshot.Project.IsDirty)
            throw new InvalidOperationException("ArcGIS Pro did not reach a clean project state after SaveAsync.");
        return OperationResult.Ok(Json(new { saved = true, editsSaved, snapshot.Project.Name }), snapshot.Revision);
    }
}
