using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationArgumentValidatorHardeningTests
{
    [Fact]
    public void Renderer_color_pattern_and_size_bounds_match_shipped_schema()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","required":["layer","color"],"additionalProperties":false,"properties":{
              "layer":{"type":"string","minLength":1},"color":{"type":"string","pattern":"^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$"},
              "size":{"type":"number","exclusiveMinimum":0}}}
            """);
        using var invalid = JsonDocument.Parse("""
            {"layer":"roads","color":"red","size":0}
            """);

        var issues = OperationArgumentValidator.Validate(invalid.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.color" && issue.Message.Contains("pattern", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Path == "$.size" && issue.Message.Contains("greater than", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Label_and_layout_constraints_validate_recursively()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","required":["layer","kind","width","height"],"additionalProperties":false,"properties":{
              "layer":{"type":"string","minLength":1},"fontFamily":{"type":"string","minLength":1},
              "kind":{"type":"string","enum":["legend","north-arrow","scale-bar"]},
              "width":{"type":"number","exclusiveMinimum":0},"height":{"type":"number","exclusiveMinimum":0}}}
            """);
        using var invalid = JsonDocument.Parse("""
            {"layer":"","fontFamily":"","kind":"title","width":0,"height":-1}
            """);

        var issues = OperationArgumentValidator.Validate(invalid.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.layer");
        Assert.Contains(issues, issue => issue.Path == "$.fontFamily");
        Assert.Contains(issues, issue => issue.Path == "$.kind" && issue.Message.Contains("enum", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Path == "$.width");
        Assert.Contains(issues, issue => issue.Path == "$.height");
    }

    [Fact]
    public void Array_bounds_unique_items_and_nested_object_paths_are_enforced()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "classes":{"type":"array","minItems":1,"maxItems":3,"uniqueItems":true,"items":{
                "type":"object","required":["value","color"],"additionalProperties":false,
                "properties":{"value":{"type":"string"},"color":{"type":"string"}}}}}}
            """);
        using var invalid = JsonDocument.Parse("""
            {"classes":[{"value":"a","color":"#fff"},{"value":"a","color":"#fff"},{"value":"b"},{"value":"c","color":"#000"}]}
            """);

        var issues = OperationArgumentValidator.Validate(invalid.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.classes" && issue.Message.Contains("at most", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "$.classes[1]" && issue.Message.Contains("unique", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(issues, issue => issue.Path == "$.classes[2].color");
    }

    [Fact]
    public void Malformed_or_unsupported_schema_is_reported_without_being_accepted()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","oneOf":[{"type":"string"}],"properties":{"name":{"pattern":"["}}}
            """);
        using var arguments = JsonDocument.Parse("""
            {"name":"value"}
            """);

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Message.Contains("Unsupported schema keyword", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Message.Contains("regular expression", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deep_and_oversized_inputs_fail_with_bounded_diagnostics()
    {
        using var schema = JsonDocument.Parse(DeepSchema(70), new JsonDocumentOptions { MaxDepth = 256 });
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 70)) + "{}" + new string('}', 70);
        using var arguments = JsonDocument.Parse(nested, new JsonDocumentOptions { MaxDepth = 256 });
        using var oversized = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 10_001)));
        using var arraySchema = JsonDocument.Parse("""{"type":"array","items":{"type":"integer"}}""");

        var deepIssues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);
        var largeIssues = OperationArgumentValidator.Validate(oversized.RootElement, arraySchema.RootElement);

        Assert.Contains(deepIssues, issue => issue.Message.Contains("depth", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(largeIssues, issue => issue.Message.Contains("too many", StringComparison.OrdinalIgnoreCase));
        Assert.True(deepIssues.Count <= 100);
        Assert.True(largeIssues.Count <= 100);
    }
    private static string DeepSchema(int depth) => depth <= 0
        ? "{\"type\":\"object\"}"
        : "{\"type\":\"object\",\"properties\":{\"a\":" + DeepSchema(depth - 1) + "}}";
}
