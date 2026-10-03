using ArcGIS.Core;
using ArcGIS.Core.Data;
using ArcGIS.Core.Data.Exceptions;
using ArcGIS.Desktop.Mapping;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

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

    /// <summary>
    /// Whether the table has a GlobalID field. The ArcGIS Pro 3.7 SDK documents two exceptions for
    /// <see cref="TableDefinition.HasGlobalID"/> (ArcGIS.Core.xml): <see cref="NotSupportedException"/>
    /// "the definition does not support this operation (e.g., the table is a shapefile)", seen live on
    /// a shapefile layer, and <see cref="GeodatabaseException"/>, the documented base class of every
    /// geodatabase exception. Both mean the GlobalID cannot be determined, which is treated as no
    /// GlobalID; any other exception is a real failure and propagates.
    /// </summary>
    public static bool HasGlobalId(TableDefinition definition)
    {
        try
        {
            return definition.HasGlobalID();
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (GeodatabaseException)
        {
            return false;
        }
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

    /// <summary>
    /// Runs a read of the member's data (definition, search, cursor iteration, counts). ArcGIS keeps a
    /// layer's table handle after its files are renamed or deleted and flags the layer
    /// <see cref="ConnectionStatus.Broken"/> only later, so the open succeeds and the next read throws a
    /// <see cref="GeodatabaseException"/> (seen live: the untranslated base type, "A geodatabase exception has
    /// occurred.", when a shapefile's files were renamed). That failure becomes
    /// <c>layer_data_source_unavailable</c>, with the original kept as the inner exception for the audit
    /// trail, when it says the dataset was not found or the layer's local files are gone. Any other
    /// geodatabase failure (an invalid where clause, an edit rule) propagates unchanged.
    /// </summary>
    public static T Read<T>(MapMember member, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (GeodatabaseException exception) when (IndicatesMissingSource(member, exception))
        {
            throw OperationException.LayerDataSourceUnavailable(member.Name, exception);
        }
    }

    /// <inheritdoc cref="Read{T}(MapMember, Func{T})"/>
    public static void Read(MapMember member, Action read)
    {
        ArgumentNullException.ThrowIfNull(read);
        Read(member, () =>
        {
            read();
            return true;
        });
    }

    private static bool IndicatesMissingSource(MapMember member, GeodatabaseException exception) =>
        exception is GeodatabaseNotFoundOrOpenedException ||
        IsBroken(member) ||
        LayerSources.LocalSourceMissing(TryGetPath(member), System.IO.File.Exists, System.IO.Directory.Exists) == true;

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
    /// The workspace path and dataset name of a feature layer's opened feature class, or nulls when
    /// the layer is not a feature layer or its data cannot be opened. Unlike
    /// <see cref="MapMember.GetPath"/>, this names a file geodatabase feature class without its
    /// feature dataset, so it can match a request spelled either way.
    /// </summary>
    public static (string? Workspace, string? Name) TryGetDatasetLocation(MapMember member)
    {
        if (member is not FeatureLayer layer || IsBroken(layer)) return (null, null);
        try
        {
            using var featureClass = OpenFeatureClass(layer);
            using var datastore = featureClass.GetDatastore();
            var workspace = datastore.GetPath();
            return (workspace is { IsAbsoluteUri: true, IsFile: true } ? workspace.LocalPath : null, featureClass.GetName());
        }
        catch (Exception exception) when (exception is not (CalledOnWrongThreadException or OperationCanceledException))
        {
            // Only a comparison hint: failing to read it means "not known to be the same dataset".
            return (null, null);
        }
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
