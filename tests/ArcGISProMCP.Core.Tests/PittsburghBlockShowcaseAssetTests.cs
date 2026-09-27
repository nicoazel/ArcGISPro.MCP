using System.Text.Json;
using Xunit;

namespace ArcGISProMCP.Core.Tests;

public sealed class PittsburghBlockShowcaseAssetTests
{
    [Fact]
    public void Workflow_is_a_self_contained_illustrative_three_frame_showcase()
    {
        using var document = JsonDocument.Parse(ReadAsset("workflows", "pittsburgh-block-mixed-use-showcase.workflow.json"));
        var root = document.RootElement;
        var source = root.GetRawText();
        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        var ids = steps.Select(step => step.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var parameters = root.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal("workflow.pittsburgh-block-mixed-use-showcase", root.GetProperty("id").GetString());
        Assert.Equal("1.1.0", root.GetProperty("version").GetString());
        Assert.Contains("illustrative", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EPSG:2272", source, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\", source, StringComparison.Ordinal);
        Assert.DoesNotContain("rhino", source, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, parameters.Count);
        Assert.True(new[] { "massingSource", "programSource", "publicRealmSource", "streetsSource", "blocksSource", "contextSource" }
            .All(parameters.Contains));

        Assert.Equal(3, steps.Count(step => Operation(step) == "map.ensure"));
        Assert.Equal(3, steps.Count(step => Operation(step) == "layout.add-map-frame"));
        Assert.Equal(2, steps.Count(step => Operation(step) == "label.configure"));
        Assert.Equal(3, steps.Count(step => Operation(step) == "layout.ensure-surround"));
        Assert.Equal(3, steps.Count(step => Operation(step) == "layer.set-appearance"));
        Assert.Equal(3, steps.Count(step => Operation(step) == "table.statistics"));
        Assert.Equal("view.capture", Operation(steps[^1]));
        Assert.DoesNotContain(steps, step => Operation(step) == "project.save");
        Assert.Equal("relative-to-ground", Step(steps, "scene-ground-relative")
            .GetProperty("arguments").GetProperty("mode").GetString());
        Assert.Contains("<dyn type=\"project\"", Step(steps, "dynamic-status")
            .GetProperty("arguments").GetProperty("text").GetString(), StringComparison.Ordinal);

        var programValues = Step(steps, "style-program").GetProperty("arguments")
            .GetProperty("classes").EnumerateArray()
            .Select(item => item.GetProperty("value").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(new[] { "Housing", "Retail", "Hospitality", "Workspace", "Civic" }.All(programValues.Contains));

        foreach (var step in steps)
            foreach (var dependency in step.GetProperty("dependsOn").EnumerateArray())
                Assert.Contains(dependency.GetString()!, ids);
        Assert.True(IsAcyclic(steps));
    }

    [Fact]
    public void Generator_is_epsg_2272_guarded_and_refuses_unrequested_replacement()
    {
        var source = ReadAsset("tools", "create-pittsburgh-block-showcase.py");

        Assert.Contains("EPSG_PENNSYLVANIA_SOUTH = 2272", source, StringComparison.Ordinal);
        Assert.Contains("--template-aprx", source, StringComparison.Ordinal);
        Assert.Contains("--parcels-source", source, StringComparison.Ordinal);
        Assert.Contains("--buildings-source", source, StringComparison.Ordinal);
        Assert.Contains("--streets-source", source, StringComparison.Ordinal);
        Assert.Contains("--replace", source, StringComparison.Ordinal);
        Assert.Contains("PittsburghShowcaseMassing", source, StringComparison.Ordinal);
        Assert.Contains("PittsburghShowcasePublicRealm", source, StringComparison.Ordinal);
        Assert.Contains("PittsburghShowcaseStreets", source, StringComparison.Ordinal);
        Assert.Contains("$feature.height_ft", source, StringComparison.Ordinal);
        Assert.Contains("multipatch", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArcGISProject(\"CURRENT\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("saveACopy", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".save()", source, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Step(JsonElement[] steps, string id) =>
        Assert.Single(steps, step => step.GetProperty("id").GetString() == id);

    private static string? Operation(JsonElement step) => step.GetProperty("operation").GetString();

    private static bool IsAcyclic(JsonElement[] steps)
    {
        var edges = steps.ToDictionary(
            step => step.GetProperty("id").GetString()!,
            step => step.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            foreach (var dependency in edges[id]) if (!Visit(dependency)) return false;
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
        return edges.Keys.All(Visit);
    }

    private static string ReadAsset(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(Path.Combine(directory.FullName, "ArcGISPro.MCP.slnx")) && File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
