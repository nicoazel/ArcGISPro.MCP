using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArcGISProMCP.Core.Tests;

public sealed partial class PanelBindingSafetyTests
{
    private static readonly string[] ApprovalReviewProperties =
    [
        "ToolName",
        "OperationId",
        "OperationVersion",
        "Risk",
        "Summary",
        "WorkspaceRevision",
        "RequestedAtText",
        "ExpiresAtText",
        "ArgumentsPreview"
    ];

    [Fact]
    public void EveryRunTextBindingIsExplicitlyOneWay()
    {
        var document = LoadDockPaneXaml();
        var unsafeBindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Run")
            .Select(element => element.Attribute("Text")?.Value)
            .Where(value => value is not null && value.StartsWith("{Binding", StringComparison.Ordinal))
            .Where(value => !IsExplicitOneWay(value!))
            .ToArray();

        Assert.True(
            unsafeBindings.Length == 0,
            $"Run.Text defaults can write back and crash on read-only view-model properties. Add Mode=OneWay: {string.Join("; ", unsafeBindings)}");
    }

    [Fact]
    public void EveryReadOnlyTextBoxTextBindingIsExplicitlyOneWay()
    {
        var document = LoadDockPaneXaml();
        var unsafeBindings = document
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBox")
            .Where(element => string.Equals(element.Attribute("IsReadOnly")?.Value, "True", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Text")?.Value)
            .Where(value => value is not null && value.StartsWith("{Binding", StringComparison.Ordinal))
            .Where(value => !IsExplicitOneWay(value!))
            .ToArray();

        Assert.True(
            unsafeBindings.Length == 0,
            $"Read-only TextBox.Text bindings must be explicit Mode=OneWay: {string.Join("; ", unsafeBindings)}");
    }

    [Fact]
    public void ApprovalCardKeepsEveryRequiredReviewPropertyVisible()
    {
        var document = LoadDockPaneXaml();
        var bindings = document
            .Descendants()
            .Attributes()
            .Select(attribute => attribute.Value)
            .Where(value => value.StartsWith("{Binding", StringComparison.Ordinal))
            .ToArray();

        var missing = ApprovalReviewProperties
            .Where(property => !bindings.Any(binding => BindingPathPattern(property).IsMatch(binding)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"The approval card no longer exposes required immutable review fields: {string.Join(", ", missing)}");
    }

    private static bool IsExplicitOneWay(string binding) => OneWayModePattern().IsMatch(binding);

    private static XDocument LoadDockPaneXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "ArcGISProMCP.AddIn",
                "McpDockPaneView.xaml");
            if (File.Exists(candidate)) return XDocument.Load(candidate, LoadOptions.SetLineInfo);
        }

        throw new FileNotFoundException("Could not locate the shipped McpDockPaneView.xaml from the test output tree.");
    }

    private static Regex BindingPathPattern(string property) => new(
        $@"^\{{Binding\s+{Regex.Escape(property)}(?:\s*[,}}])",
        RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?:^|,)\s*Mode\s*=\s*OneWay\s*(?:,|})", RegexOptions.CultureInvariant)]
    private static partial Regex OneWayModePattern();
}
