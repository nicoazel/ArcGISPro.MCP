using System.IO;
using ArcGIS.Core;
using ArcGIS.Core.Data;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.ArcGIS;

/// <summary>
/// Opens a layer's underlying data. A broken or missing data source (for example relative paths
/// that no longer resolve after a project was copied) becomes <c>layer_data_source_unavailable</c>
/// instead of a NullReferenceException or COM failure deep inside an operation.
/// </summary>
internal static class LayerData
{
    /// <summary>ArcGIS reports <see cref="ConnectionStatus.Broken"/> when it failed to connect to the data source.</summary>
    public static bool IsBroken(MapMember member) => member.ConnectionStatus == ConnectionStatus.Broken;

    /// <summary>Throws <c>layer_data_source_unavailable</c> when the member's data connection is broken.</summary>
    public static void EnsureAvailable(MapMember member)
    {
        if (IsBroken(member)) throw OperationException.LayerDataSourceUnavailable(member.Name);
    }

    /// <summary>The layer's table; the caller disposes it.</summary>
    public static Table OpenTable(BasicFeatureLayer layer)
    {
        EnsureAvailable(layer);
        return Open(layer, layer.GetTable);
    }

    /// <summary>The layer's feature class; the caller disposes it.</summary>
    public static FeatureClass OpenFeatureClass(FeatureLayer layer)
    {
        EnsureAvailable(layer);
        return Open(layer, layer.GetFeatureClass);
    }

    /// <summary>The dataset path ArcGIS reports for the member, or null when it has none or cannot be read.</summary>
    public static Uri? TryGetPath(MapMember member)
    {
        try
        {
            return member.GetPath();
        }
        catch (Exception exception) when (exception is not (CalledOnWrongThreadException or OperationCanceledException))
        {
            return null;
        }
    }

    /// <summary>A display form of a dataset path: the local path for files, the URL otherwise.</summary>
    public static string? Display(Uri? path) => path is null ? null : path.IsFile ? path.LocalPath : path.ToString();

    /// <summary>
    /// Whether two dataset paths name the same data. File paths are compared case-insensitively after
    /// normalization, and a shapefile's ".shp" extension is optional (ArcGIS may report either form).
    /// </summary>
    public static bool SameSource(Uri? actual, Uri requested)
    {
        if (actual is null || !actual.IsAbsoluteUri) return false;
        if (actual.IsFile != requested.IsFile) return false;
        if (actual.IsFile)
            return string.Equals(NormalizeFile(actual.LocalPath), NormalizeFile(requested.LocalPath), StringComparison.OrdinalIgnoreCase);
        return string.Equals(actual.AbsoluteUri.TrimEnd('/'), requested.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFile(string path)
    {
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

    private static T Open<T>(MapMember member, Func<T?> open) where T : class
    {
        T? value;
        try
        {
            value = open();
        }
        catch (Exception exception) when (exception is not (CalledOnWrongThreadException or OperationCanceledException or OperationException))
        {
            // Opening the data is the only thing attempted here, so any failure means the source is unusable.
            throw OperationException.LayerDataSourceUnavailable(member.Name, exception);
        }
        return value ?? throw OperationException.LayerDataSourceUnavailable(member.Name);
    }
}
