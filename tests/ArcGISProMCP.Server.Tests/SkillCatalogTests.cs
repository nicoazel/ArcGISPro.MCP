using ArcGISProMCP.Server.Skills;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcgis-mcp-skills-" + Guid.NewGuid().ToString("N"));

    public SkillCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Manifests_without_an_operation_allowlist_are_not_exposed()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "allowed.skill.json"), Manifest("allowed", """["project.get"]"""), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "empty.skill.json"), Manifest("empty", "[]"), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "missing.skill.json"), Manifest("missing", null), TestContext.Current.CancellationToken);

        var skills = await SkillCatalog.LoadAsync(_root, TestContext.Current.CancellationToken);

        Assert.Equal(["allowed"], skills.Select(skill => skill.Id));
    }

    [Fact]
    public async Task Missing_root_yields_no_skills()
    {
        var skills = await SkillCatalog.LoadAsync(Path.Combine(_root, "absent"), TestContext.Current.CancellationToken);

        Assert.Empty(skills);
    }

    [Fact]
    public async Task Bundled_skills_are_copied_next_to_the_test_binaries()
    {
        var skills = await SkillCatalog.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Contains(skills, skill => skill.Id == "arcgis.cartography.master-plan");
    }

    private static string Manifest(string id, string? allowedOperations) => $$"""
        {
          "id": "{{id}}",
          "version": "1.0.0",
          "title": "{{id}}",
          "summary": "Test skill.",
          "tags": [],
          "requiredCapabilities": [],
          {{(allowedOperations is null ? "" : $"\"allowedOperations\": {allowedOperations},")}}
          "preconditions": [],
          "visualChecks": [],
          "recoveryGuidance": [],
          "workflowId": "workflow.test"
        }
        """;
}
