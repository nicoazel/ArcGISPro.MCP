using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationArgumentValidatorTests
{
    [Fact]
    public void Accepts_supported_object_schema_subset()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type":"object",
              "required":["name","count","mode","values"],
              "additionalProperties":false,
              "properties":{
                "name":{"type":"string","minLength":3},
                "count":{"type":"integer","minimum":1,"exclusiveMaximum":10},
                "mode":{"type":"string","enum":["fast","safe"]},
                "values":{"type":"array","items":{"type":"number","minimum":0}}
              }
            }
            """);
        using var arguments = JsonDocument.Parse("""
            {"name":"Map","count":4,"mode":"safe","values":[0,1.5,8]}
            """);

        Assert.Empty(OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement));
    }

    [Fact]
    public void Reports_required_additional_type_enum_and_string_paths()
    {
        using var schema = JsonDocument.Parse("""
            {
              "type":"object", "required":["name","mode"], "additionalProperties":false,
              "properties":{"name":{"type":"string","minLength":3},"mode":{"type":"string","enum":["safe"]}}
            }
            """);
        using var arguments = JsonDocument.Parse("""
            {"mode":2,"extra":true}
            """);

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.name" && issue.Message.Contains("Required", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "$.mode" && issue.Message.Contains("Expected type", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "$.extra" && issue.Message.Contains("Additional", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_numeric_bounds_and_nested_array_item_paths()
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "count":{"type":"number","minimum":1,"maximum":5,"exclusiveMinimum":1,"exclusiveMaximum":5},
              "items":{"type":"array","items":{"type":"string","minLength":2}}
            }}
            """);
        using var arguments = JsonDocument.Parse("""
            {"count":1,"items":["ok","x"]}
            """);

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);
        using var upperArguments = JsonDocument.Parse("""
            {"count":6,"items":["ok","ok"]}
            """);
        var upperIssues = OperationArgumentValidator.Validate(upperArguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.count" && issue.Message.Contains("greater than", StringComparison.Ordinal));
        Assert.Contains(upperIssues, issue => issue.Path == "$.count" && issue.Message.Contains("at most", StringComparison.Ordinal));
        Assert.Contains(issues, issue => issue.Path == "$.items[1]" && issue.Message.Contains("length", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_root_type_and_integer_fraction()
    {
        using var objectSchema = JsonDocument.Parse("""
            {"type":"object"}
            """);
        using var arraySchema = JsonDocument.Parse("""
            {"type":"array","items":{"type":"integer"}}
            """);
        using var scalar = JsonDocument.Parse("42");
        using var fractionalArray = JsonDocument.Parse("[1.25]");

        var rootIssues = OperationArgumentValidator.Validate(scalar.RootElement, objectSchema.RootElement);
        var itemIssues = OperationArgumentValidator.Validate(fractionalArray.RootElement, arraySchema.RootElement);

        Assert.Contains(rootIssues, issue => issue.Path == "$" && issue.Message.Contains("object", StringComparison.Ordinal));
        Assert.Contains(itemIssues, issue => issue.Path == "$[0]" && issue.Message.Contains("integer", StringComparison.Ordinal));
    }
}
