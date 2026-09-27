using System.Collections.Concurrent;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;

namespace ArcGISProMCP.Server.Tests.Harness;

/// <summary>
/// Scriptable <see cref="IBridgeClient"/>: each bridge method maps to a handler that receives the
/// serialized (web-cased) parameters. Unscripted methods fail like an absent ArcGIS Pro host.
/// </summary>
public sealed class FakeBridgeClient : IBridgeClient
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, Func<JsonElement, JsonElement>> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<BridgeCall> _calls = new();

    public IReadOnlyList<BridgeCall> Calls => [.. _calls];

    public FakeBridgeClient On(string method, Func<JsonElement, JsonElement> handler)
    {
        _handlers[method] = handler;
        return this;
    }

    public FakeBridgeClient OnObject(string method, Func<JsonElement, object?> handler) =>
        On(method, parameters => JsonSerializer.SerializeToElement(handler(parameters), WireOptions));

    public FakeBridgeClient Returns(string method, object? result) => OnObject(method, _ => result);

    public FakeBridgeClient ReturnsJson(string method, string json)
    {
        using var document = JsonDocument.Parse(json);
        var element = document.RootElement.Clone();
        return On(method, _ => element);
    }

    public FakeBridgeClient Throws(string method, string code, string message, bool retryable = false) =>
        On(method, _ => throw new BridgeException(code, message, retryable));

    public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var serialized = JsonSerializer.SerializeToElement(parameters, WireOptions);
        _calls.Enqueue(new BridgeCall(method, serialized));
        if (!_handlers.TryGetValue(method, out var handler))
            throw new BridgeException("arcgis_unavailable", $"Fake bridge has no response scripted for '{method}'.", true);
        return Task.FromResult(handler(serialized));
    }
}

public sealed record BridgeCall(string Method, JsonElement Parameters);
