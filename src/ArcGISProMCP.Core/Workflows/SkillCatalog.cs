using System.Text.Json;

namespace ArcGISProMCP.Core.Workflows;

/// <summary>Loads bundled skill manifests from a directory of <c>*.skill.json</c> files.</summary>
public static class SkillCatalog
{
    /// <summary>At most this many manifest files are read from the directory.</summary>
    public const int MaximumSkills = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns the manifests in <paramref name="root"/>, or none when it does not exist. A manifest
    /// must declare an explicit, non-empty <see cref="SkillManifest.AllowedOperations"/>; one that
    /// omits it would read as "anything goes", so it is skipped and not exposed.
    /// </summary>
    public static async Task<IReadOnlyList<SkillManifest>> LoadAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root)) return [];
        var skills = new List<SkillManifest>();
        foreach (var path in Directory.EnumerateFiles(root, "*.skill.json").Take(MaximumSkills))
        {
            var stream = File.OpenRead(path);
            SkillManifest? skill;
            await using (stream.ConfigureAwait(false))
            {
                skill = await JsonSerializer.DeserializeAsync<SkillManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            if (skill is null) continue;
            if (skill.AllowedOperations is null || skill.AllowedOperations.IsEmpty)
            {
                System.Diagnostics.Trace.TraceWarning("Skipping skill manifest '{0}': allowedOperations is missing or empty.", path);
                continue;
            }

            skills.Add(skill);
        }

        return skills;
    }
}
