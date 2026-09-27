using System.Diagnostics;
using System.Text.Json;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class GeoprocessingRunOperation() : ProOperationBase(OperationDescriptor.Create(
    "gp.run", "Run geoprocessing tool",
    "Runs an ArcGIS geoprocessing tool by toolbox-qualified name with bounded ordered parameters, explicit environments, deterministic history/output flags, and complete result messages and derived values.",
    JsonSchemas.ObjectSchema(
        "\"tool\": {\"type\": \"string\", \"minLength\": 1, \"maxLength\": 2048}, " +
        "\"parameters\": {\"type\": \"array\", \"maxItems\": 256}, " +
        "\"environments\": {\"type\": \"object\", \"maxProperties\": 128}, " +
        "\"overwriteOutput\": {\"type\": \"boolean\"}, " +
        "\"addOutputsToMap\": {\"type\": \"boolean\"}, " +
        "\"addToHistory\": {\"type\": \"boolean\"}, " +
        "\"refreshProjectItems\": {\"type\": \"boolean\"}",
        "tool", "parameters"),
    risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true, executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "analysis", "data processing"], aliases: ["run tool", "spatial analysis", "buffer", "clip"],
    examples: ["Run analysis.Buffer with input, output, and distance parameters."], related: ["layer.add", "view.capture"], typicalDuration: "seconds-to-hours",
    executesUserCode: true))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var request = GeoprocessingRequest.Parse(arguments);
        var flags = GPExecuteToolFlags.GPThread;
        if (OptionalBoolean(arguments, "addToHistory", true)) flags |= GPExecuteToolFlags.AddToHistory;
        if (OptionalBoolean(arguments, "refreshProjectItems", true)) flags |= GPExecuteToolFlags.RefreshProjectItems;
        if (OptionalBoolean(arguments, "addOutputsToMap", true)) flags |= GPExecuteToolFlags.AddOutputsToMap;

        var timer = Stopwatch.StartNew();
        var result = await Geoprocessing.ExecuteToolAsync(
            request.Tool,
            request.Parameters,
            request.Environments,
            cancellationToken,
            null,
            flags).ConfigureAwait(false);
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
            flags = new
            {
                addOutputsToMap = OptionalBoolean(arguments, "addOutputsToMap", true),
                addToHistory = OptionalBoolean(arguments, "addToHistory", true),
                refreshProjectItems = OptionalBoolean(arguments, "refreshProjectItems", true),
                overwriteOutput = request.OverwriteOutput
            },
            messages = result.Messages.Select(message => new { type = message.Type.ToString(), message.Text, message.ErrorCode }).ToArray()
        });
        OperationNotice[] userCodeNotice = UserCodeExecutionDetector.RunsUserCode(Descriptor, arguments)
            ? [new OperationNotice(
                UserCodeExecutionDetector.NoticeCode,
                "This geoprocessing request executed user-supplied Python (custom toolbox or Python expression).",
                "warning")]
            : [];
        return result.IsFailed || result.IsCanceled
            ? OperationResult.Fail(result.IsCanceled ? "geoprocessing_cancelled" : "geoprocessing_failed",
                result.ErrorMessages.FirstOrDefault()?.Text ?? $"Geoprocessing tool '{request.Tool}' did not complete.", snapshot.Revision) with
            { Data = data, Notices = [.. userCodeNotice] }
            : OperationResult.Ok(
                data,
                snapshot.Revision,
                userCodeNotice.Concat(result.Messages
                    .Where(message => message.Type == GPMessageType.Warning)
                    .Select(message => new OperationNotice("geoprocessing_warning", message.Text, "warning"))));
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

    private static string ToGpValue(JsonElement element)
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
