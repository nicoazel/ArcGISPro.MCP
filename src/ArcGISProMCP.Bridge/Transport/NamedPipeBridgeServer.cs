using System.IO.Pipes;
using ArcGISProMCP.Bridge.Protocol;

namespace ArcGISProMCP.Bridge.Transport;

public sealed class NamedPipeBridgeServer(
    IBridgeRequestHandler handler,
    string pipeName = BridgeProtocol.DefaultPipeName) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _listenTask;

    public bool IsRunning => _listenTask is { IsCompleted: false };

    public void Start()
    {
        if (_listenTask is { IsCompleted: false }) return;
        _listenTask = ListenAsync(_stopping.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var server = CreatePipe();
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A client disconnect is isolated to its connection; accept the next request.
            }
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        BridgeRequest? request = null;
        BridgeResponse response;
        try
        {
            request = await LengthPrefixedJson.ReadAsync<BridgeRequest>(stream, cancellationToken).ConfigureAwait(false);
            if (request.ProtocolVersion != BridgeProtocol.Version)
            {
                response = Failure(request.RequestId, "unsupported_protocol", $"Bridge protocol {request.ProtocolVersion} is unsupported.");
            }
            else
            {
                response = await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = Failure(request?.RequestId ?? string.Empty, "bridge_handler_failed", exception.Message);
        }

        await LengthPrefixedJson.WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
    }

    private static BridgeResponse Failure(string requestId, string code, string message) =>
        new(BridgeProtocol.Version, requestId, false, null, new BridgeError(code, message), DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }
}
