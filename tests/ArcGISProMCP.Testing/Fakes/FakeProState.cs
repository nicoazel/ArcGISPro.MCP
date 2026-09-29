using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Testing;

/// <summary>An in-memory ArcGIS Pro project that the fake services read and mutate.</summary>
internal sealed class FakeProState
{
    public string? ProjectName { get; set; } = "Fixture";

    public string? ProjectUri { get; set; } = @"C:\fixtures\Fixture.aprx";

    public bool IsDirty { get; set; }

    /// <summary>
    /// When set, feature edits mark the project dirty and a save cleans it, as ArcGIS Pro does.
    /// Off by default so operation tests control <see cref="IsDirty"/> explicitly.
    /// </summary>
    public bool TrackDirty { get; set; }

    /// <summary>Values IsDirty takes on successive snapshots; empty means use <see cref="IsDirty"/>.</summary>
    public Queue<bool> DirtySamples { get; } = new();

    public List<FakeMap> Maps { get; } = [];

    public string? ActiveMapName { get; set; }

    public List<FakeLayout> Layouts { get; } = [];

    public string? ActiveLayoutName { get; set; }

    /// <summary>Every service call, in order, for assertions about what an operation did.</summary>
    public List<string> Calls { get; } = [];

    public bool IsOpen => ProjectUri is not null;

    public FakeMap AddMap(string name, string type = "Map", params FakeLayer[] layers)
    {
        var map = new FakeMap(name, type, $"CIMPATH=map/{name.ToLowerInvariant()}.xml");
        map.Layers.AddRange(layers);
        Maps.Add(map);
        return map;
    }

    public FakeLayout AddLayout(string name, double pageWidthInches = 11, double pageHeightInches = 8.5)
    {
        var layout = new FakeLayout(name, $"CIMPATH=layout/{name.ToLowerInvariant()}.xml", pageWidthInches, pageHeightInches);
        Layouts.Add(layout);
        return layout;
    }

    public static string MapHandle(FakeMap map) => $"pro://map/{Uri.EscapeDataString(map.Uri)}";

    public static string LayerHandle(FakeLayer layer) => $"pro://layer/{Uri.EscapeDataString(layer.Uri)}";

    public static string LayoutHandle(FakeLayout layout) => $"pro://layout/{Uri.EscapeDataString(layout.Uri)}";
}

internal sealed class FakeMap(string name, string type, string uri)
{
    public string Name { get; } = name;

    public string Type { get; } = type;

    public string Uri { get; } = uri;

    public List<FakeLayer> Layers { get; } = [];

    public MapViewKind? CreatedAs { get; set; }

    public string? Basemap { get; set; }

    public int SelectionCount { get; set; }

    public bool HasOpenView { get; set; }
}

internal sealed class FakeLayer(string name, string uri, string type = "FeatureLayer", bool isFeatureLayer = true)
{
    public string Name { get; } = name;

    public string Uri { get; } = uri;

    public string Type { get; } = type;

    public bool IsFeatureLayer { get; } = isFeatureLayer;

    public bool IsVisible { get; set; } = true;

    public double Transparency { get; set; }

    public LayerElevation? Elevation { get; set; }

    /// <summary>The feature table behind a feature layer; null for layers the feature operations reject.</summary>
    public FakeFeatureTable? Table { get; init; }

    public static FakeLayer Feature(string name, LayerElevation? elevation = null, FakeFeatureTable? table = null) =>
        new(name, $"CIMPATH=layer/{name.ToLowerInvariant()}.xml")
        {
            Elevation = elevation ?? new LayerElevation("on-ground", 0, 1),
            Table = table ?? FakeFeatureTable.Parcels()
        };

    public static FakeLayer Other(string name, string type) =>
        new(name, $"CIMPATH=layer/{name.ToLowerInvariant()}.xml", type, isFeatureLayer: false);
}

internal sealed class FakeLayout(string name, string uri, double pageWidthInches, double pageHeightInches)
{
    public string Name { get; } = name;

    public string Uri { get; } = uri;

    public double PageWidthInches { get; } = pageWidthInches;

    public double PageHeightInches { get; } = pageHeightInches;
}
