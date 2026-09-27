using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArcGISProMCP.AddIn.Operations;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// Guards the migration of add-in operation input schemas from hand-written JSON strings
/// (the since-removed <c>JsonSchemas.ObjectSchema</c>) to the typed builder. Fixtures/operation-input-schemas.json
/// was generated from the original source strings before any call site changed. The migrated
/// schemas live in src/ArcGISProMCP.AddIn/Operations/Schemas, which depends only on Core and is
/// compiled into this test assembly, so the exact production schema objects are compared here.
/// </summary>
public sealed class OperationSchemaMigrationTests
{
    /// <summary>
    /// Operation id to (migrated schema, the member reference its descriptor must use).
    /// </summary>
    private static readonly Dictionary<string, (JsonElement Schema, string Reference)> Migrated = new(StringComparer.Ordinal)
    {
        ["arcpy.inspect-script"] = (ArcPyOperationSchemas.InspectScriptInput, "ArcPyOperationSchemas.InspectScriptInput"),
        ["arcpy.run-script"] = (ArcPyOperationSchemas.RunScriptInput, "ArcPyOperationSchemas.RunScriptInput"),
        ["feature.layer.describe"] = (FeatureOperationSchemas.LayerDescribeInput, "FeatureOperationSchemas.LayerDescribeInput"),
        ["feature.query"] = (FeatureOperationSchemas.QueryInput, "FeatureOperationSchemas.QueryInput"),
        ["feature.select"] = (FeatureOperationSchemas.SelectInput, "FeatureOperationSchemas.SelectInput"),
        ["feature.create"] = (FeatureOperationSchemas.CreateInput, "FeatureOperationSchemas.CreateInput"),
        ["feature.update"] = (FeatureOperationSchemas.UpdateInput, "FeatureOperationSchemas.UpdateInput"),
        ["feature.delete"] = (FeatureOperationSchemas.DeleteInput, "FeatureOperationSchemas.DeleteInput"),
        ["layer.list"] = (LayerOperationSchemas.ListInput, "LayerOperationSchemas.ListInput"),
        ["layer.add"] = (LayerOperationSchemas.AddInput, "LayerOperationSchemas.AddInput"),
        ["layer.set-appearance"] = (LayerOperationSchemas.SetAppearanceInput, "LayerOperationSchemas.SetAppearanceInput"),
        ["layer.set-elevation"] = (LayerOperationSchemas.SetElevationInput, "LayerOperationSchemas.SetElevationInput"),
        ["basemap.set"] = (LayerOperationSchemas.BasemapSetInput, "LayerOperationSchemas.BasemapSetInput"),
        ["style.search"] = (LayerOperationSchemas.StyleSearchInput, "LayerOperationSchemas.StyleSearchInput"),
        ["layout.inspect"] = (LayoutOperationSchemas.InspectInput, "LayoutOperationSchemas.InspectInput"),
        ["layout.ensure"] = (LayoutOperationSchemas.EnsureInput, "LayoutOperationSchemas.EnsureInput"),
        ["layout.add-map-frame"] = (LayoutOperationSchemas.AddMapFrameInput, "LayoutOperationSchemas.AddMapFrameInput"),
        ["layout.activate"] = (LayoutOperationSchemas.ActivateInput, "LayoutOperationSchemas.ActivateInput"),
        ["layout.set-text"] = (LayoutPresentationOperationSchemas.SetTextInput, "LayoutPresentationOperationSchemas.SetTextInput"),
        ["layout.ensure-surround"] = (LayoutSurroundOperationSchemas.EnsureSurroundInput, "LayoutSurroundOperationSchemas.EnsureSurroundInput"),
        ["layout.set-frame-extent"] = (PresentationRefinementOperationSchemas.SetFrameExtentInput, "PresentationRefinementOperationSchemas.SetFrameExtentInput"),
        ["symbology.set-unique-values"] = (PresentationRefinementOperationSchemas.SetUniqueValuesInput, "PresentationRefinementOperationSchemas.SetUniqueValuesInput"),
        ["map.ensure"] = (MapOperationSchemas.EnsureInput, "MapOperationSchemas.EnsureInput"),
        ["map.activate"] = (MapOperationSchemas.ActivateInput, "MapOperationSchemas.ActivateInput"),
        ["map.clear-selection"] = (MapSelectionOperationSchemas.ClearSelectionInput, "MapSelectionOperationSchemas.ClearSelectionInput"),
        ["metadata.get"] = (MetadataOperationSchemas.GetInput, "MetadataOperationSchemas.GetInput"),
        ["metadata.update"] = (MetadataOperationSchemas.UpdateInput, "MetadataOperationSchemas.UpdateInput"),
        ["project.open"] = (ProjectOperationSchemas.OpenInput, "ProjectOperationSchemas.OpenInput"),
        ["symbology.set-simple"] = (SymbologyOperationSchemas.SetSimpleInput, "SymbologyOperationSchemas.SetSimpleInput"),
        ["label.configure"] = (SymbologyOperationSchemas.LabelConfigureInput, "SymbologyOperationSchemas.LabelConfigureInput"),
        ["table.query"] = (TableOperationSchemas.QueryInput, "TableOperationSchemas.QueryInput"),
        ["table.statistics"] = (TableOperationSchemas.StatisticsInput, "TableOperationSchemas.StatisticsInput"),
        ["view.capture"] = (ViewOperationSchemas.CaptureInput, "ViewOperationSchemas.CaptureInput"),
    };

    [Fact]
    public void Migrated_input_schemas_are_semantically_identical_to_the_original_string_schemas()
    {
        var fixture = LoadFixture();
        foreach (var (id, (schema, _)) in Migrated)
        {
            Assert.True(fixture.TryGetValue(id, out var original), $"No original schema was captured for {id}.");
            var expected = Normalize(original);
            var actual = Normalize(schema);
            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"{id} changed semantics.{Environment.NewLine}original: {expected.ToJsonString()}{Environment.NewLine}migrated: {actual.ToJsonString()}");
        }
    }

    [Fact]
    public void Migrated_descriptors_reference_their_typed_schema()
    {
        var sources = ReadOperationSources();
        foreach (var (id, (_, reference)) in Migrated)
        {
            var descriptor = Descriptor(sources, id);
            Assert.DoesNotContain("JsonSchemas.ObjectSchema(", descriptor, StringComparison.Ordinal);
            Assert.Contains(reference, descriptor, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_captured_schema_is_migrated()
    {
        Assert.Equal(LoadFixture().Keys.Order(StringComparer.Ordinal), Migrated.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void No_operation_declares_a_string_schema()
    {
        // The string-based JsonSchemas.ObjectSchema helper was removed once gp.run migrated.
        foreach (var path in Directory.GetFiles(OperationsDirectory(), "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("ObjectSchema(", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Semantic_comparison_detects_constraint_changes()
    {
        var original = LoadFixture()["feature.update"];
        var loosened = JsonNode.Parse(original.GetRawText())!;
        loosened["properties"]!["target"]!["maxProperties"] = 2;
        var reordered = JsonNode.Parse(original.GetRawText())!;
        reordered["required"] = new JsonArray("target", "layer");

        Assert.True(JsonNode.DeepEquals(Normalize(original), Normalize(original)));
        Assert.False(JsonNode.DeepEquals(Normalize(original), Normalize(JsonSerializer.SerializeToElement(loosened))));
        Assert.False(JsonNode.DeepEquals(Normalize(original), Normalize(JsonSerializer.SerializeToElement(reordered))));
    }

    /// <summary>
    /// Makes JSON Schema defaults explicit so that schemas that differ only in spelling out a
    /// default compare equal: an object schema without <c>properties</c>, <c>required</c> or
    /// <c>additionalProperties</c> behaves (in OperationArgumentValidator and JSON Schema) exactly
    /// like one with <c>{}</c>, <c>[]</c> and <c>true</c>. Property order is irrelevant to DeepEquals.
    /// </summary>
    internal static JsonNode Normalize(JsonElement schema) => NormalizeNode(JsonNode.Parse(schema.GetRawText())!);

    private static JsonNode NormalizeNode(JsonNode node)
    {
        if (node is not JsonObject schema) return node;
        if (schema["properties"] is JsonObject properties)
            foreach (var name in properties.Select(pair => pair.Key).ToArray())
                properties[name] = NormalizeNode(properties[name]!.DeepClone());
        if (schema["items"] is JsonObject items)
            schema["items"] = NormalizeNode(items.DeepClone());
        if (schema["type"]?.GetValue<string>() == "object")
        {
            if (!schema.ContainsKey("properties")) schema["properties"] = new JsonObject();
            if (!schema.ContainsKey("required")) schema["required"] = new JsonArray();
            if (!schema.ContainsKey("additionalProperties")) schema["additionalProperties"] = true;
        }
        return schema;
    }

    internal static Dictionary<string, JsonElement> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "operation-input-schemas.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    internal static string Descriptor(IReadOnlyList<string> sources, string id)
    {
        foreach (var source in sources)
        {
            var match = Regex.Match(source, $@"OperationDescriptor\.Create\(\s*""{Regex.Escape(id)}""");
            if (!match.Success) continue;
            var end = source.IndexOf("protected override", match.Index, StringComparison.Ordinal);
            return source[match.Index..end];
        }
        Assert.Fail($"Descriptor {id} not found.");
        return string.Empty;
    }

    internal static IReadOnlyList<string> ReadOperationSources() =>
        Directory.GetFiles(OperationsDirectory(), "*.cs").Select(File.ReadAllText).ToArray();

    internal static string OperationsDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "ArcGISProMCP.AddIn", "Operations");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("Could not locate src/ArcGISProMCP.AddIn/Operations from the test output tree.");
    }
}
