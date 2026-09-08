using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ArcGISProMCP.Bridge.Protocol;

namespace ArcGISProMCP.Bridge.Transport;

/// <summary>Bounded transport concurrency; resource owners must serialize mutations themselves.</summary>
public sealed class NamedPipeBridgeServer : IAsyncDisposable
{
    private readonly IBridgeRequestHandler _handler;
    private readonly string _pipeName;
    private readonly int _maximumConnections;
    private readonly TimeSpan _ioTimeout;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private readonly object _lifecycle = new();
    private FileStream? _lease;
    private Task? _listenTask;
    private Task? _disposeTask;
    private long _connectionSequence;

    public NamedPipeBridgeServer(IBridgeRequestHandler handler, string pipeName = BridgeProtocol.DefaultPipeName,
        int maximumConnections = 8, TimeSpan? ioTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        if (maximumConnections is < 2 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumConnections));
        _handler = handler;
        _pipeName = pipeName;
        _maximumConnections = maximumConnections;
        _ioTimeout = ioTimeout ?? TimeSpan.FromSeconds(10);
        if (_ioTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ioTimeout));
        _slots = new SemaphoreSlim(maximumConnections, maximumConnections);
    }

    public bool IsRunning => _listenTask is { IsCompleted: false };
    public int ActiveConnectionCount => _connections.Count;

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            if (_listenTask is not null) return;
            var leaseRoot = Path.Combine(Path.GetTempPath(), "ArcGISProMCP", "bridge-leases");
            Directory.CreateDirectory(leaseRoot);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_pipeName.ToUpperInvariant())));
            try
            {
                // File sharing provides a crash-released, thread-independent exclusive lease.
                // Retain the empty file: deleting it would introduce a cross-process lock race.
                _lease = new FileStream(Path.Combine(leaseRoot, key + ".lease"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                var first = CreatePipe();
                _listenTask = ListenAsync(first, _stopping.Token);
            }
            catch
            {
                _lease?.Dispose();
                _lease = null;
                throw;
            }
        }
    }

    private async Task ListenAsync(NamedPipeServerStream first, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pending = first;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var handedOff = false;
                try
                {
                    pending ??= CreatePipe();
                    await pending.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    var connected = pending;
                    pending = null;
                    var id = Interlocked.Increment(ref _connectionSequence);
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _connections[id] = completion.Task;
                    handedOff = true;
                    _ = ServeAndReleaseAsync(id, connected, completion, cancellationToken);
                }
                finally
                {
                    if (!handedOff) _slots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ServeAndReleaseAsync(long id, NamedPipeServerStream pipe, TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (pipe.ConfigureAwait(false))
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { /* Disconnect affects only this client. */ }
        catch (OperationCanceledException) { /* Stop or bounded I/O timeout. */ }
        catch (Exception exception) { System.Diagnostics.Trace.TraceError("Bridge connection failed: {0}", exception); }
        finally
        {
            _slots.Release();
            completion.TrySetResult();
            _connections.TryRemove(id, out _);
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        BridgeRequest? request = null;
        BridgeResponse response;
        try
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            read.CancelAfter(_ioTimeout);
            request = await LengthPrefixedJson.ReadAsync<BridgeRequest>(stream, read.Token).ConfigureAwait(false);
            if (request.ProtocolVersion != BridgeProtocol.Version)
                response = Failure(request.RequestId, "unsupported_protocol", $"Bridge protocol {request.ProtocolVersion} is unsupported.");
            else if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 ||
                     string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 128)
                response = Failure(request.RequestId ?? string.Empty, "invalid_request", "Request ID and method must contain 1 to 128 characters.");
            else
                response = await _handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = Failure(request?.RequestId ?? string.Empty, "bridge_handler_failed", exception.Message);
        }
        using var write = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        write.CancelAfter(_ioTimeout);
        await LengthPrefixedJson.WriteAsync(stream, response, write.Token).ConfigureAwait(false);
    }

    private NamedPipeServerStream CreatePipe() => new(_pipeName, PipeDirection.InOut, _maximumConnections,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

    private static BridgeResponse Failure(string requestId, string code, string message) =>
        new(BridgeProtocol.Version, requestId, false, null, new BridgeError(code, message), DateTimeOffset.UtcNow);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_listenTask is not null) await _listenTask.ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(_connections.Values.ToArray()).ConfigureAwait(false);
            _lease?.Dispose();
            _slots.Dispose();
            _stopping.Dispose();
        }
    }
}
