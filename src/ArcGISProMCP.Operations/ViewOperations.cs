using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Resources;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

internal sealed class ViewCaptureOperation(FileResourceStore resources, IViewCaptureService views) : ProOperationBase(OperationDescriptor.Create(
    "view.capture", "Capture map or layout view",
    "Captures the active map view or a named layout as PNG visual evidence and returns a resource handle plus semantic context.",
    ViewOperationSchemas.CaptureInput,
    outputSchema: ViewOperationSchemas.CaptureOutput,
    executionTarget: ExecutionTarget.ArcGISUiThread, capabilities: ["visual-observations"],
    tags: ["view", "capture", "screenshot", "visual evidence"], aliases: ["see map", "inspect map", "take screenshot"],
    related: ["map.activate", "layout.activate"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var width = Integer(arguments, "width", 1280);
        var height = Integer(arguments, "height", 800);
        if (width is < 64 or > 4096 || height is < 64 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Capture dimensions must be from 64 to 4096 pixels.");

        var viewKind = OptionalString(arguments, "view") ?? "active-map";
        var capture = string.Equals(viewKind, "layout", StringComparison.OrdinalIgnoreCase)
            ? await CaptureLayoutAsync(OptionalString(arguments, "layout"), width, height, context, cancellationToken).ConfigureAwait(false)
            : await CaptureMapAsync(width, height, context, cancellationToken).ConfigureAwait(false);

        var resource = await resources.StoreAsync(capture.Bytes, "image/png", ".png", $"{capture.SourceName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png", cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(
            Json(new
            {
                resource = resource.Uri,
                width = capture.Width,
                height = capture.Height,
                sourceKind = capture.SourceKind,
                sourceName = capture.SourceName,
                sourceUri = capture.SourceUri,
                capturedAt = DateTimeOffset.UtcNow
            }),
            snapshot.Revision,
            resources: [resource]);
    }

    private Task<CaptureResult> CaptureMapAsync(
        int width,
        int height,
        OperationContext context,
        CancellationToken cancellationToken) =>
        context.Dispatcher.OnUiThreadAsync(() =>
        {
            var capture = views.CaptureActiveMap(width, height);
            return Task.FromResult(new CaptureResult(
                capture.Png, capture.Width, capture.Height,
                "map", capture.MapName, capture.MapUri));
        }, cancellationToken);

    private async Task<CaptureResult> CaptureLayoutAsync(
        string? layoutReference,
        int width,
        int height,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-{Guid.NewGuid():N}.png");
        try
        {
            var metadata = await context.Dispatcher.OnMainCimThreadAsync(
                () => views.ExportLayout(layoutReference, width, height, temporaryPath),
                cancellationToken).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            // The export resolution is rounded up, so the file can exceed the requested size;
            // the stored image and the reported dimensions are always the requested ones.
            bytes = views.ResizePng(bytes, width, height);
            return new CaptureResult(bytes, width, height, "layout", metadata.Name, metadata.Uri);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static int Integer(JsonElement arguments, string name, int defaultValue) =>
        arguments.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : defaultValue;

    private sealed record CaptureResult(
        byte[] Bytes,
        int Width,
        int Height,
        string SourceKind,
        string SourceName,
        string SourceUri);
}
