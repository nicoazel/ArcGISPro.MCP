using System.IO.Pipes;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;

namespace ArcGISProMCP.Bridge.Transport;

public interface IBridgeClient
{
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken);
}

public sealed class NamedPipeBridgeClient(
    string pipeName,
    TimeSpan connectTimeout,
    TimeSpan requestTimeout) : IBridgeClient
{
    public NamedPipeBridgeClient()
        : this(BridgeProtocol.DefaultPipeName, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5))
    {
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestLifetime.CancelAfter(requestTimeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        using (var connectLifetime = CancellationTokenSource.CreateLinkedTokenSource(requestLifetime.Token))
        {
            connectLifetime.CancelAfter(connectTimeout);
            try
            {
                await pipe.ConnectAsync(connectLifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BridgeException("arcgis_unavailable", "ArcGIS Pro MCP add-in is not accepting bridge connections.", true);
            }
            catch (IOException exception)
            {
                throw new BridgeException("arcgis_unavailable", "Could not connect to the ArcGIS Pro bridge.", true, exception);
            }
        }

        var request = new BridgeRequest(
            BridgeProtocol.Version,
            Guid.NewGuid().ToString("N"),
            method,
            parameters is null ? null : JsonSerializer.SerializeToElement(parameters),
            DateTimeOffset.UtcNow);
        BridgeResponse response;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await LengthPrefixedJson.WriteAsync(pipe, request, requestLifetime.Token).ConfigureAwait(false);
            response = await LengthPrefixedJson.ReadAsync<BridgeResponse>(pipe, requestLifetime.Token).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new BridgeException("outcome_unknown", "The bridge disconnected after transmission began. The operation may have run; inspect workspace state before retrying a mutation.", false, exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new BridgeException("outcome_unknown", "Waiting for the bridge result was cancelled or timed out. This does not cancel an accepted host write; inspect workspace state before retrying a mutation.", false, exception);
        }

        if (response.ProtocolVersion != BridgeProtocol.Version || response.RequestId != request.RequestId)
        {
            throw new BridgeException("protocol_mismatch", "Bridge response did not match the request.");
        }

        if (!response.Success)
        {
            var error = response.Error ?? new BridgeError("unknown_bridge_error", "ArcGIS Pro returned an unspecified error.");
            throw new BridgeException(error.Code, error.Message, error.Retryable);
        }

        return response.Result?.Clone() ?? JsonSerializer.SerializeToElement(new { });
    }
}
