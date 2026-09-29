using System.Collections.Concurrent;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;

namespace ArcGISProMCP.Testing;

/// <summary>
/// An <see cref="IBridgeClient"/> that calls a host's <see cref="IBridgeRequestHandler"/> directly.
/// Requests and responses are serialized and parsed exactly as <see cref="NamedPipeBridgeClient"/>
/// and the pipe server do (default options for parameters, web defaults for the envelope), and a
/// failed response becomes the same <see cref="BridgeException"/>, so everything but the pipe itself
/// is the production path.
/// </summary>
internal sealed class InProcessBridgeClient(IBridgeRequestHandler handler) : IBridgeClient
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<InProcessBridgeCall> _calls = new();

    /// <summary>Every bridge call in arrival order, with its parameters as they went on the wire.</summary>
    public IReadOnlyList<InProcessBridgeCall> Calls => [.. _calls];

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        cancellationToken.ThrowIfCancellationRequested();
        var request = new BridgeRequest(
            BridgeProtocol.Version,
            Guid.NewGuid().ToString("N"),
            method,
            parameters is null ? null : JsonSerializer.SerializeToElement(parameters),
            DateTimeOffset.UtcNow);
        request = RoundTrip(request);
        _calls.Enqueue(new InProcessBridgeCall(method, request.Parameters));

        var response = RoundTrip(await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false));
        if (response.ProtocolVersion != BridgeProtocol.Version || response.RequestId != request.RequestId)
            throw new BridgeException("protocol_mismatch", "Bridge response did not match the request.");
        if (!response.Success)
        {
            var error = response.Error ?? new BridgeError("unknown_bridge_error", "ArcGIS Pro returned an unspecified error.");
            throw new BridgeException(error.Code, error.Message, error.Retryable);
        }

        return response.Result?.Clone() ?? JsonSerializer.SerializeToElement(new { });
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, WireOptions), WireOptions)
        ?? throw new BridgeException("invalid_json", "Bridge message deserialized to null.");
}

internal sealed record InProcessBridgeCall(string Method, JsonElement? Parameters);
