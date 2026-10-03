using System.Text.Json;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.Core.Tests;

/// <summary>
/// layout.add-map-frame, layout.ensure-surround and layout.set-text refuse elements off the page
/// (element_outside_page). Every bundled workflow must still place its frames, surrounds and text on
/// the page its layout.ensure step creates.
/// </summary>
public sealed class BundledWorkflowLayoutTests
{
    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "workflows"), "*.workflow.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Layout_elements_lie_on_the_page(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "workflows", fileName)));
        var pages = new Dictionary<string, (double Width, double Height)>(StringComparer.OrdinalIgnoreCase);
        var checkedElements = 0;
        foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
        {
            var operation = step.GetProperty("operation").GetString();
            var arguments = step.GetProperty("arguments");
            switch (operation)
            {
                case "layout.ensure":
                    pages[arguments.GetProperty("name").GetString()!] = (Number(arguments, "width", 11), Number(arguments, "height", 8.5));
                    break;
                case "layout.add-map-frame":
                case "layout.ensure-surround":
                {
                    var (width, height) = pages[arguments.GetProperty("layout").GetString()!];
                    var defaultWidth = operation == "layout.add-map-frame" ? 10 : double.NaN;
                    var defaultHeight = operation == "layout.add-map-frame" ? 7.5 : double.NaN;
                    var box = new PageBox(Number(arguments, "x", 0.5), Number(arguments, "y", 0.5),
                        Number(arguments, "width", defaultWidth), Number(arguments, "height", defaultHeight));
                    LayoutGeometry.EnsureOnPage($"{fileName} {operation} '{arguments.GetProperty("name").GetString()}'", box, width, height, "Inch");
                    checkedElements++;
                    break;
                }
                case "layout.set-text":
                {
                    var (width, height) = pages[arguments.GetProperty("layout").GetString()!];
                    LayoutGeometry.EnsurePointOnPage($"{fileName} text '{arguments.GetProperty("name").GetString()}'",
                        Number(arguments, "x", double.NaN), Number(arguments, "y", double.NaN), width, height, "Inch");
                    checkedElements++;
                    break;
                }
            }
        }

        Assert.True(checkedElements > 0, $"{fileName} places no layout elements; drop it from this test or check the parser.");
    }

    private static double Number(JsonElement arguments, string name, double defaultValue) =>
        arguments.TryGetProperty(name, out var value) ? value.GetDouble() : defaultValue;

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "ArcGISPro.MCP.slnx"))) return current.FullName;
        }

        throw new DirectoryNotFoundException("Repository root containing ArcGISPro.MCP.slnx was not found.");
    }
}
