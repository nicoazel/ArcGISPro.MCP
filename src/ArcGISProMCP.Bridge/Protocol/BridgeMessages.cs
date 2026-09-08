using System.Text.Json;

namespace ArcGISProMCP.Bridge.Protocol;

public static class BridgeProtocol
{
    public const int Version = 1;
    public const int MaximumMessageBytes = 8 * 1024 * 1024;
    public const string DefaultPipeName = "ArcGISProMCP.v1";
}

public sealed record BridgeRequest(
    int ProtocolVersion,
    string RequestId,
    string Method,
    JsonElement? Parameters,
    DateTimeOffset SentAt);

public sealed record BridgeResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    JsonElement? Result,
    BridgeError? Error,
    DateTimeOffset CompletedAt);

public sealed record BridgeError(string Code, string Message, bool Retryable = false);

public interface IBridgeRequestHandler
{
    Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken);
}

public sealed class BridgeException(string code, string message, bool retryable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}
