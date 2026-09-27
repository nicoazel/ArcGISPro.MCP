using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Tests.Fakes;

namespace ArcGISProMCP.Operations.Tests;

/// <summary>
/// Moving operations out of the add-in is a refactor: no descriptor may change. The fixture
/// operation-descriptors.json was dumped from the add-in's registry (all 41 operations,
/// including the ArcPy pair) before any operation moved, so every operation the fake catalog
/// composes is compared field by field against what the add-in shipped.
/// </summary>
public sealed class DescriptorGuardTests
{
    [Fact]
    public void Every_portable_descriptor_is_identical_to_the_add_in_descriptor_before_the_seam()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        var fixture = LoadFixture("operation-descriptors.json");

        Assert.NotEmpty(pro.Registry.Descriptors);
        foreach (var descriptor in pro.Registry.Descriptors)
        {
            Assert.True(fixture.TryGetValue(descriptor.Id, out var expected), $"{descriptor.Id} is not a descriptor the add-in shipped.");
            var actual = Describe(descriptor);
            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), actual),
                $"{descriptor.Id} changed.{Environment.NewLine}expected: {expected.GetRawText()}{Environment.NewLine}actual: {actual.ToJsonString()}");
        }
    }

    [Fact]
    public void Portable_input_schemas_match_the_schemas_captured_before_the_typed_migration()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);
        var original = LoadFixture("operation-input-schemas.json");

        var compared = 0;
        foreach (var descriptor in pro.Registry.Descriptors)
        {
            if (!original.TryGetValue(descriptor.Id, out var expected)) continue;
            compared++;
            Assert.True(
                JsonNode.DeepEquals(NormalizeSchema(expected), NormalizeSchema(descriptor.InputSchema)),
                $"{descriptor.Id} input schema changed.");
        }
        Assert.True(compared > 0);
    }

    [Fact]
    public void Fake_catalog_composes_every_operation_type_in_the_operations_assembly()
    {
        using var arcPy = new ArcPyRuntimeFixture();
        using var pro = new FakePro(arcPy: arcPy.Settings);

        var declared = typeof(ProOperationBase).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IOperation).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);
        var composed = pro.Registry.Descriptors
            .Select(descriptor => pro.Operation(descriptor.Id).GetType().Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(declared, composed);
    }

    /// <summary>The same projection the pre-seam dump used.</summary>
    internal static JsonObject Describe(OperationDescriptor descriptor) => new()
    {
        ["id"] = descriptor.Id,
        ["version"] = descriptor.Version,
        ["title"] = descriptor.Title,
        ["summary"] = descriptor.Summary,
        ["risk"] = descriptor.Risk.ToString(),
        ["executionTarget"] = descriptor.ExecutionTarget.ToString(),
        ["requiresConfirmation"] = descriptor.RequiresConfirmation,
        ["undoable"] = descriptor.Undoable,
        ["executesUserCode"] = descriptor.ExecutesUserCode,
        ["typicalDuration"] = descriptor.TypicalDuration,
        ["requiredCapabilities"] = Array(descriptor.RequiredCapabilities.Order(StringComparer.Ordinal)),
        ["tags"] = Array(descriptor.Tags.Order(StringComparer.Ordinal)),
        ["aliases"] = Array(descriptor.Aliases),
        ["examples"] = Array(descriptor.Examples),
        ["relatedOperations"] = Array(descriptor.RelatedOperations),
        ["inputSchema"] = JsonNode.Parse(descriptor.InputSchema.GetRawText()),
        ["outputSchema"] = descriptor.OutputSchema is { } output ? JsonNode.Parse(output.GetRawText()) : null,
    };

    internal static Dictionary<string, JsonElement> LoadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)value).ToArray());

    /// <summary>Spells out object-schema defaults, as OperationSchemaMigrationTests does.</summary>
    private static JsonNode NormalizeSchema(JsonElement schema) => NormalizeNode(JsonNode.Parse(schema.GetRawText())!);

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
}
