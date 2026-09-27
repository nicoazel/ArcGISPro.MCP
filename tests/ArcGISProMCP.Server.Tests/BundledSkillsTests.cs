using ArcGISProMCP.Server.Skills;
using Xunit;

namespace ArcGISProMCP.Server.Tests;

// Manifest validation is covered by the Core SkillCatalog tests; these check the server's bundled root.
public sealed class BundledSkillsTests
{
    [Fact]
    public async Task Bundled_skills_are_copied_next_to_the_test_binaries()
    {
        var skills = await BundledSkills.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Contains(skills, skill => skill.Id == "arcgis.cartography.master-plan");
    }

    [Fact]
    public async Task Find_returns_a_bundled_skill_by_exact_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.NotNull(await BundledSkills.FindAsync("arcgis.cartography.master-plan", cancellationToken));
        Assert.Null(await BundledSkills.FindAsync("ARCGIS.CARTOGRAPHY.MASTER-PLAN", cancellationToken));
    }
}
