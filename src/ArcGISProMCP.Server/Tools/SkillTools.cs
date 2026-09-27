using System.ComponentModel;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Server.Skills;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Tools;

/// <summary>One skill_search hit.</summary>
public sealed record SkillSearchHit(
    string Id,
    string Version,
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    string WorkflowId,
    [property: Description("Number of query terms found in the title, summary or tags.")] int Score);

[McpServerToolType]
public sealed class SkillTools
{
    [McpServerTool(Name = "skill_search", Title = "Find ArcGIS skills",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<SkillSearchHit[]>))]
    [Description("Finds compact bundled skill summaries by intent; use skill_get for preconditions, allowed operations, visual checks, recovery guidance, and the linked reusable workflow.")]
    public static async Task<CallToolResult> Search(string query = "", CancellationToken cancellationToken = default)
    {
        var skills = await BundledSkills.LoadAsync(cancellationToken).ConfigureAwait(false);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        SkillSearchHit[] matches = [.. skills
            .Select(skill => new SkillSearchHit(
                skill.Id,
                skill.Version,
                skill.Title,
                skill.Summary,
                [.. skill.Tags],
                skill.WorkflowId,
                terms.Count(term => $"{skill.Title} {skill.Summary} {string.Join(' ', skill.Tags)}".Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Where(skill => terms.Length == 0 || skill.Score > 0)
            .OrderByDescending(skill => skill.Score)
            .Take(20)];
        return ToolResults.Success(matches);
    }

    [McpServerTool(Name = "skill_get", Title = "Read ArcGIS skill guidance",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ToolEnvelope<SkillManifest>))]
    [Description("Returns one bundled skill manifest. Skill text is reusable guidance; live registry validation and operation permissions still apply.")]
    public static async Task<CallToolResult> Get(string skillId, CancellationToken cancellationToken = default)
    {
        var skill = await BundledSkills.FindAsync(skillId, cancellationToken).ConfigureAwait(false);
        return skill is null
            ? ToolResults.Failure<SkillManifest>(new ToolError("skill_not_found", $"Skill '{skillId}' was not found. Use skill_search to list bundled skills.", false, null))
            : ToolResults.Success(skill);
    }
}
