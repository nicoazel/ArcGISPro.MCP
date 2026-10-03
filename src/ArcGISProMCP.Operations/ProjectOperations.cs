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
    "Opens an existing ArcGIS Pro .aprx project, replacing the current project. Requires local approval because it discards the current session context. Refuses while the current project has unsaved feature edits or project changes (ArcGIS Pro marks a project changed right after opening it); save them first with project.save.",
    ProjectOperationSchemas.OpenInput,
    risk: OperationRisk.SafeWrite, requiresConfirmation: true, executionTarget: ExecutionTarget.ArcGISUiThread,
    tags: ["project", "workspace", "open"], aliases: ["open aprx", "switch project"],
    related: ["project.get", "map.list"])), IExecutionPrecondition
{
    /// <summary>
    /// Refuses unsaved work before the executor validates the approval token, so the refusal does
    /// not spend it. <see cref="ExecuteCoreAsync"/> re-checks on the UI turn that opens the project.
    /// </summary>
    public async ValueTask<OperationRefusal?> CheckPreconditionAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var exception = await context.Dispatcher.OnUiThreadAsync(() => Task.FromResult(UnsavedWork()), cancellationToken).ConfigureAwait(false);
        return exception is null ? null : new OperationRefusal(exception.Code, exception.Message);
    }

    /// <summary>
    /// The refusal for the current project's unsaved work, or null. Must run on the UI thread.
    /// Pending feature edits come first: with them ArcGIS Pro asks "Save all edits?"; otherwise a
    /// dirty project makes it ask "Save changes to &lt;project&gt;?". Either modal prompt blocks the
    /// open until a person answers it. ArcGIS Pro reports a project dirty right after opening it,
    /// so in practice project.open usually needs a project.save first.
    /// </summary>
    private OperationException? UnsavedWork() =>
        project.HasEdits ? PendingEdits() : project.IsDirty ? UnsavedProjectChanges() : null;

    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(RequiredString(arguments, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("ArcGIS Pro project was not found.", path);
        if (!string.Equals(Path.GetExtension(path), ".aprx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Project path must reference an .aprx file.", nameof(arguments));

        await context.Dispatcher.OnUiThreadAsync(async () =>
        {
            // With unsaved work ArcGIS Pro asks "Save all edits?" / "Save changes?" in a modal dialog
            // before closing the current project, and the call blocks until a person answers.
            // Refuse instead, re-checked on the same UI turn that opens the project.
            if (UnsavedWork() is { } refusal) throw refusal;
            await project.OpenAsync(path).ConfigureAwait(true);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(Json(new { opened = true, path, snapshot.Project.Name }), snapshot.Revision);
    }

    private const string RetryNote =
        " This refusal did not spend the approval token, but the token is bound to the workspace revision: " +
        "an approved project.save changes it, so request a new approval for project.open if the revision changed.";

    internal const string PendingEditsMessage =
        "The current project has unsaved feature edits, so ArcGIS Pro would stop on its modal \"Save all edits?\" prompt. " +
        "Nothing was opened. Save the edits with an approved project.save (it saves pending edits and the project), " +
        "or save or discard them in ArcGIS Pro, then retry project.open." + RetryNote;

    internal const string UnsavedProjectChangesMessage =
        "The current project has unsaved changes, so ArcGIS Pro would stop on its modal \"Save changes?\" prompt " +
        "(ArcGIS Pro also marks a project changed right after opening it). Nothing was opened. Save the project with " +
        "an approved project.save, or save it in ArcGIS Pro, then retry project.open." + RetryNote;

    internal static OperationException PendingEdits() => new(OperationErrorCodes.PendingEdits, PendingEditsMessage);

    internal static OperationException UnsavedProjectChanges() =>
        new(OperationErrorCodes.UnsavedProjectChanges, UnsavedProjectChangesMessage);
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
