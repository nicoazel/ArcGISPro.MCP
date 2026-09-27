using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Server.Resources;
using ArcGISProMCP.Server.Skills;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server.Prompts;

/// <summary>
/// MCP prompts generated from bundled skills (server-local, always available) and saved workflows
/// (from the ArcGIS Pro add-in, listed only while a host answers). Both are served by the
/// prompts/list and prompts/get handlers so the set stays dynamic without a static collection.
/// </summary>
public static class ArcGisPrompts
{
    /// <summary>Prompt name prefix for a bundled skill: <c>skill.&lt;skillId&gt;</c>.</summary>
    public const string SkillPrefix = "skill.";

    /// <summary>Prompt name prefix for a saved workflow: <c>run.&lt;workflowId&gt;[@version]</c>.</summary>
    public const string WorkflowPrefix = "run.";

    private const string GoalArgument = "goal";
    private const int MaxListedWorkflows = 50;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ParameterOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async ValueTask<ListPromptsResult> ListAsync(
        RequestContext<ListPromptsRequestParams> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prompts = new List<Prompt>();
        foreach (var skill in await SkillCatalog.LoadAsync(cancellationToken).ConfigureAwait(false))
            prompts.Add(SkillPrompt(skill));

        var bridge = request.Services?.GetService<IBridgeClient>();
        if (bridge is not null)
            prompts.AddRange(await ListWorkflowPromptsAsync(bridge, cancellationToken).ConfigureAwait(false));

        return new ListPromptsResult { Prompts = prompts };
    }

    public static async ValueTask<GetPromptResult> GetAsync(
        RequestContext<GetPromptRequestParams> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Params?.Name ?? throw new McpProtocolException("A prompt name is required.", McpErrorCode.InvalidParams);
        var arguments = request.Params.Arguments;

        if (name.StartsWith(SkillPrefix, StringComparison.Ordinal))
        {
            var skillId = name[SkillPrefix.Length..];
            var skill = await SkillCatalog.FindAsync(skillId, cancellationToken).ConfigureAwait(false)
                ?? throw new McpProtocolException($"Prompt '{name}' was not found.", McpErrorCode.InvalidParams);
            return SkillMessage(skill, ArgumentText(arguments, GoalArgument));
        }

        if (name.StartsWith(WorkflowPrefix, StringComparison.Ordinal))
        {
            var bridge = request.Services?.GetService<IBridgeClient>()
                ?? throw new McpProtocolException("The ArcGIS Pro bridge is not configured.", McpErrorCode.InternalError);
            var (workflowId, version) = ArcGisResources.SplitVersion(name[WorkflowPrefix.Length..]);
            WorkflowDefinition workflow;
            try
            {
                workflow = await GetWorkflowAsync(bridge, workflowId, version, cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeException exception)
            {
                var code = exception.Code.EndsWith("_not_found", StringComparison.Ordinal) ? McpErrorCode.InvalidParams : McpErrorCode.InternalError;
                throw new McpProtocolException($"{exception.Code}: {exception.Message}", exception, code);
            }
            var skills = await SkillCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
            var linked = skills.Where(skill => string.Equals(skill.WorkflowId, workflow.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            return WorkflowMessage(workflow, arguments, linked);
        }

        throw new McpProtocolException($"Prompt '{name}' was not found.", McpErrorCode.InvalidParams);
    }

    private static Prompt SkillPrompt(SkillManifest skill) => new()
    {
        Name = SkillPrefix + skill.Id,
        Title = skill.Title,
        Description = skill.Summary,
        Arguments =
        [
            new PromptArgument
            {
                Name = GoalArgument,
                Description = "Optional description of what you want to achieve with this skill.",
                Required = false
            }
        ]
    };

    private static async Task<IReadOnlyList<Prompt>> ListWorkflowPromptsAsync(IBridgeClient bridge, CancellationToken cancellationToken)
    {
        JsonElement list;
        try
        {
            list = await bridge.CallAsync("workflow.list", null, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException)
        {
            // No ArcGIS Pro host is answering: skills remain available as prompts.
            return [];
        }
        if (list.ValueKind != JsonValueKind.Array) return [];

        var prompts = new List<Prompt>();
        foreach (var item in list.EnumerateArray().Take(MaxListedWorkflows))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = String(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var prompt = new Prompt
            {
                Name = WorkflowPrefix + id,
                Title = String(item, "title"),
                Description = String(item, "summary")
            };
            try
            {
                // workflow.list omits parameters; the definition supplies the prompt arguments.
                var workflow = await GetWorkflowAsync(bridge, id, null, cancellationToken).ConfigureAwait(false);
                prompt.Arguments = [.. workflow.Parameters.Select(parameter => new PromptArgument
                {
                    Name = parameter.Name,
                    Description = ParameterDescription(parameter),
                    Required = parameter.Required && parameter.DefaultValue is null
                })];
            }
            catch (Exception exception) when (exception is BridgeException or JsonException)
            {
                // List the workflow without arguments rather than hiding it; prompts/get reports the failure.
            }
            prompts.Add(prompt);
        }
        return prompts;
    }

    private static async Task<WorkflowDefinition> GetWorkflowAsync(
        IBridgeClient bridge,
        string workflowId,
        string? version,
        CancellationToken cancellationToken)
    {
        var element = await bridge.CallAsync("workflow.get", new { workflowId, version }, cancellationToken).ConfigureAwait(false);
        return element.Deserialize<WorkflowDefinition>(WireOptions)
            ?? throw new BridgeException("invalid_workflow", $"Workflow '{workflowId}' could not be parsed.");
    }

    private static GetPromptResult SkillMessage(SkillManifest skill, string? goal)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Use the ArcGIS Pro skill \"{skill.Title}\" ({skill.Id} {skill.Version}).\n{skill.Summary}\n");
        if (!string.IsNullOrWhiteSpace(goal))
            text.Append(CultureInfo.InvariantCulture, $"\nGoal: {goal}\n");
        AppendList(text, "Preconditions", skill.Preconditions);
        text.Append("\nSteps:\n");
        text.Append("1. Call system_get_state; confirm the preconditions and note the current workspace revision.\n");
        text.Append(CultureInfo.InvariantCulture, $"2. Call workflow_get with workflowId \"{skill.WorkflowId}\" to review its parameters and steps; ask for any required parameter values you do not have.\n");
        text.Append(CultureInfo.InvariantCulture, $"3. Call workflow_run with workflowId \"{skill.WorkflowId}\", the returned version, the parameters and expectedRevision set to that revision. If you adjust individual steps instead, only use these registry operations: {string.Join(", ", skill.AllowedOperations.Order(StringComparer.Ordinal))}.\n");
        text.Append("4. Review the per-step observations (read arcgis://resource/ handles with resource_read) and verify the visual checks below before reporting success.\n");
        AppendList(text, "Visual checks", skill.VisualChecks);
        AppendList(text, "Recovery guidance", skill.RecoveryGuidance);
        return Result(skill.Summary, text.ToString());
    }

    private static GetPromptResult WorkflowMessage(
        WorkflowDefinition workflow,
        IDictionary<string, JsonElement>? arguments,
        IReadOnlyList<SkillManifest> linkedSkills)
    {
        var parameters = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var parameter in workflow.Parameters)
        {
            if (arguments is not null && arguments.TryGetValue(parameter.Name, out var value) && !IsBlank(value))
                parameters[parameter.Name] = ParameterValue(parameter, value);
            else if (parameter.Required && parameter.DefaultValue is null)
                missing.Add(parameter.Name);
        }
        if (missing.Count > 0)
            throw new McpProtocolException(
                $"Workflow '{workflow.Id}' requires values for: {string.Join(", ", missing)}.",
                McpErrorCode.InvalidParams);

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Run the ArcGIS Pro workflow \"{workflow.Title}\" ({workflow.Id}@{workflow.Version}).\n{workflow.Summary}\n");
        text.Append("\nSteps:\n");
        text.Append("1. Call system_get_state and note the current workspace revision");
        if (!workflow.RequiredCapabilities.IsEmpty)
            text.Append(CultureInfo.InvariantCulture, $"; confirm these capabilities are available: {string.Join(", ", workflow.RequiredCapabilities.Order(StringComparer.Ordinal))}");
        text.Append(".\n");
        text.Append(CultureInfo.InvariantCulture, $"2. Call workflow_get with workflowId \"{workflow.Id}\" and version \"{workflow.Version}\" to review the steps before running.\n");
        text.Append(CultureInfo.InvariantCulture, $"3. Call workflow_run with workflowId \"{workflow.Id}\", version \"{workflow.Version}\", expectedRevision set to that revision, and these parameters:\n");
        text.Append("```json\n").Append(JsonSerializer.Serialize(parameters, ParameterOptions)).Append("\n```\n");
        text.Append("4. Review the final result and per-step observations; read arcgis://resource/ handles with resource_read and confirm the result matches the intent before reporting success.\n");
        foreach (var skill in linkedSkills)
        {
            AppendList(text, $"Visual checks ({skill.Title})", skill.VisualChecks);
            AppendList(text, $"Recovery guidance ({skill.Title})", skill.RecoveryGuidance);
        }
        return Result(workflow.Summary, text.ToString());
    }

    private static GetPromptResult Result(string? description, string text) => new()
    {
        Description = description,
        Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }]
    };

    private static void AppendList(StringBuilder text, string heading, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        text.Append(CultureInfo.InvariantCulture, $"\n{heading}:\n");
        foreach (var item in list)
            text.Append("- ").Append(item).Append('\n');
    }

    private static string ParameterDescription(WorkflowParameter parameter)
    {
        var description = string.IsNullOrWhiteSpace(parameter.Description) ? parameter.Name : parameter.Description;
        description += $" ({parameter.Type})";
        if (parameter.DefaultValue is { } defaultValue)
            description += $" Default: {defaultValue.GetRawText()}.";
        return description;
    }

    /// <summary>Prompt arguments arrive as strings; non-text workflow parameters accept JSON literals.</summary>
    private static JsonElement ParameterValue(WorkflowParameter parameter, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return value;
        if (parameter.Type is "string" or "path") return value;
        try
        {
            using var document = JsonDocument.Parse(value.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static bool IsBlank(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static string? ArgumentText(IDictionary<string, JsonElement>? arguments, string name) =>
        arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? String(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }
}
