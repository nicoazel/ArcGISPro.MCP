using ArcGISProMCP.Core.Operations;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class JsonSchemasTests
{
    [Fact]
    public void ObjectSchema_wraps_property_fragments_in_an_object()
    {
        var schema = JsonSchemas.ObjectSchema(
            "\"path\": {\"type\": \"string\"}, \"limit\": {\"type\": \"integer\"}", "path");
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal("path", schema.GetProperty("required")[0].GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }
}
