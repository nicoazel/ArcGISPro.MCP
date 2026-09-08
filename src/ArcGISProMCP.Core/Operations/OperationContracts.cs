using System.Collections.Immutable;
using System.Text.Json;

namespace ArcGISProMCP.Core.Operations;

public enum OperationRisk
{
    ReadOnly,
    SafeWrite,
    Destructive,
    ExternalSideEffect
}

public enum ExecutionTarget
{
    ArcGISMainCimThread,
    ArcGISUiThread,
    Background,
    RhinoUiThread,
    ExternalWorker
}

public sealed record OperationDescriptor(
    string Id,
    string Version,
    string Title,
    string Summary,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    OperationRisk Risk,
    ExecutionTarget ExecutionTarget,
    ImmutableHashSet<string> RequiredCapabilities,
    ImmutableHashSet<string> Tags,
    ImmutableArray<string> Aliases,
    ImmutableArray<string> Examples,
    ImmutableArray<string> RelatedOperations,
    bool RequiresConfirmation = false,
    bool Undoable = false,
    string? TypicalDuration = null)
{
    public static OperationDescriptor Create(
        string id,
        string title,
        string summary,
        JsonElement inputSchema,
        OperationRisk risk = OperationRisk.ReadOnly,
        ExecutionTarget executionTarget = ExecutionTarget.ArcGISMainCimThread,
        IEnumerable<string>? capabilities = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? aliases = null,
        IEnumerable<string>? examples = null,
        IEnumerable<string>? related = null,
        bool requiresConfirmation = false,
        bool undoable = false,
        string version = "1.0.0",
        JsonElement? outputSchema = null,
        string? typicalDuration = null) =>
        new(
            id,
            version,
            title,
            summary,
            inputSchema.Clone(),
            outputSchema?.Clone(),
            risk,
            executionTarget,
            (capabilities ?? []).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            (tags ?? []).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            (aliases ?? []).ToImmutableArray(),
            (examples ?? []).ToImmutableArray(),
            (related ?? []).ToImmutableArray(),
            requiresConfirmation,
            undoable,
            typicalDuration);
}

public sealed record OperationRequest(
    string OperationId,
    JsonElement Arguments,
    string? ExpectedWorkspaceRevision = null,
    string? ConfirmationToken = null,
    string? IdempotencyKey = null,
    bool DryRun = false);

public sealed record OperationResult(
    bool Success,
    JsonElement? Data,
    string? ErrorCode,
    string? Message,
    string WorkspaceRevision,
    ImmutableArray<OperationNotice> Notices,
    ImmutableArray<ResourceHandle> Resources)
{
    public static OperationResult Ok(
        JsonElement? data,
        string revision,
        IEnumerable<OperationNotice>? notices = null,
        IEnumerable<ResourceHandle>? resources = null) =>
        new(true, data?.Clone(), null, null, revision,
            (notices ?? []).ToImmutableArray(), (resources ?? []).ToImmutableArray());

    public static OperationResult Fail(string code, string message, string revision) =>
        new(false, null, code, message, revision, [], []);
}

public sealed record OperationNotice(string Code, string Message, string Severity = "info");

public sealed record ResourceHandle(string Uri, string MimeType, string? Name = null);

public sealed record OperationQuery(
    string? Text = null,
    string? Domain = null,
    ImmutableHashSet<string>? Capabilities = null,
    OperationRisk? MaximumRisk = null,
    int Limit = 20);

public sealed record SearchHit(OperationDescriptor Descriptor, double Score, ImmutableArray<string> MatchedTerms);
