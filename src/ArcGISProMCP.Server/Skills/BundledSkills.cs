using System.Text.Json;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.Server.Skills;

/// <summary>
/// The skill manifests bundled next to the server binary, shared by the skill tools, resources and prompts.
/// Loading and validation are delegated to <see cref="SkillCatalog"/>.
/// </summary>
public static class BundledSkills
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Directory holding the bundled <c>*.skill.json</c> manifests.</summary>
    public static string Root => Path.Combine(AppContext.BaseDirectory, "skills");

    public static Task<IReadOnlyList<SkillManifest>> LoadAsync(CancellationToken cancellationToken) =>
        SkillCatalog.LoadAsync(Root, cancellationToken);

    public static async Task<SkillManifest?> FindAsync(string skillId, CancellationToken cancellationToken)
    {
        var skills = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal));
    }
}
