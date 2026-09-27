using System.Text.Json;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.Server.Skills;

/// <summary>
/// Loads the bundled, server-local skill manifests shared by the skill tools, resources and prompts.
/// </summary>
public static class SkillCatalog
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Directory holding <c>*.skill.json</c> manifests next to the server binary.</summary>
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "skills");

    public static Task<IReadOnlyList<SkillManifest>> LoadAsync(CancellationToken cancellationToken) =>
        LoadAsync(DefaultRoot, cancellationToken);

    public static async Task<IReadOnlyList<SkillManifest>> LoadAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return [];
        var skills = new List<SkillManifest>();
        foreach (var path in Directory.EnumerateFiles(root, "*.skill.json").Take(100))
        {
            await using var stream = File.OpenRead(path);
            var skill = await JsonSerializer.DeserializeAsync<SkillManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (skill is null) continue;
            // A skill must declare an explicit, nonempty operation allowlist; manifests that
            // omit it would read as "anything goes" and are not exposed.
            if (skill.AllowedOperations is null || skill.AllowedOperations.IsEmpty)
            {
                System.Diagnostics.Trace.TraceWarning("Skipping skill manifest '{0}': allowedOperations is missing or empty.", path);
                continue;
            }
            skills.Add(skill);
        }
        return skills;
    }

    public static async Task<SkillManifest?> FindAsync(string skillId, CancellationToken cancellationToken)
    {
        var skills = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal));
    }
}
