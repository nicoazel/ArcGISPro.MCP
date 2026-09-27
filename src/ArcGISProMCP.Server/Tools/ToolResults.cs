using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ModelContextProtocol.Protocol;

namespace ArcGISProMCP.Server.Tools;

/// <summary>
/// The structured result of every tool. Success and failure share this one schema, so clients can
/// validate either against the tool's outputSchema. <see cref="Result"/> is null when the call
/// failed before producing a result; registry_invoke and workflow_run keep it populated when the
/// operation or workflow itself reported <c>success: false</c>.
/// </summary>
public sealed record ToolEnvelope<T>(
    [property: Description("True when the call succeeded. False means error is set and the MCP result has isError: true.")] bool Ok,
    [property: Description("The typed result; null when the call failed before producing one.")] T? Result,
    [property: Description("Why the call failed; null on success.")] ToolError? Error);

public sealed record ToolError(
    [property: Description("Stable machine-readable error code, for example workspace_revision_mismatch or arcgis_unavailable.")] string Code,
    [property: Description("Human-readable explanation.")] string Message,
    [property: Description("True when repeating the same call later may succeed (for example ArcGIS Pro was not reachable).")] bool Retryable,
    [property: Description("Workspace revision observed when the failure happened, when known.")] string? Revision);

/// <summary>Builds <see cref="CallToolResult"/> values that carry a <see cref="ToolEnvelope{T}"/>.</summary>
internal static class ToolResults
{
    public static JsonSerializerOptions JsonOptions => BridgeJson.Options;

    /// <summary>
    /// Calls the bridge and returns its typed result. A <see cref="BridgeException"/> becomes an
    /// isError result with the bridge's code and retryability. <paramref name="failure"/> lets a
    /// tool report a result that describes its own failure (success: false) as an error while
    /// keeping the result.
    /// </summary>
    public static async Task<CallToolResult> CallAsync<T>(
        IBridgeClient bridge,
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        Func<T, ToolError?>? failure = null)
        where T : class
    {
        var (result, error) = await ReadAsync<T>(bridge, method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return Failure<T>(error!);
        error = failure?.Invoke(result);
        return error is null ? Success(result) : Failure(result, error);
    }

    /// <summary>Calls the bridge; returns either the typed result or the error describing why not.</summary>
    public static async Task<(T? Result, ToolError? Error)> ReadAsync<T>(
        IBridgeClient bridge,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(bridge);
        JsonElement element;
        try
        {
            element = await bridge.CallAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (BridgeException exception)
        {
            return (null, FromException(exception));
        }

        try
        {
            return element.Deserialize<T>(JsonOptions) is { } result
                ? (result, null)
                : (null, ContractMismatch(method, "The result was null."));
        }
        catch (JsonException exception)
        {
            return (null, ContractMismatch(method, exception.Message));
        }
    }

    public static CallToolResult Success<T>(T result) => Build(new ToolEnvelope<T>(true, result, null), isError: false);

    public static CallToolResult Failure<T>(ToolError error) => Build(new ToolEnvelope<T>(false, default, error), isError: true);

    public static CallToolResult Failure<T>(T? result, ToolError error) => Build(new ToolEnvelope<T>(false, result, error), isError: true);

    public static ToolError FromException(BridgeException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ToolError(exception.Code, exception.Message, exception.Retryable, null);
    }

    private static ToolError ContractMismatch(string method, string detail) => new(
        "bridge_contract_mismatch",
        $"The ArcGIS Pro add-in returned a '{method}' result this gateway does not understand. Install matching add-in and gateway versions. {detail}",
        false,
        null);

    private static CallToolResult Build<T>(ToolEnvelope<T> envelope, bool isError)
    {
        var structured = JsonSerializer.SerializeToElement(envelope, JsonOptions);
        return new CallToolResult
        {
            StructuredContent = structured,
            // Clients without structured-content support read the same JSON as text.
            Content = [new TextContentBlock { Text = structured.GetRawText() }],
            IsError = isError
        };
    }
}
