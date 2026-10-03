namespace ArcGISProMCP.Operations;

/// <summary>What <c>layer.add</c> does with an existing layer that has the requested name.</summary>
internal enum ExistingLayerAction
{
    /// <summary>The layer is healthy and reads the requested data: reuse it unchanged.</summary>
    Reuse,

    /// <summary>
    /// The layer is healthy but ArcGIS reports no dataset path for it (for example some service
    /// layers), so the source cannot be compared: reuse it unchanged and say so.
    /// </summary>
    ReuseUnverified,

    /// <summary>
    /// The layer is healthy but reads different data. It may only be repaired in place (dataset
    /// swapped, symbology kept); a healthy layer is never removed.
    /// </summary>
    RepairInPlaceOnly,

    /// <summary>
    /// The layer's data connection is broken: repair it in place, or as a last resort remove it and
    /// add a new layer at the same position.
    /// </summary>
    RepairOrRecreate,
}

/// <summary>
/// Host-neutral rules for comparing a layer's data source with a <c>layer.add</c> request. ArcGIS
/// spells the same data in several ways (".shp" or not, trailing separators, "/" or "\", case, a
/// file geodatabase feature class with or without its feature dataset), so equality is decided on
/// normalized paths and, failing that, on workspace plus dataset name.
/// </summary>
internal static class LayerSources
{
    private const string GeodatabaseSuffix = ".gdb";

    /// <summary>
    /// Decides what to do with an existing same-named data layer. <paramref name="sameDataset"/> is
    /// evaluated only when the paths differ; it compares workspace and dataset name, which the host
    /// reads from the layer's opened data.
    /// </summary>
    public static ExistingLayerAction DecideExisting(
        bool broken, bool requestIsLayerFile, Uri? actual, Uri requested, Func<bool> sameDataset)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(sameDataset);
        if (broken) return ExistingLayerAction.RepairOrRecreate;
        // A layer file's own path is never the layer's data path, so only health can be checked.
        if (requestIsLayerFile) return ExistingLayerAction.Reuse;
        if (actual is null) return ExistingLayerAction.ReuseUnverified;
        return SameSource(actual, requested) || sameDataset()
            ? ExistingLayerAction.Reuse
            : ExistingLayerAction.RepairInPlaceOnly;
    }

    /// <summary>
    /// The <c>DataSourceStatus</c> reported by <c>layer.add</c>: <c>broken</c>, <c>unverified</c>
    /// (no dataset path to compare), <c>mismatch</c> (the layer reads something other than the
    /// request) or <c>ok</c>.
    /// </summary>
    public static string DataSourceStatus(
        bool broken, bool requestIsLayerFile, bool created, Uri? actual, Uri requested, Func<bool> sameDataset)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(sameDataset);
        if (broken) return "broken";
        // A new layer was created from the request itself, and a layer file's path is not a data path.
        if (requestIsLayerFile || (created && actual is null)) return "ok";
        if (actual is null) return "unverified";
        return SameSource(actual, requested) || sameDataset() ? "ok" : "mismatch";
    }

    /// <summary>
    /// Whether two dataset paths name the same data. File paths are compared case-insensitively after
    /// normalization, and a shapefile's ".shp" extension is optional (ArcGIS may report either form).
    /// A null or relative <paramref name="actual"/> never matches. Different spellings of one share
    /// (a UNC path and a mapped drive) are not resolved here and compare unequal.
    /// </summary>
    public static bool SameSource(Uri? actual, Uri requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (actual is null || !actual.IsAbsoluteUri || !requested.IsAbsoluteUri) return false;
        if (actual.IsFile != requested.IsFile) return false;
        if (actual.IsFile)
            return string.Equals(NormalizeFile(actual.LocalPath), NormalizeFile(requested.LocalPath), StringComparison.OrdinalIgnoreCase);
        return string.Equals(actual.AbsoluteUri.TrimEnd('/'), requested.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A comparable form of a local dataset path: full path, backslash separators, no trailing
    /// separator and no ".shp" extension. Paths that cannot be made full are kept as given.
    /// </summary>
    public static string NormalizeFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }
        full = full.Replace('/', '\\').TrimEnd('\\');
        return full.EndsWith(".shp", StringComparison.OrdinalIgnoreCase) ? full[..^4] : full;
    }

    /// <summary>
    /// Whether two workspace-plus-dataset locations name the same data: the workspace paths are
    /// normalized like <see cref="NormalizeFile"/> and the dataset names are compared ignoring case
    /// and a ".shp" extension. This is how a feature class reported with its feature dataset
    /// (<c>x.gdb\Transport\Roads</c>) matches a request without it (<c>x.gdb\Roads</c>).
    /// </summary>
    public static bool SameDataset(string? actualWorkspace, string? actualName, string? requestedWorkspace, string? requestedName)
    {
        if (string.IsNullOrWhiteSpace(actualWorkspace) || string.IsNullOrWhiteSpace(actualName) ||
            string.IsNullOrWhiteSpace(requestedWorkspace) || string.IsNullOrWhiteSpace(requestedName))
        {
            return false;
        }
        return string.Equals(NormalizeFile(actualWorkspace), NormalizeFile(requestedWorkspace), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(StripShp(actualName.Trim()), StripShp(requestedName.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits a local shapefile or file geodatabase feature class path into its workspace (the
    /// shapefile folder or the <c>.gdb</c> folder) and dataset name; false for anything else. The
    /// last <c>.gdb</c> path segment is the geodatabase, so a parent folder that happens to end in
    /// ".gdb" is not mistaken for it, and a feature dataset segment is skipped.
    /// </summary>
    public static bool TrySplitLocalDataset(string path, out string workspace, out string datasetName, out bool isShapefile)
    {
        workspace = string.Empty;
        datasetName = string.Empty;
        isShapefile = false;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var segments = path.Replace('/', '\\').Split('\\');
        var trimmed = segments.Length;
        while (trimmed > 0 && segments[trimmed - 1].Length == 0) trimmed--;
        if (trimmed < 2) return false;

        var last = segments[trimmed - 1];
        if (last.EndsWith(".shp", StringComparison.OrdinalIgnoreCase))
        {
            var name = last[..^4];
            if (name.Length == 0) return false;
            workspace = string.Join('\\', segments, 0, trimmed - 1);
            datasetName = name;
            isShapefile = true;
            return workspace.Length > 0;
        }

        // The geodatabase is the last ".gdb" segment that has at least one segment after it.
        for (var index = trimmed - 2; index >= 0; index--)
        {
            if (!segments[index].EndsWith(GeodatabaseSuffix, StringComparison.OrdinalIgnoreCase) ||
                segments[index].Length == GeodatabaseSuffix.Length)
            {
                continue;
            }
            // Feature dataset\feature class is the deepest layout a file geodatabase supports.
            if (trimmed - 1 - index > 2) return false;
            workspace = string.Join('\\', segments, 0, index + 1);
            datasetName = last;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a layer's local data is known to be gone, for classifying a geodatabase failure on
    /// a layer ArcGIS has not (yet) flagged as broken. True when neither the dataset path (with or
    /// without ".shp") nor any enclosing file geodatabase exists; false when the dataset path
    /// exists; null when it cannot be told from the file system: no path, a service URL, or a
    /// dataset inside an existing geodatabase, GeoPackage or connection file.
    /// </summary>
    public static bool? LocalSourceMissing(Uri? path, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(directoryExists);
        if (path is not { IsAbsoluteUri: true, IsFile: true }) return null;
        var local = path.LocalPath.TrimEnd('\\', '/');
        if (local.Length == 0) return null;
        if (fileExists(local) || directoryExists(local)) return false;
        if (!local.EndsWith(".shp", StringComparison.OrdinalIgnoreCase) && fileExists(local + ".shp")) return false;

        // Walk up to the first existing ancestor. A geodatabase directory or a container file
        // (.gpkg, .sqlite, .sde) holds datasets that are not file-system entries: undecidable.
        // A plain folder means the file-based dataset itself is gone.
        for (var ancestor = Path.GetDirectoryName(local); !string.IsNullOrEmpty(ancestor); ancestor = Path.GetDirectoryName(ancestor))
        {
            if (fileExists(ancestor)) return null;
            if (directoryExists(ancestor))
                return ancestor.EndsWith(GeodatabaseSuffix, StringComparison.OrdinalIgnoreCase) ? null : true;
        }
        return true;
    }

    private static string StripShp(string name) =>
        name.EndsWith(".shp", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
