using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.AddIn.ArcGIS;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class ViewCaptureOperation(ProResourceStore resources) : ProOperationBase(OperationDescriptor.Create(
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

    private static Task<CaptureResult> CaptureMapAsync(
        int width,
        int height,
        OperationContext context,
        CancellationToken cancellationToken) =>
        context.Dispatcher.OnUiThreadAsync(() =>
        {
            var view = MapView.Active ?? throw new InvalidOperationException("An active map view is required for capture.");
            if (!view.IsReady) throw new InvalidOperationException("The active map view is still drawing; retry when it is ready.");
            var bitmap = view.CaptureThumbnail(width, height)
                ?? throw new InvalidOperationException("ArcGIS Pro could not capture the active map view.");
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Task.FromResult(new CaptureResult(
                stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight,
                "map", view.Map.Name, view.Map.URI));
        }, cancellationToken);

    private static async Task<CaptureResult> CaptureLayoutAsync(
        string? layoutReference,
        int width,
        int height,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"ArcGISProMCP-{Guid.NewGuid():N}.png");
        try
        {
            var metadata = await context.Dispatcher.OnMainCimThreadAsync(() =>
            {
                var layout = string.IsNullOrWhiteSpace(layoutReference)
                    ? LayoutView.Active?.Layout ?? throw new InvalidOperationException("Specify a layout or activate one before capture.")
                    : ProHandles.ResolveLayout(layoutReference);
                var page = layout.GetPage();
                var resolution = (int)Math.Ceiling(Math.Max(width / page.Width, height / page.Height));
                layout.Export(new PNGFormat
                {
                    OutputFileName = temporaryPath,
                    Width = width,
                    Height = height,
                    Resolution = Math.Clamp(resolution, 24, 1200),
                    HasWorldFile = false,
                    HasTransparentBackground = false
                });
                return new CaptureMetadata(layout.Name, layout.URI);
            }, cancellationToken).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            bytes = ResizePng(bytes, width, height);
            return new CaptureResult(bytes, width, height, "layout", metadata.Name, metadata.Uri);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static int Integer(JsonElement arguments, string name, int defaultValue) =>
        arguments.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : defaultValue;

    private static byte[] ResizePng(byte[] bytes, int width, int height)
    {
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        if (source.PixelWidth == width && source.PixelHeight == height) return bytes;

        var resized = new TransformedBitmap(
            source,
            new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
        resized.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private sealed record CaptureResult(
        byte[] Bytes,
        int Width,
        int Height,
        string SourceKind,
        string SourceName,
        string SourceUri);

    private sealed record CaptureMetadata(string Name, string Uri);
}
