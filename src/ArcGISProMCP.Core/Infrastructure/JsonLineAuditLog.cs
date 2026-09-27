using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.Core.Infrastructure;

/// <summary>
/// Append-only JSON Lines audit log with size-based rotation. When the active file exceeds
/// <c>maximumBytes</c> it is renamed to <c>&lt;name&gt;.&lt;utc-timestamp&gt;.jsonl</c> and only the newest
/// <c>retainedFiles</c> rotated files are kept.
/// </summary>
/// <remarks>
/// Every ArcGIS Pro process shares the same file, so rotation is best effort: if another process
/// holds the file or has already rotated it, rotation is skipped and the record is still appended.
/// A rotation failure never drops a record. Each record is appended with one write through a
/// handle that lets other processes read, write, rename and delete the file.
/// </remarks>
public sealed class JsonLineAuditLog : IOperationAuditLog, IDisposable
{
    public const long DefaultMaximumBytes = 16L * 1024 * 1024;
    public const int DefaultRetainedFiles = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _path;
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly long _maximumBytes;
    private readonly int _retainedFiles;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonLineAuditLog(
        string path,
        long maximumBytes = DefaultMaximumBytes,
        int retainedFiles = DefaultRetainedFiles,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 0);
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("Audit path must have a parent directory.", nameof(path));
        _baseName = Path.GetFileNameWithoutExtension(_path);
        _extension = Path.GetExtension(_path);
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
    }

    public string FilePath => _path;

    public async ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        var line = Utf8NoBom.GetBytes(JsonSerializer.Serialize(auditEvent, JsonOptions) + Environment.NewLine);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                RotateIfNeeded();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Typically another Pro process has the file open or rotated it first.
                System.Diagnostics.Trace.TraceWarning("Audit log rotation skipped for '{0}': {1}", _path, exception.Message);
            }

            var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0,
                FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RotateIfNeeded()
    {
        var active = new FileInfo(_path);
        if (!active.Exists || active.Length <= _maximumBytes) return;

        var stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var destination = Path.Combine(_directory, $"{_baseName}.{stamp}{_extension}");
        for (var suffix = 1; File.Exists(destination); suffix++)
            destination = Path.Combine(_directory, $"{_baseName}.{stamp}-{suffix}{_extension}");
        File.Move(_path, destination);

        var rotated = Directory.EnumerateFiles(_directory, $"{_baseName}.*{_extension}")
            .Where(file => !string.Equals(Path.GetFullPath(file), _path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Skip(_retainedFiles)
            .ToArray();
        foreach (var stale in rotated)
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException exception)
            {
                System.Diagnostics.Trace.TraceWarning("Could not delete rotated audit log '{0}': {1}", stale, exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                System.Diagnostics.Trace.TraceWarning("Could not delete rotated audit log '{0}': {1}", stale, exception.Message);
            }
        }
    }

    public void Dispose() => _gate.Dispose();
}
