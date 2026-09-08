using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workspaces;

namespace ArcGISProMCP.Core.Approvals;

public enum ApprovalRequestState
{
    Pending,
    Approved,
    Denied,
    Expired,
    Cancelled,
    Consumed
}

public enum ApprovalResolution
{
    ApproveOnce,
    Deny
}

public sealed record ApprovalRequestSnapshot(
    string Id,
    string OperationId,
    string OperationVersion,
    string OperationTitle,
    string OperationSummary,
    OperationRisk Risk,
    JsonElement Arguments,
    string WorkspaceRevision,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    ApprovalRequestState State,
    string? ConfirmationToken);

public interface IApprovalService : IConfirmationValidator, IDisposable
{
    event EventHandler? Changed;

    ApprovalRequestSnapshot Request(
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace);

    ApprovalRequestSnapshot? GetStatus(string requestId);

    IReadOnlyList<ApprovalRequestSnapshot> GetPending();

    bool TryResolve(string requestId, ApprovalResolution resolution);

    bool TryCancel(string requestId);

    void RevokeAll();
}

/// <summary>
/// Process-local, fail-closed approval queue. Approved tokens are short lived, bound to the exact
/// operation/version/canonical arguments/workspace revision, and atomically consumed once.
/// </summary>
public sealed class ApprovalService : IApprovalService
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;
    private readonly ITimer _expiryTimer;
    private bool _disposed;

    public ApprovalService(TimeProvider? timeProvider = null, TimeSpan? lifetime = null, int capacity = 128)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Approval lifetime must be greater than zero and no more than ten minutes.");
        if (capacity is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Approval capacity must be between 1 and 4096.");

        _capacity = capacity;
        _expiryTimer = _timeProvider.CreateTimer(
            static state => ((ApprovalService)state!).ExpirePending(),
            this,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    public event EventHandler? Changed;

    public ApprovalRequestSnapshot Request(
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(workspace);
        if (!RequiresApproval(descriptor))
            throw new InvalidOperationException($"Operation '{descriptor.Id}' does not require interactive approval.");
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Approval arguments must be a JSON object.", nameof(arguments));

        var now = _timeProvider.GetUtcNow();
        var fingerprint = CreateFingerprint(descriptor, arguments, workspace.Revision);
        ApprovalRequestSnapshot snapshot;
        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(now);
            var existing = _entries.Values.FirstOrDefault(entry =>
                entry.State == ApprovalRequestState.Pending &&
                string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (existing is not null)
            {
                snapshot = ToSnapshot(existing, includeToken: false);
            }
            else
            {
                PruneTerminalLocked();
                if (_entries.Count >= _capacity)
                    throw new InvalidOperationException("The local approval queue is full. Resolve or cancel an existing request first.");

                var entry = new Entry(
                    NewOpaqueValue(16),
                    descriptor.Id,
                    descriptor.Version,
                    descriptor.Title,
                    descriptor.Summary,
                    descriptor.Risk,
                    arguments.Clone(),
                    workspace.Revision,
                    fingerprint,
                    now,
                    now + _lifetime);
                _entries.Add(entry.Id, entry);
                snapshot = ToSnapshot(entry, includeToken: false);
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    public ApprovalRequestSnapshot? GetStatus(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return null;
        ApprovalRequestSnapshot? snapshot;
        bool changed;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(_timeProvider.GetUtcNow());
            snapshot = _entries.TryGetValue(requestId, out var entry)
                ? ToSnapshot(entry, includeToken: true)
                : null;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return snapshot;
    }

    public IReadOnlyList<ApprovalRequestSnapshot> GetPending()
    {
        ApprovalRequestSnapshot[] snapshots;
        bool changed;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(_timeProvider.GetUtcNow());
            snapshots = _entries.Values
                .Where(entry => entry.State == ApprovalRequestState.Pending)
                .OrderBy(entry => entry.ExpiresAt)
                .Select(entry => ToSnapshot(entry, includeToken: false))
                .ToArray();
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return snapshots;
    }

    public bool TryResolve(string requestId, ApprovalResolution resolution)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        if (resolution is not ApprovalResolution.ApproveOnce and not ApprovalResolution.Deny) return false;
        var resolved = false;
        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(requestId, out var entry) && entry.State == ApprovalRequestState.Pending)
            {
                entry.State = resolution == ApprovalResolution.ApproveOnce
                    ? ApprovalRequestState.Approved
                    : ApprovalRequestState.Denied;
                entry.ConfirmationToken = resolution == ApprovalResolution.ApproveOnce
                    ? NewOpaqueValue(32)
                    : null;
                resolved = true;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return resolved;
    }

    public bool TryCancel(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        var cancelled = false;
        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(_timeProvider.GetUtcNow());
            if (_entries.TryGetValue(requestId, out var entry) &&
                entry.State is ApprovalRequestState.Pending or ApprovalRequestState.Approved)
            {
                entry.State = ApprovalRequestState.Cancelled;
                entry.ConfirmationToken = null;
                cancelled = true;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return cancelled;
    }

    public void RevokeAll()
    {
        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var entry in _entries.Values)
            {
                if (entry.State is not (ApprovalRequestState.Pending or ApprovalRequestState.Approved)) continue;
                entry.State = ApprovalRequestState.Cancelled;
                entry.ConfirmationToken = null;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask<bool> IsValidAsync(
        string token,
        OperationDescriptor descriptor,
        JsonElement arguments,
        WorkspaceSnapshot workspace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(token)) return ValueTask.FromResult(false);

        var expectedFingerprint = CreateFingerprint(descriptor, arguments, workspace.Revision);
        var valid = false;
        var changed = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = ExpireLocked(_timeProvider.GetUtcNow());
            var entry = _entries.Values.FirstOrDefault(candidate =>
                candidate.State == ApprovalRequestState.Approved &&
                string.Equals(candidate.ConfirmationToken, token, StringComparison.Ordinal));
            if (entry is not null && string.Equals(entry.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
            {
                entry.State = ApprovalRequestState.Consumed;
                entry.ConfirmationToken = null;
                valid = true;
                changed = true;
            }
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return ValueTask.FromResult(valid);
    }

    private void ExpirePending()
    {
        var changed = false;
        lock (_gate)
        {
            if (!_disposed) changed = ExpireLocked(_timeProvider.GetUtcNow());
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool ExpireLocked(DateTimeOffset now)
    {
        var changed = false;
        foreach (var entry in _entries.Values)
        {
            if (entry.State is ApprovalRequestState.Pending or ApprovalRequestState.Approved && now >= entry.ExpiresAt)
            {
                entry.State = ApprovalRequestState.Expired;
                entry.ConfirmationToken = null;
                changed = true;
            }
        }
        return changed;
    }

    private void PruneTerminalLocked()
    {
        if (_entries.Count < _capacity) return;
        foreach (var id in _entries.Values
                     .Where(entry => entry.State is ApprovalRequestState.Denied or ApprovalRequestState.Expired or ApprovalRequestState.Cancelled or ApprovalRequestState.Consumed)
                     .OrderBy(entry => entry.RequestedAt)
                     .Select(entry => entry.Id)
                     .ToArray())
        {
            _entries.Remove(id);
            if (_entries.Count < _capacity) break;
        }
    }

    private static bool RequiresApproval(OperationDescriptor descriptor) =>
        descriptor.RequiresConfirmation ||
        descriptor.Risk is OperationRisk.Destructive or OperationRisk.ExternalSideEffect;

    private static ApprovalRequestSnapshot ToSnapshot(Entry entry, bool includeToken) => new(
        entry.Id,
        entry.OperationId,
        entry.OperationVersion,
        entry.OperationTitle,
        entry.OperationSummary,
        entry.Risk,
        entry.Arguments.Clone(),
        entry.WorkspaceRevision,
        entry.RequestedAt,
        entry.ExpiresAt,
        entry.State,
        includeToken && entry.State == ApprovalRequestState.Approved ? entry.ConfirmationToken : null);

    private static string CreateFingerprint(
        OperationDescriptor descriptor,
        JsonElement arguments,
        string workspaceRevision)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, arguments);
        }

        var prefix = Encoding.UTF8.GetBytes($"{descriptor.Id}\0{descriptor.Version}\0{workspaceRevision}\0");
        var canonical = stream.ToArray();
        var payload = new byte[prefix.Length + canonical.Length];
        Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
        Buffer.BlockCopy(canonical, 0, payload, prefix.Length, canonical.Length);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("Approval arguments contain an unsupported JSON value.", nameof(value));
        }
    }

    private static string NewOpaqueValue(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            Changed = null;
        }
        _expiryTimer.Dispose();
    }

    private sealed class Entry(
        string id,
        string operationId,
        string operationVersion,
        string operationTitle,
        string operationSummary,
        OperationRisk risk,
        JsonElement arguments,
        string workspaceRevision,
        string fingerprint,
        DateTimeOffset requestedAt,
        DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;
        public string OperationId { get; } = operationId;
        public string OperationVersion { get; } = operationVersion;
        public string OperationTitle { get; } = operationTitle;
        public string OperationSummary { get; } = operationSummary;
        public OperationRisk Risk { get; } = risk;
        public JsonElement Arguments { get; } = arguments;
        public string WorkspaceRevision { get; } = workspaceRevision;
        public string Fingerprint { get; } = fingerprint;
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public ApprovalRequestState State { get; set; } = ApprovalRequestState.Pending;
        public string? ConfirmationToken { get; set; }
    }
}
