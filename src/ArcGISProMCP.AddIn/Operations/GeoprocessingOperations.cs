using System.Text.Json;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal sealed class GeoprocessingRunOperation() : ProOperationBase(OperationDescriptor.Create(
    "gp.run", "Run geoprocessing tool",
    "Runs an ArcGIS geoprocessing tool by toolbox-qualified name with ordered parameters and optional environments.",
    JsonSchemas.ObjectSchema(
        "\"tool\": {\"type\": \"string\", \"minLength\": 1}, \"parameters\": {\"type\": \"array\"}, \"environments\": {\"type\": \"object\"}, \"addOutputsToMap\": {\"type\": \"boolean\"}",
        "tool", "parameters"),
    risk: OperationRisk.ExternalSideEffect, requiresConfirmation: true, executionTarget: ExecutionTarget.Background, capabilities: ["geoprocessing"],
    tags: ["gp", "geoprocessing", "analysis", "data processing"], aliases: ["run tool", "spatial analysis", "buffer", "clip"],
    examples: ["Run analysis.Buffer with input, output, and distance parameters."], related: ["layer.add", "view.capture"], typicalDuration: "seconds-to-hours"))
{
    protected override async Task<OperationResult> ExecuteCoreAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
    {
        var tool = RequiredString(arguments, "tool");
        if (!arguments.TryGetProperty("parameters", out var parametersElement) || parametersElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("parameters must be a JSON array.", nameof(arguments));
        var values = parametersElement.EnumerateArray().Select(ToGpValue).ToArray();
        var environments = arguments.TryGetProperty("environments", out var environmentsElement) && environmentsElement.ValueKind == JsonValueKind.Object
            ? environmentsElement.EnumerateObject().Select(property => KeyValuePair.Create(property.Name, ToGpValue(property.Value))).ToArray()
            : [];
        var flags = GPExecuteToolFlags.GPThread | GPExecuteToolFlags.AddToHistory | GPExecuteToolFlags.RefreshProjectItems;
        if (OptionalBoolean(arguments, "addOutputsToMap", true)) flags |= GPExecuteToolFlags.AddOutputsToMap;

        var result = await Geoprocessing.ExecuteToolAsync(tool, values, environments, cancellationToken, null, flags).ConfigureAwait(false);
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        var data = Json(new
        {
            tool, result.IsFailed, result.IsCanceled, result.ErrorCode, result.ReturnValue,
            messages = result.Messages.Select(message => new { type = message.Type.ToString(), message.Text, message.ErrorCode }).ToArray()
        });
        return result.IsFailed || result.IsCanceled
            ? OperationResult.Fail(result.IsCanceled ? "geoprocessing_cancelled" : "geoprocessing_failed",
                result.ErrorMessages.FirstOrDefault()?.Text ?? $"Geoprocessing tool '{tool}' did not complete.", snapshot.Revision) with { Data = data }
            : OperationResult.Ok(data, snapshot.Revision);
    }

    private static string ToGpValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Null or JsonValueKind.Undefined => "#",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.Array => string.Join(";", element.EnumerateArray().Select(ToGpValue)),
        _ => element.GetRawText()
    };
}
