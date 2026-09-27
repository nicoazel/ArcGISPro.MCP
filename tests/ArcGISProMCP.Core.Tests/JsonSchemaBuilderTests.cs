using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class JsonSchemaBuilderTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static JsonElement FeatureQuerySchema() => JsonSchemas.Object(
        [
            ("layer", JsonSchemas.String(minLength: 1)),
            ("map", JsonSchemas.String()),
            ("where", JsonSchemas.String(maxLength: 4096)),
            ("fields", JsonSchemas.Array(JsonSchemas.String(), maxItems: 64)),
            ("envelope", JsonSchemas.Object(
                [
                    ("xmin", JsonSchemas.Number()),
                    ("ymin", JsonSchemas.Number()),
                    ("xmax", JsonSchemas.Number()),
                    ("ymax", JsonSchemas.Number()),
                ],
                ["xmin", "ymin", "xmax", "ymax"])),
            ("spatialRelationship", JsonSchemas.Enum("intersects", "envelopeIntersects", "contains", "within", "touches", "crosses", "overlaps")),
            ("limit", JsonSchemas.Integer(minimum: 1, maximum: 500)),
        ],
        ["layer"]);

    // The original hand-written input schemas, captured before migration (see OperationSchemaMigrationTests).
    private static readonly Dictionary<string, JsonElement> Original = OperationSchemaMigrationTests.LoadFixture();

    [Fact]
    public void Object_matches_the_original_feature_layer_describe_schema()
    {
        var built = JsonSchemas.Object(
            [("layer", JsonSchemas.String(minLength: 1)), ("map", JsonSchemas.String())],
            ["layer"]);
        Assert.True(JsonElement.DeepEquals(Original["feature.layer.describe"], built), built.GetRawText());
    }

    [Fact]
    public void Object_matches_the_original_feature_query_schema()
    {
        var built = FeatureQuerySchema();
        Assert.True(JsonElement.DeepEquals(Original["feature.query"], built), built.GetRawText());
    }

    [Fact]
    public void Object_matches_literal_arcpy_inspect_script_schema()
    {
        var expected = JsonSchemas.Parse("""
            {
              "type": "object",
              "properties": { "scriptPath": { "type": "string", "minLength": 1, "maxLength": 512 } },
              "required": ["scriptPath"],
              "additionalProperties": false
            }
            """);
        var built = JsonSchemas.Object([("scriptPath", JsonSchemas.String(minLength: 1, maxLength: 512))], ["scriptPath"]);
        Assert.True(JsonElement.DeepEquals(expected, built), built.GetRawText());
    }

    [Fact]
    public void Object_without_properties_still_emits_properties_required_and_additional_properties()
    {
        var expected = JsonSchemas.Parse("""
            { "type": "object", "properties": { }, "required": [], "additionalProperties": false }
            """);
        var built = JsonSchemas.Object([]);
        Assert.True(JsonElement.DeepEquals(expected, built), built.GetRawText());
    }

    [Theory]
    [InlineData("""{"layer":"Parcels"}""")]
    [InlineData("""{"layer":"Parcels","map":"Map","where":"1=1","fields":["A","B"],"limit":500}""")]
    [InlineData("""{"layer":"Parcels","envelope":{"xmin":0,"ymin":0,"xmax":1.5,"ymax":2},"spatialRelationship":"within"}""")]
    public void Built_schema_accepts_valid_arguments(string json)
    {
        var issues = OperationArgumentValidator.Validate(JsonSchemas.Parse(json), FeatureQuerySchema());
        Assert.Empty(issues);
    }

    [Theory]
    [InlineData("""{}""", "$.layer")]
    [InlineData("""{"layer":""}""", "$.layer")]
    [InlineData("""{"layer":"Parcels","limit":0}""", "$.limit")]
    [InlineData("""{"layer":"Parcels","limit":501}""", "$.limit")]
    [InlineData("""{"layer":"Parcels","limit":1.5}""", "$.limit")]
    [InlineData("""{"layer":"Parcels","spatialRelationship":"near"}""", "$.spatialRelationship")]
    [InlineData("""{"layer":"Parcels","envelope":{"xmin":0,"ymin":0,"xmax":1}}""", "$.envelope.ymax")]
    [InlineData("""{"layer":"Parcels","fields":[1]}""", "$.fields[0]")]
    [InlineData("""{"layer":"Parcels","extra":true}""", "$.extra")]
    public void Built_schema_rejects_invalid_arguments(string json, string expectedPath)
    {
        var issues = OperationArgumentValidator.Validate(JsonSchemas.Parse(json), FeatureQuerySchema());
        Assert.Contains(issues, issue => issue.Path == expectedPath);
    }

    [Fact]
    public void Primitive_builders_emit_expected_keywords_and_validate()
    {
        var text = JsonSchemas.String(minLength: 2, maxLength: 4, pattern: "^[a-z]+$", description: "code");
        Assert.Equal("code", text.GetProperty("description").GetString());
        Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse("\"abc\""), text));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("\"ABC\""), text));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("\"a\""), text));

        var number = JsonSchemas.Number(exclusiveMinimum: 0, maximum: 1);
        Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse("1"), number));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("0"), number));

        Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse("true"), JsonSchemas.Boolean()));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("1"), JsonSchemas.Boolean()));

        var unique = JsonSchemas.Array(JsonSchemas.Integer(), minItems: 1, maxItems: 3, uniqueItems: true);
        Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse("[1,2]"), unique));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("[1,1]"), unique));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("[]"), unique));

        var any = JsonSchemas.Any();
        Assert.Equal(JsonValueKind.Object, any.ValueKind);
        Assert.Empty(any.EnumerateObject());
        foreach (var value in new[] { "null", "1", "\"x\"", "[1,{}]", "{\"a\":1}" })
            Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse(value), any));
    }

    [Fact]
    public void Object_supports_open_objects_and_property_count_bounds()
    {
        var schema = JsonSchemas.Object([("a", JsonSchemas.Integer())], additionalProperties: true, minProperties: 1, maxProperties: 2);
        Assert.Empty(OperationArgumentValidator.Validate(JsonSchemas.Parse("""{"a":1,"b":"x"}"""), schema));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("{}"), schema));
        Assert.NotEmpty(OperationArgumentValidator.Validate(JsonSchemas.Parse("""{"a":1,"b":2,"c":3}"""), schema));
    }

    [Fact]
    public void Unsupported_keyword_in_child_schema_throws_at_build_time()
    {
        var unsupported = JsonSchemas.Parse("""{ "oneOf": [ { "type": "string" }, { "type": "null" } ] }""");
        var error = Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("value", unsupported)]));
        Assert.Contains("oneOf", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => JsonSchemas.Array(unsupported));
    }

    [Fact]
    public void Malformed_child_schema_throws_at_build_time()
    {
        var typeArray = JsonSchemas.Parse("""{ "type": ["string", "null"] }""");
        Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("value", typeArray)]));
        Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("value", default)]));
    }

    [Fact]
    public void Invalid_builder_arguments_throw()
    {
        Assert.Throws<ArgumentException>(() => JsonSchemas.String(pattern: "(unclosed"));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSchemas.String(minLength: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSchemas.String(minLength: 5, maxLength: 4));
        Assert.Throws<ArgumentException>(() => JsonSchemas.Enum());
        Assert.Throws<ArgumentException>(() => JsonSchemas.Enum("a", "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSchemas.Integer(minimum: 2, maximum: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSchemas.Number(minimum: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => JsonSchemas.Array(minItems: 3, maxItems: 2));
        Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("a", JsonSchemas.Any()), ("a", JsonSchemas.Any())]));
        Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("a", JsonSchemas.Any())], ["b"]));
        Assert.Throws<ArgumentException>(() => JsonSchemas.Object([("", JsonSchemas.Any())]));
    }

    [Fact]
    public void ResultEnvelope_validates_serialized_successful_result()
    {
        var data = JsonSchemas.Parse("""{"count":3,"layers":["A","B","C"]}""");
        var result = OperationResult.Ok(
            data,
            "rev-7",
            [new OperationNotice("truncated", "Only the first 3 layers were returned.", "warning")],
            [new ResourceHandle("arcgis://resource/abc", "image/png", "view.png"), new ResourceHandle("arcgis://resource/def", "application/json")]);
        var wire = JsonSerializer.SerializeToElement(result, WireOptions);

        Assert.Empty(OperationArgumentValidator.Validate(wire, JsonSchemas.ResultEnvelope(null)));

        var typed = JsonSchemas.ResultEnvelope(JsonSchemas.Object(
            [("count", JsonSchemas.Integer(minimum: 0)), ("layers", JsonSchemas.Array(JsonSchemas.String()))],
            ["count", "layers"]));
        Assert.Empty(OperationArgumentValidator.Validate(wire, typed));
    }

    [Fact]
    public void ResultEnvelope_validates_serialized_failed_result_when_data_is_unconstrained()
    {
        var wire = JsonSerializer.SerializeToElement(OperationResult.Fail("not_found", "Layer not found.", "rev-1"), WireOptions);
        Assert.Empty(OperationArgumentValidator.Validate(wire, JsonSchemas.ResultEnvelope(null)));
    }

    [Fact]
    public void ResultEnvelope_uses_wire_property_names_and_rejects_mismatches()
    {
        var envelope = JsonSchemas.ResultEnvelope(JsonSchemas.Object([("count", JsonSchemas.Integer())], ["count"]));
        var names = envelope.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["success", "data", "errorCode", "message", "workspaceRevision", "notices", "resources"], names);

        var wrongData = JsonSerializer.SerializeToElement(OperationResult.Ok(JsonSchemas.Parse("""{"count":"x"}"""), "r"), WireOptions);
        Assert.Contains(OperationArgumentValidator.Validate(wrongData, envelope), issue => issue.Path == "$.data.count");

        var pascalCase = JsonSerializer.SerializeToElement(OperationResult.Ok(null, "r"));
        Assert.NotEmpty(OperationArgumentValidator.Validate(pascalCase, JsonSchemas.ResultEnvelope(null)));
    }
}
