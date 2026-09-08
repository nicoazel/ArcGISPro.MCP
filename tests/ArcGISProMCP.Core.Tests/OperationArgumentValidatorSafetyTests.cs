using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Tests;

public sealed class OperationArgumentValidatorSafetyTests
{
    [Fact]
    public void Rejects_duplicate_schema_keywords()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"type\":\"array\"}");
        using var arguments = JsonDocument.Parse("[]");

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rejects_duplicate_argument_properties_even_when_schema_is_empty()
    {
        using var schema = JsonDocument.Parse("{}");
        using var arguments = JsonDocument.Parse("{\"tool\":\"a\",\"tool\":\"b\"}");

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Path == "$.tool" && issue.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Traverses_unconstrained_gp_style_values_with_a_depth_bound()
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"parameters\":{\"type\":\"array\"}}}");
        var nested = string.Concat(Enumerable.Repeat("[", 70)) + "1" + string.Concat(Enumerable.Repeat("]", 70));
        using var arguments = JsonDocument.Parse("{\"parameters\":" + nested + "}", new JsonDocumentOptions { MaxDepth = 256 });

        var issues = OperationArgumentValidator.Validate(arguments.RootElement, schema.RootElement);

        Assert.Contains(issues, issue => issue.Message.Contains("depth", StringComparison.OrdinalIgnoreCase));
        Assert.True(issues.Count <= 100);
    }
}
