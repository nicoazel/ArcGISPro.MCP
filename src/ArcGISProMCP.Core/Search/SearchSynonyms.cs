using System.Collections.Immutable;

namespace ArcGISProMCP.Core.Search;

/// <summary>
/// A small, curated map from everyday and general GIS wording to the vocabulary ArcGIS Pro and the
/// operation descriptors use. It is directional (a user's word to the product's word) and applies to
/// queries only; indexed text is never expanded. Synonym matches score below direct matches
/// (<see cref="SearchIndex{T}.SynonymFactor"/>), so a descriptor that uses the caller's own word still wins.
/// </summary>
/// <remarks>
/// Entries must be general vocabulary that any GIS user or glossary would recognise. Do not add an
/// entry because one eval task phrases something oddly: the held-out eval sets exist to catch that.
/// This map is also the only place search-side aliases live; operation descriptors are not edited
/// for search tuning.
/// </remarks>
public static class SearchSynonyms
{
    // (what a user says, what the product calls it). Keys and targets may be phrases.
    private static readonly (string[] Keys, string[] Targets)[] Groups =
    [
        // Cartography and symbology.
        (["color", "colour", "colors", "colours", "fill", "restyle", "styling"], ["symbology", "symbol", "renderer", "color"]),
        (["red", "green", "blue", "yellow", "orange", "purple", "brown", "pink"], ["color"]),
        (["see through", "transparent", "translucent", "opacity", "opaque", "fade"], ["transparency"]),
        (["hide", "hidden", "visible", "invisible", "turn off"], ["visibility"]),
        (["heading", "headline", "caption"], ["title", "text"]),
        (["typeface", "pt", "point size", "font size", "bold", "italic", "arial", "helvetica", "verdana", "times new roman"], ["font", "text"]),
        (["page", "sheet", "printout", "print layout"], ["layout"]),
        (["background map", "background", "reference map"], ["basemap"]),
        (["satellite", "aerial", "orthophoto", "orthoimagery"], ["imagery"]),
        (["scene", "globe"], ["map", "scene"]),
        (["zoom", "pan", "fit", "bounding box", "bbox", "envelope", "area of interest", "aoi", "map window"], ["extent", "envelope"]),
        (["picture", "snapshot", "screengrab", "screen capture", "screen shot"], ["capture", "screenshot"]),

        // Selection and querying.
        (["highlight", "pick"], ["select", "selection"]),
        (["deselect", "unselect"], ["clear selection"]),
        (["find", "look up", "lookup", "look for", "filter"], ["query", "search", "select"]),
        (["where clause", "sql", "definition query"], ["where", "query", "attribute"]),

        // Records, fields and tables.
        (["record", "entry", "entries"], ["row", "record"]),
        (["row"], ["record", "row"]),
        (["column", "attribute"], ["field"]),
        (["how many", "number of", "tally"], ["count"]),
        (["average", "mean", "median", "sum", "total", "minimum", "maximum", "min", "max", "stats", "summarize", "summarise"], ["statistics", "summary"]),
        (["spreadsheet", "xlsx", "xls", "workbook"], ["excel"]),
        (["csv", "delimited", "text file"], ["table", "delimited"]),
        (["latitude", "longitude", "lat", "lon", "x y"], ["xy"]),

        // Generic verbs.
        (["add", "insert", "new", "make", "generate", "build", "set up"], ["create"]),
        (["remove", "drop", "discard", "purge"], ["delete", "remove"]),
        (["change", "modify", "edit", "alter"], ["update", "set", "alter"]),
        (["load", "import", "bring in"], ["add", "import"]),
        (["launch"], ["open", "run"]),
        (["save as", "write out"], ["export"]),
        (["persist", "store"], ["save"]),
        (["clone"], ["copy"]),
        (["compute", "calc", "determine", "work out"], ["calculate"]),
        (["enumerate", "inventory"], ["list"]),
        (["info", "information", "properties", "schema", "structure"], ["describe", "schema", "properties"]),
        (["execute", "invoke"], ["run", "execute"]),
        (["python", "py"], ["script", "python", "arcpy"]),
        (["checksum", "fingerprint", "sha"], ["hash"]),

        // Geometry, overlay and proximity.
        (["overlap", "overlapping", "in common", "common area", "intersection"], ["intersect"]),
        (["combine", "consolidate", "put together"], ["merge", "append"]),
        (["cut", "crop", "trim"], ["clip", "extract", "mask"]),
        (["subtract", "difference", "cut out"], ["erase"]),
        (["explode", "break apart", "multipart"], ["multipart", "singlepart", "split"]),
        (["reproject", "re project", "transform coordinates", "change projection", "change coordinate system"], ["project", "projection"]),
        (["coordinate system", "spatial reference", "crs", "srs", "epsg", "datum"], ["coordinate system", "spatial reference", "projection"]),
        (["fix", "repair", "correct", "heal", "clean up"], ["repair", "check"]),
        (["acres", "acre", "acreage", "hectares", "hectare", "perimeter", "square miles", "square kilometers", "square feet", "square meters"],
            ["area", "geometry"]),
        (["nearest", "closest", "nearby", "proximity"], ["near", "distance", "proximity"]),

        // Surfaces, rasters and statistics.
        (["dem", "dtm", "elevation model", "terrain model"], ["elevation", "surface", "dem"]),
        (["gradient", "steepness"], ["slope"]),
        (["shaded relief", "relief shading"], ["hillshade"]),
        (["heat map", "heatmap"], ["density", "kernel"]),
        (["hotspot", "hotspots"], ["hot spot"]),
        (["voronoi", "proximity polygons"], ["thiessen"]),
        (["grid cells", "hexagon", "hexagons", "hexbin", "hex grid", "square grid"], ["fishnet", "tessellation"]),
        (["gdb", "fgdb", "file gdb"], ["geodatabase"]),
        (["shp"], ["shapefile"])
    ];

    private static readonly ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Map = Build();

    /// <summary>Longest key, in words; phrase detection never looks further ahead.</summary>
    public static int LongestPhrase { get; } = Groups.SelectMany(group => group.Keys).Max(key => SearchText.Words(key).Count);

    /// <summary>Alternatives for a stem, or a space-joined stem phrase; empty when there are none.</summary>
    public static ImmutableArray<ImmutableArray<string>> For(string stemKey) =>
        Map.TryGetValue(stemKey, out var synonyms) ? synonyms : [];

    private static ImmutableDictionary<string, ImmutableArray<ImmutableArray<string>>> Build()
    {
        var builder = new Dictionary<string, List<ImmutableArray<string>>>(StringComparer.Ordinal);
        foreach (var (keys, targets) in Groups)
        {
            var targetStems = targets
                .Select(target => SearchText.Words(target).Where(word => !SearchText.IsStopWord(word)).Select(SearchText.Stem).ToImmutableArray())
                .Where(stems => !stems.IsEmpty)
                .ToArray();
            foreach (var key in keys)
            {
                var stemKey = string.Join(' ', SearchText.Words(key).Select(SearchText.Stem));
                if (!builder.TryGetValue(stemKey, out var list)) builder[stemKey] = list = [];
                foreach (var target in targetStems)
                {
                    // A word is never its own synonym; the direct match already covers it.
                    if (string.Join(' ', target) == stemKey) continue;
                    if (!list.Any(existing => existing.SequenceEqual(target))) list.Add(target);
                }
            }
        }

        return builder
            .Where(pair => pair.Value.Count > 0)
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableArray(), StringComparer.Ordinal);
    }
}
