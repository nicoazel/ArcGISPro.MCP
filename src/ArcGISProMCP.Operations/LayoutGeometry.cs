using System.Globalization;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Operations;

/// <summary>An axis-aligned box on a layout page, in page units (inches for layouts this server creates).</summary>
internal readonly record struct PageBox(double X, double Y, double Width, double Height)
{
    public double XMax => X + Width;

    public double YMax => Y + Height;
}

/// <summary>How an ArcGIS-placed map surround compares with the box it was asked to fill.</summary>
internal enum SurroundFit
{
    /// <summary>Anchored at the requested corner with the requested size.</summary>
    Exact,

    /// <summary>
    /// Anchored at the requested corner, but ArcGIS Pro sized it from its style (a legend fitting its
    /// items, a north arrow keeping its proportions, a scale bar snapping to whole divisions and taking
    /// its height from the style). Accepted; the result reports the actual bounds.
    /// </summary>
    Resized,

    /// <summary>Not anchored where requested, or a size no style adjustment explains.</summary>
    Rejected,
}

/// <summary>
/// Host-neutral page rules for layout operations: elements must be placed on the page, and the
/// acceptance rule for map surrounds whose final size ArcGIS Pro decides.
/// </summary>
internal static class LayoutGeometry
{
    /// <summary>How far (page units) an element may extend past the page edge.</summary>
    public const double PageTolerance = 0.01;

    /// <summary>How far (page units) the placed lower-left corner may drift from the requested one.</summary>
    public const double AnchorTolerance = 0.02;

    /// <summary>How closely (page units) a converged dimension must match the request.</summary>
    public const double SizeTolerance = 0.02;

    /// <summary>
    /// Throws <c>element_outside_page</c> unless the box lies on the page (within
    /// <see cref="PageTolerance"/>). <paramref name="element"/> describes it, for example
    /// "Map frame 'Main'".
    /// </summary>
    public static void EnsureOnPage(string element, PageBox box, double pageWidth, double pageHeight, string units)
    {
        if (box.X >= -PageTolerance && box.Y >= -PageTolerance &&
            box.XMax <= pageWidth + PageTolerance && box.YMax <= pageHeight + PageTolerance)
        {
            return;
        }

        throw new OperationException(
            OperationErrorCodes.ElementOutsidePage,
            $"{element} at x {Format(box.X)}, y {Format(box.Y)} with size {Format(box.Width)} x {Format(box.Height)} does not fit on the " +
            $"{Format(pageWidth)} x {Format(pageHeight)} {units} page. Keep x and y at least 0 and x + width, y + height within the page.");
    }

    /// <summary>Throws <c>element_outside_page</c> unless the anchor point lies on the page.</summary>
    public static void EnsurePointOnPage(string element, double x, double y, double pageWidth, double pageHeight, string units)
    {
        if (x >= -PageTolerance && y >= -PageTolerance && x <= pageWidth + PageTolerance && y <= pageHeight + PageTolerance) return;

        throw new OperationException(
            OperationErrorCodes.ElementOutsidePage,
            $"{element} anchored at x {Format(x)}, y {Format(y)} is outside the {Format(pageWidth)} x {Format(pageHeight)} {units} page.");
    }

    /// <summary>
    /// Whether a surround that ArcGIS placed at <paramref name="actual"/> satisfies a request for
    /// <paramref name="requested"/>. Every kind must be anchored at the requested lower-left corner.
    /// Legends and north arrows treat the request as a maximum box (legends fit their items with
    /// AdjustColumnsAndSize; north arrows keep their proportions, so one dimension converges). Scale
    /// bars take their height from the style and may round their width to whole divisions, so only
    /// the width is held to the request, within half to one and a half times.
    /// </summary>
    public static SurroundFit FitSurround(string kind, PageBox requested, PageBox actual)
    {
        var anchored = Math.Abs(actual.X - requested.X) <= AnchorTolerance &&
                       Math.Abs(actual.Y - requested.Y) <= AnchorTolerance;
        if (!anchored || !(actual.Width > 0) || !(actual.Height > 0)) return SurroundFit.Rejected;

        var widthMatches = Math.Abs(actual.Width - requested.Width) <= SizeTolerance;
        var heightMatches = Math.Abs(actual.Height - requested.Height) <= SizeTolerance;
        if (widthMatches && heightMatches) return SurroundFit.Exact;

        bool accepted;
        switch (kind)
        {
            case "scale-bar":
                accepted = actual.Width >= requested.Width * 0.5 && actual.Width <= requested.Width * 1.5;
                break;
            case "legend":
                accepted = WithinBox(requested, actual) && actual.Width >= 0.25 && actual.Height >= 0.15;
                break;
            default:
                accepted = WithinBox(requested, actual) &&
                           actual.Width >= requested.Width * 0.5 && actual.Height >= requested.Height * 0.5 &&
                           (widthMatches || heightMatches);
                break;
        }

        return accepted ? SurroundFit.Resized : SurroundFit.Rejected;
    }

    /// <summary>The notice text for a <see cref="SurroundFit.Resized"/> surround.</summary>
    public static string ResizedMessage(string kind, string name, PageBox requested, PageBox actual) =>
        $"ArcGIS Pro sized {kind} '{name}' to {Format(actual.Width)} x {Format(actual.Height)} from its style " +
        $"(requested {Format(requested.Width)} x {Format(requested.Height)}), anchored at x {Format(actual.X)}, y {Format(actual.Y)}. " +
        "The result's bounds are the actual size; check for overlaps with layout.inspect.";

    private static bool WithinBox(PageBox requested, PageBox actual) =>
        actual.Width <= requested.Width + SizeTolerance && actual.Height <= requested.Height + SizeTolerance;

    private static string Format(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
}
