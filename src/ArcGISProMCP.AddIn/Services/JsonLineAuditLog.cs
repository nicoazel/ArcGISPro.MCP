using System.IO;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Services;

internal sealed class JsonLineAuditLog : IOperationAuditLog, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineAuditLog(string path)
    {
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Audit path must have a parent directory.", nameof(path)));
    }

    public async ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(auditEvent, JsonOptions) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
