using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArcGIS.Desktop.Layouts;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.AddIn.ArcGIS.Services;

internal sealed class ProViewCaptureService : IViewCaptureService
{
    public MapViewCapture CaptureActiveMap(int width, int height)
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
        return new MapViewCapture(stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, view.Map.Name, view.Map.URI);
    }

    public LayoutExport ExportLayout(string? layoutReference, int width, int height, string outputPath)
    {
        var layout = string.IsNullOrWhiteSpace(layoutReference)
            ? LayoutView.Active?.Layout ?? throw new InvalidOperationException("Specify a layout or activate one before capture.")
            : ProHandles.ResolveLayout(layoutReference);
        var page = layout.GetPage();
        var resolution = (int)Math.Ceiling(Math.Max(width / page.Width, height / page.Height));
        layout.Export(new PNGFormat
        {
            OutputFileName = outputPath,
            Width = width,
            Height = height,
            Resolution = Math.Clamp(resolution, 24, 1200),
            HasWorldFile = false,
            HasTransparentBackground = false
        });
        return new LayoutExport(layout.Name, layout.URI);
    }

    public byte[] ResizePng(byte[] png, int width, int height)
    {
        using var input = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        if (source.PixelWidth == width && source.PixelHeight == height) return png;

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
}
