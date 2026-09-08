using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using ArcGISProMCP.Core;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class RegistryAndWorkflowAssetTests
{
    [Fact]
    public void Registry_is_deterministic_and_sorted_by_stable_id()
    {
        var registry = new OperationRegistry();
        registry.Register(FakeOperation("arcgis.test.z"));
        registry.Register(FakeOperation("arcgis.test.a"));

        Assert.Equal(new[] { "arcgis.test.a", "arcgis.test.z" },
            registry.Descriptors.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void Registry_rejects_duplicate_stable_ids()
    {
        var registry = new OperationRegistry();
        registry.Register(FakeOperation("arcgis.test.duplicate"));
        Assert.NotNull(Record.Exception(() =>
            registry.Register(FakeOperation("arcgis.test.duplicate"))));
    }

    [Fact]
    public void Registry_try_get_returns_registered_operation()
    {
        var registry = new OperationRegistry();
        var operation = FakeOperation("arcgis.test.lookup");
        registry.Register(operation);
        Assert.True(registry.TryGet("arcgis.test.lookup", out var found));
        Assert.Same(operation, found);
    }

    [Fact]
    public void Master_skill_declares_capabilities_invariants_and_visual_checks()
    {
        using var json = JsonDocument.Parse(ReadAsset("skills", "master-cartography.skill.json"));
        var root = json.RootElement;
        Assert.Equal("arcgis.cartography.master-plan", root.GetProperty("id").GetString());
        Assert.Equal("1.0.1", root.GetProperty("version").GetString());
        Assert.NotEmpty(root.GetProperty("requiredCapabilities").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("allowedOperations").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("preconditions").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("visualChecks").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("recoveryGuidance").EnumerateArray());
        Assert.Equal("workflow.master-cartography-rhino-handoff", root.GetProperty("workflowId").GetString());
    }

    [Fact]
    public void Master_workflow_has_three_maps_one_layout_and_valid_dependencies()
    {
        using var json = JsonDocument.Parse(ReadAsset("workflows", "master-cartography-rhino-handoff.workflow.json"));
        var root = json.RootElement;
        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        var ids = steps.Select(s => s.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal("workflow.master-cartography-rhino-handoff", root.GetProperty("id").GetString());
        Assert.Equal("1.0.1", root.GetProperty("version").GetString());
        Assert.Contains(steps, s => s.GetProperty("arguments").ToString().Contains("Zoning", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.GetProperty("arguments").ToString().Contains("Transit", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.GetProperty("arguments").ToString().Contains("Buildings-SiteDesign", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.GetProperty("operation").GetString() == "layout.activate");
        Assert.Contains(steps, s => s.GetProperty("operation").GetString() == "view.capture");
        Assert.Contains(steps, s => s.GetProperty("operation").GetString() == "rhino.handoff");

        using var skillJson = JsonDocument.Parse(ReadAsset("skills", "master-cartography.skill.json"));
        var allowed = skillJson.RootElement.GetProperty("allowedOperations").EnumerateArray()
            .Select(operation => operation.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(steps, step => Assert.Contains(step.GetProperty("operation").GetString()!, allowed));

        foreach (var step in steps)
        foreach (var dependency in step.GetProperty("dependsOn").EnumerateArray())
        {
            var dependencyId = dependency.GetString();
            Assert.NotNull(dependencyId);
            Assert.Contains(dependencyId!, ids);
        }

        Assert.True(IsAcyclic(steps));
    }

    private static TestOperation FakeOperation(string id) => new(id);

    private sealed class TestOperation(string id) : IOperation
    {
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(
            id,
            "Test operation",
            "Registry contract test operation.",
            JsonSchemas.EmptyObject);

        public Task<OperationResult> ExecuteAsync(
            JsonElement arguments,
            OperationContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok(null, "test"));
    }

    [Fact]
    public void Presentation_workflow_is_parameterized_acyclic_and_captures_three_clean_frames()
    {
        using var document = JsonDocument.Parse(ReadAsset("workflows", "east-liberty-presentation.workflow.json"));
        var root = document.RootElement;
        Assert.Contains(root.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == "proposalSource");
        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        Assert.True(IsAcyclic(steps));
        Assert.Equal(3, steps.Count(step => step.GetProperty("operation").GetString() == "layout.add-map-frame"));
        Assert.Equal(3, steps.Count(step => step.GetProperty("operation").GetString() == "map.clear-selection"));
        Assert.Equal("view.capture", steps[^1].GetProperty("operation").GetString());
        Assert.DoesNotContain("D:\\", root.GetRawText(), StringComparison.Ordinal);
    }

    private static bool IsAcyclic(JsonElement[] steps)
    {
        var edges = steps.ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()!).ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string node)
        {
            if (visited.Contains(node)) return true;
            if (!visiting.Add(node)) return false;
            foreach (var edge in edges[node]) if (!Visit(edge)) return false;
            visiting.Remove(node);
            visited.Add(node);
            return true;
        }
        return edges.Keys.All(Visit);
    }

    private static string ReadAsset(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var directPath = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
            if (File.Exists(Path.Combine(current.FullName, "ArcGISPro.MCP.slnx")) && File.Exists(directPath))
                return File.ReadAllText(directPath);
            var path = Path.Combine(new[] { current.FullName, "ArcGISPro.MCP" }.Concat(segments).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            current = current.Parent;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
