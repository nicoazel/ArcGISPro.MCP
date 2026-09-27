using System.Diagnostics;
using System.Text.Json;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Operations.Services;

namespace ArcGISProMCP.Operations;

/// <summary>
/// Generic geoprocessing runner. Always confirmation-gated. Uses the toolbox catalog's risk tier to
/// refuse Destructive and UserCode tools in autonomous mode (<see cref="IUnattendedExecutionGate"/>),
/// to warn on the approval card and in result notices, and to statically validate dry runs.
/// </summary>
internal sealed class GeoprocessingRunOperation(ToolboxCatalog catalog, IGeoprocessingService geoprocessing) : ProOperationBase(OperationDescriptor.Create(
    "gp.run", "Run geoprocessing tool",
    "Runs an ArcGIS geoprocessing tool by toolbox-qualified name with bounded positional parameters (gp.describe 'signature' order), explicit environments, deterministic history/output flags, and complete result messages and derived values. Always requires review; autonomous mode refuses Destructive and UserCode tools. A dry run statically validates the parameters and reports the risk tier.",
    JsonSchemas.Object(
        [
            ("tool", JsonSchemas.String(1, 2048)),
            ("parameters", JsonSchemas.Array(maxItems: 256)),
            ("environments", JsonSchemas.Object([], additionalProperties: true, maxProperties: 128)),
            ("overwriteOutput", JsonSchemas.Boolean()),
            ("addOutputsToMap", JsonSchemas.Boolean()),
            ("addToHistory", JsonSchemas.Boolean()),
            ("refreshProjectItems", JsonSchemas.Boolean())
        ],
        ["tool", "parameters"]),
    risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true, executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "analysis", "data processing"], aliases: ["run tool", "spatial analysis", "buffer", "clip"],
    examples: ["Run analysis.Buffer with input, output, and distance parameters."], related: ["gp.search", "gp.describe", "gp.query", "layer.add", "view.capture"],
    typicalDuration: "seconds-to-hours",
    executesUserCode: true)), IDryRunnableOperation, IUnattendedExecutionGate, IApprovalWarningSource
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var request = GeoprocessingRequest.Parse(arguments);
        var risk = await Task.Run(() => GeoprocessingRunPolicy.Assess(catalog, request.Tool), cancellationToken).ConfigureAwait(false);
        // The tool always runs on the GP thread; history, project refresh and map output default on.
        var flags = new GeoprocessingExecutionFlags(
            AddOutputsToMap: OptionalBoolean(arguments, "addOutputsToMap", true),
            AddToHistory: OptionalBoolean(arguments, "addToHistory", true),
            RefreshProjectItems: OptionalBoolean(arguments, "refreshProjectItems", true));

        var timer = Stopwatch.StartNew();
        var result = await geoprocessing.ExecuteAsync(
            request.Tool,
            request.Parameters,
            request.Environments,
            flags,
            cancellationToken).ConfigureAwait(false);
        timer.Stop();
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var data = Json(new
        {
            tool = request.Tool,
            result.IsFailed,
            result.IsCanceled,
            result.ErrorCode,
            result.ReturnValue,
            values = result.Values?.ToArray() ?? [],
            valueTypes = result.ValueTypes?.ToArray() ?? [],
            elapsedMilliseconds = timer.ElapsedMilliseconds,
            riskTier = risk?.Tier.ToString(),
            flags = new
            {
                addOutputsToMap = OptionalBoolean(arguments, "addOutputsToMap", true),
                addToHistory = OptionalBoolean(arguments, "addToHistory", true),
                refreshProjectItems = OptionalBoolean(arguments, "refreshProjectItems", true),
                overwriteOutput = request.OverwriteOutput
            },
            messages = result.Messages.Select(message => new { type = message.Type, message.Text, message.ErrorCode }).ToArray()
        });
        OperationNotice[] userCodeNotice = UserCodeExecutionDetector.RunsUserCode(Descriptor, arguments)
            ? [new OperationNotice(
                UserCodeExecutionDetector.NoticeCode,
                "This geoprocessing request executed user-supplied Python (custom toolbox or Python expression).",
                "warning")]
            : [];
        var riskNotices = userCodeNotice.Concat(GeoprocessingRunPolicy.ResultNotices(request.Tool, risk)).ToArray();
        return result.IsFailed || result.IsCanceled
            ? OperationResult.Fail(result.IsCanceled ? "geoprocessing_cancelled" : "geoprocessing_failed",
                result.FirstErrorMessage ?? $"Geoprocessing tool '{request.Tool}' did not complete.", snapshot.Revision) with
            { Data = data, Notices = [.. riskNotices] }
            : OperationResult.Ok(
                data,
                snapshot.Revision,
                riskNotices.Concat(result.Messages
                    .Where(message => message.Type == GeoprocessingMessageTypes.Warning)
                    .Select(message => new OperationNotice("geoprocessing_warning", message.Text, "warning"))));
    }

    /// <summary>Static validation plus tier/confirmation/user-code flags. Never touches ArcGIS.</summary>
    public async Task<OperationResult> DryRunAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var revision = (await context.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Revision;
        GeoprocessingRequest request;
        try
        {
            request = GeoprocessingRequest.Parse(arguments);
        }
        catch (ArgumentException exception)
        {
            return OperationResult.Fail("invalid_arguments", exception.Message, revision);
        }

        var autonomous = context.Confirmation is IAutonomousExecutionPolicy { AllowsUnattendedRiskyOperations: true };
        var userCode = UserCodeExecutionDetector.RunsUserCode(Descriptor, arguments);
        var report = await Task.Run(() => GeoprocessingRunPolicy.DryRun(
            catalog, request.Tool, request.Parameters, userCode, requiresConfirmation: true, autonomous), cancellationToken).ConfigureAwait(false);
        var data = GeoprocessingJson.Serialize(new
        {
            report.Valid,
            dryRun = true,
            operation = Descriptor.Id,
            tool = request.Tool,
            report.Validation.Validated,
            toolSummary = report.Validation.ToolSummary is { } summary ? GeoprocessingJson.Summary(summary) : null,
            report.RiskTier,
            mutatesInput = report.Risk?.MutatesInput ?? false,
            report.ExecutesUserCode,
            consumesCredits = report.Risk?.ConsumesCredits ?? false,
            riskReasons = report.Risk?.Reasons ?? [],
            report.RequiresConfirmation,
            report.AutonomousMode,
            report.WouldBeRefused,
            unattendedRefusal = report.UnattendedRefusal,
            report.ApprovalWarning,
            issues = report.Validation.Issues,
            flags = new
            {
                addOutputsToMap = OptionalBoolean(arguments, "addOutputsToMap", true),
                addToHistory = OptionalBoolean(arguments, "addToHistory", true),
                refreshProjectItems = OptionalBoolean(arguments, "refreshProjectItems", true),
                overwriteOutput = request.OverwriteOutput
            },
            workspaceRevision = revision
        });
        return OperationResult.Ok(data, revision);
    }

    /// <summary>In autonomous mode, Destructive and UserCode tools (and detected user code) need a person.</summary>
    public async ValueTask<OperationRefusal?> CheckUnattendedAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        GeoprocessingRequest request;
        try
        {
            request = GeoprocessingRequest.Parse(arguments);
        }
        catch (ArgumentException)
        {
            // Malformed requests fail in ExecuteCoreAsync with the parse error.
            return null;
        }

        var userCode = UserCodeExecutionDetector.RunsUserCode(Descriptor, arguments);
        var risk = await Task.Run(() => GeoprocessingRunPolicy.Assess(catalog, request.Tool), cancellationToken).ConfigureAwait(false);
        return GeoprocessingRunPolicy.UnattendedRefusal(request.Tool, risk, userCode);
    }

    /// <summary>User-code warning plus "modifies/deletes input data in place" / "consumes ArcGIS Online credits".</summary>
    public string? GetApprovalWarning(JsonElement arguments)
    {
        var userCode = UserCodeExecutionDetector.GetWarning(Descriptor, arguments);
        string? risk = null;
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
        {
            try
            {
                risk = GeoprocessingRunPolicy.ApprovalWarning(GeoprocessingRunPolicy.Assess(catalog, tool.GetString()));
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Trace.TraceWarning("Geoprocessing approval warning unavailable: {0}", exception.Message);
            }
        }

        return (userCode, risk) switch
        {
            (null, null) => null,
            ({ } code, null) => code,
            (null, { } data) => data,
            ({ } code, { } data) => $"{data} {code}"
        };
    }
}

internal sealed record GeoprocessingRequest(
    string Tool,
    string[] Parameters,
    KeyValuePair<string, string>[] Environments,
    bool? OverwriteOutput)
{
    private const int MaximumParameterCount = 256;
    private const int MaximumEnvironmentCount = 128;
    private const int MaximumValueLength = 32_768;

    internal static GeoprocessingRequest Parse(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("tool", out var toolElement) || toolElement.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Argument 'tool' is required.", nameof(arguments));
        var tool = (toolElement.GetString() ?? string.Empty).Trim();
        if (tool.Length is 0 or > 2048 || tool.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("tool must be a single nonempty toolbox-qualified name or toolbox path of at most 2048 characters.", nameof(arguments));

        if (!arguments.TryGetProperty("parameters", out var parametersElement) || parametersElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("parameters must be a JSON array.", nameof(arguments));
        var parameterElements = parametersElement.EnumerateArray().ToArray();
        if (parameterElements.Length > MaximumParameterCount)
            throw new ArgumentException($"parameters cannot contain more than {MaximumParameterCount} values.", nameof(arguments));
        var parameters = parameterElements.Select(ToGpValue).ToArray();

        var environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (arguments.TryGetProperty("environments", out var environmentsElement))
        {
            if (environmentsElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("environments must be a JSON object.", nameof(arguments));
            foreach (var property in environmentsElement.EnumerateObject())
            {
                if (environments.Count >= MaximumEnvironmentCount)
                    throw new ArgumentException($"environments cannot contain more than {MaximumEnvironmentCount} values.", nameof(arguments));
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 128)
                    throw new ArgumentException("environment names must contain 1 to 128 characters.", nameof(arguments));
                environments.Add(property.Name, ToGpValue(property.Value));
            }
        }

        bool? overwriteOutput = null;
        if (arguments.TryGetProperty("overwriteOutput", out var overwriteElement))
        {
            if (overwriteElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("overwriteOutput must be a boolean.", nameof(arguments));
            overwriteOutput = overwriteElement.GetBoolean();
            environments["overwriteoutput"] = overwriteOutput.Value ? "true" : "false";
        }

        return new GeoprocessingRequest(tool, parameters, environments.ToArray(), overwriteOutput);
    }

    internal static string ToGpValue(JsonElement element)
    {
        var value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => "#",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Array => string.Join(";", element.EnumerateArray().Select(ToGpValue)),
            _ => element.GetRawText()
        };
        if (value.Length > MaximumValueLength)
            throw new ArgumentException($"A geoprocessing parameter or environment value exceeds {MaximumValueLength} characters.", nameof(element));
        return value;
    }
}
