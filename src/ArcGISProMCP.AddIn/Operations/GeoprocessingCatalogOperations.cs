using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations;

namespace ArcGISProMCP.AddIn.Operations;

/// <summary>Serialization shared by the geoprocessing catalog operations (camelCase, enums as names).</summary>
internal static class GeoprocessingJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static JsonElement Serialize(object value) => JsonSerializer.SerializeToElement(value, Options);

    internal static object Summary(GpToolSummary tool) => new
    {
        tool.ExecutionName,
        tool.Name,
        tool.DisplayName,
        tool.Summary,
        tool.Toolbox,
        tool.ToolboxAlias,
        tool.Toolset,
        tool.ToolType,
        tool.RiskTier,
        tool.Deprecated,
        tool.IsSystem
    };

    internal static object Risk(GpRiskAssessment risk) => new
    {
        risk.Tier,
        risk.MutatesInput,
        risk.ExecutesUserCode,
        risk.AcceptsPythonExpression,
        risk.ConsumesCredits,
        risk.Reasons
    };

    /// <summary>Catalog-level notices, so an empty catalog is explained rather than silently returning nothing.</summary>
    internal static IEnumerable<OperationNotice> CatalogNotices(ToolboxCatalog catalog) =>
        catalog.ToolCount == 0
            ? [new OperationNotice("gp_catalog_empty",
                "The toolbox catalog is empty. " + string.Join(" ", catalog.Warnings.Take(3)), "warning")]
            : [];
}

internal sealed class GeoprocessingSearchOperation(ToolboxCatalog catalog) : ProOperationBase(OperationDescriptor.Create(
    "gp.search", "Search geoprocessing tools",
    "Searches the installed ArcGIS Pro system toolboxes by name, display name, keywords and summary. Returns execution names (alias.ToolName) for gp.describe and gp.run, with each tool's risk tier. Reads toolbox metadata only; never runs a tool.",
    JsonSchemas.Object(
        [
            ("query", JsonSchemas.String(maxLength: 512, description: "Words to match; empty lists tools alphabetically.")),
            ("limit", JsonSchemas.Integer(1, 100, "Maximum results (default 20)."))
        ]),
    executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "toolbox", "tool", "search", "discover"], aliases: ["find tool", "which tool", "list tools"],
    examples: ["Search 'buffer' to find analysis.Buffer and its risk tier."], related: ["gp.describe", "gp.run", "gp.query"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var query = OptionalString(arguments, "query");
        var limit = arguments.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var requested)
            ? Math.Clamp(requested, 1, 100)
            : 20;
        var hits = await Task.Run(() => catalog.Search(query, limit), cancellationToken).ConfigureAwait(false);
        var notices = await Task.Run(() => GeoprocessingJson.CatalogNotices(catalog).ToArray(), cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var data = GeoprocessingJson.Serialize(new
        {
            query,
            count = hits.Count,
            catalogToolCount = catalog.ToolCount,
            tools = hits.Select(hit => new { tool = GeoprocessingJson.Summary(hit.Tool), hit.Score, hit.MatchedTerms }).ToArray()
        });
        return OperationResult.Ok(data, snapshot.Revision, notices);
    }
}

internal sealed class GeoprocessingDescribeOperation(ToolboxCatalog catalog) : ProOperationBase(OperationDescriptor.Create(
    "gp.describe", "Describe geoprocessing tool",
    "Describes one system geoprocessing tool from its toolbox metadata: parameters (data types, required/optional/derived, direction, defaults, domains such as coded values and ranges), the positional signature gp.run expects, environments, attributes and the risk tier with its reasons. Never runs the tool.",
    JsonSchemas.Object(
        [("tool", JsonSchemas.String(1, 512, description: "Execution name such as 'analysis.Buffer' (the 'Buffer_analysis' form is also accepted)."))],
        ["tool"]),
    executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "toolbox", "parameters", "signature", "describe"], aliases: ["tool parameters", "tool help", "tool syntax"],
    examples: ["Describe analysis.Buffer before building gp.run parameters."], related: ["gp.search", "gp.run", "gp.query"]))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var tool = RequiredString(arguments, "tool").Trim();
        var description = await Task.Run(() => catalog.Describe(tool), cancellationToken).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (description is null)
        {
            var suggestions = await Task.Run(() => catalog.Search(tool, 5).Select(hit => hit.Tool.ExecutionName).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var hint = suggestions.Length == 0 ? " Use gp.search to find the execution name." : $" Did you mean: {string.Join(", ", suggestions)}?";
            return OperationResult.Fail("tool_not_found", $"Tool '{tool}' is not in the toolbox catalog.{hint}", snapshot.Revision) with
            {
                Data = GeoprocessingJson.Serialize(new { tool, suggestions }),
                Notices = [.. GeoprocessingJson.CatalogNotices(catalog)]
            };
        }

        var positional = description.PositionalParameters.ToArray();
        var data = GeoprocessingJson.Serialize(new
        {
            tool = GeoprocessingJson.Summary(description.Tool),
            description.Description,
            signature = positional.Select(parameter => parameter.Name).ToArray(),
            usage = "Pass gp.run/gp.query 'parameters' positionally in 'signature' order (parameter 'position'), not dialog order. " +
                "Derived outputs are never passed. Use null or '#' to leave an optional value unset; a JSON array becomes a ';'-separated multivalue.",
            parameters = description.Parameters.OrderBy(parameter => parameter.DisplayOrder).ToArray(),
            description.Environments,
            description.Attributes,
            description.Keywords,
            risk = GeoprocessingJson.Risk(description.Risk)
        });
        return OperationResult.Ok(data, snapshot.Revision);
    }
}

/// <summary>
/// Runs one allowlisted read-only system tool (GeoprocessingRiskPolicy.ReadOnlyQueryTools) without
/// review. The name must be a plain alias.Name that resolves to a system toolbox, parameters are
/// statically validated first, and outputs are never added to the map, overwritten or added to history.
/// </summary>
internal sealed class GeoprocessingQueryOperation(ToolboxCatalog catalog) : ProOperationBase(OperationDescriptor.Create(
    "gp.query", "Run read-only geoprocessing query",
    "Runs one read-only system geoprocessing query tool (management.GetCount, management.GetRasterProperties, management.GetCellValue) without review and returns its result values and messages. Outputs are not added to the map, nothing is overwritten and nothing is written to geoprocessing history. Use gp.run for every other tool.",
    JsonSchemas.Object(
        [
            ("tool", JsonSchemas.String(3, 128, pattern: "^[a-z0-9]+\\.[A-Za-z0-9]+$", description: "Allowlisted execution name, for example 'management.GetCount'.")),
            ("parameters", JsonSchemas.Array(maxItems: 32, description: "Positional values in gp.describe 'signature' order; null or '#' leaves a value unset."))
        ],
        ["tool", "parameters"]),
    executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "count", "raster", "query", "read-only"], aliases: ["get count", "row count", "raster properties", "cell value"],
    examples: ["Run management.GetCount on a layer to count its rows."], related: ["gp.describe", "gp.run", "table.statistics"],
    typicalDuration: "seconds"))
{
    private const int MaximumParameterCount = 32;

    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var toolName = RequiredString(arguments, "tool");
        if (!arguments.TryGetProperty("parameters", out var parametersElement) || parametersElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("parameters must be a JSON array.", nameof(arguments));
        var parameterElements = parametersElement.EnumerateArray().ToArray();
        if (parameterElements.Length > MaximumParameterCount)
            throw new ArgumentException($"parameters cannot contain more than {MaximumParameterCount} values.", nameof(arguments));
        var parameters = parameterElements.Select(GeoprocessingRequest.ToGpValue).ToArray();

        var resolution = await Task.Run(() => GeoprocessingRunPolicy.ResolveQueryTool(catalog, toolName), cancellationToken).ConfigureAwait(false);
        var revision = (await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Revision;
        if (!resolution.Succeeded)
            return OperationResult.Fail(resolution.ErrorCode!, resolution.Message!, revision);

        var description = resolution.Tool!;
        var validation = GpStaticValidator.Validate(description, parameters);
        if (!validation.IsValid)
        {
            return OperationResult.Fail("invalid_parameters",
                string.Join(" ", validation.Issues.Where(issue => issue.Severity == GpValidationSeverity.Error).Select(issue => issue.Message)),
                revision) with
            { Data = GeoprocessingJson.Serialize(new { tool = description.Tool.ExecutionName, validation.Issues }) };
        }

        // Only GPThread: no AddOutputsToMap, AddToHistory or RefreshProjectItems, and overwrite explicitly off.
        KeyValuePair<string, string>[] environments = [new("overwriteoutput", "false")];
        var timer = Stopwatch.StartNew();
        var result = await Geoprocessing.ExecuteToolAsync(
            description.Tool.ExecutionName,
            parameters,
            environments,
            cancellationToken,
            null,
            GPExecuteToolFlags.GPThread).ConfigureAwait(false);
        timer.Stop();
        var data = GeoprocessingJson.Serialize(new
        {
            tool = description.Tool.ExecutionName,
            result.IsFailed,
            result.IsCanceled,
            result.ErrorCode,
            result.ReturnValue,
            values = result.Values?.ToArray() ?? [],
            valueTypes = result.ValueTypes?.ToArray() ?? [],
            elapsedMilliseconds = timer.ElapsedMilliseconds,
            flags = new { addOutputsToMap = false, addToHistory = false, refreshProjectItems = false, overwriteOutput = false },
            messages = result.Messages.Select(message => new { type = message.Type.ToString(), message.Text, message.ErrorCode }).ToArray()
        });
        return result.IsFailed || result.IsCanceled
            ? OperationResult.Fail(result.IsCanceled ? "geoprocessing_cancelled" : "geoprocessing_failed",
                result.ErrorMessages.FirstOrDefault()?.Text ?? $"Geoprocessing tool '{description.Tool.ExecutionName}' did not complete.", revision) with
            { Data = data }
            : OperationResult.Ok(
                data,
                revision,
                result.Messages
                    .Where(message => message.Type == GPMessageType.Warning)
                    .Select(message => new OperationNotice("geoprocessing_warning", message.Text, "warning")));
    }
}
