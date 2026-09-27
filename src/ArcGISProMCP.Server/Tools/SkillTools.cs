using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Server.Skills;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Tools;

[McpServerToolType]
public sealed class SkillTools
{
    private static readonly JsonSerializerOptions JsonOptions = SkillCatalog.JsonOptions;

    [McpServerTool(Name = "skill_search", Title = "Find ArcGIS skills", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Finds compact bundled skill summaries by intent; use skill_get for preconditions, allowed operations, visual checks, recovery guidance, and the linked reusable workflow.")]
    public static async Task<string> Search(string query = "", CancellationToken cancellationToken = default)
    {
        var skills = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = skills.Select(skill => new
        {
            skill.Id,
            skill.Version,
            skill.Title,
            skill.Summary,
            skill.Tags,
            skill.WorkflowId,
            score = terms.Count(term => $"{skill.Title} {skill.Summary} {string.Join(' ', skill.Tags)}".Contains(term, StringComparison.OrdinalIgnoreCase))
        }).Where(skill => terms.Length == 0 || skill.score > 0).OrderByDescending(skill => skill.score).Take(20);
        return JsonSerializer.Serialize(matches, JsonOptions);
    }

    [McpServerTool(Name = "skill_get", Title = "Read ArcGIS skill guidance", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Returns one bundled skill manifest. Skill text is reusable guidance; live registry validation and operation permissions still apply.")]
    public static async Task<string> Get(string skillId, CancellationToken cancellationToken = default)
    {
        var skills = await LoadAsync(cancellationToken).ConfigureAwait(false);
        var skill = skills.FirstOrDefault(item => string.Equals(item.Id, skillId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Skill '{skillId}' was not found.", nameof(skillId));
        return JsonSerializer.Serialize(skill, JsonOptions);
    }

    private static Task<IReadOnlyList<SkillManifest>> LoadAsync(CancellationToken cancellationToken) =>
        SkillCatalog.LoadAsync(cancellationToken);
}
