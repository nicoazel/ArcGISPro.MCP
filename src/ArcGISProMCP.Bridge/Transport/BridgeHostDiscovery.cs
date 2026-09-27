using System.Diagnostics;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;

namespace ArcGISProMCP.Bridge.Transport;

public sealed record BridgeHostRecord(
    int ProcessId,
    string PipeName,
    DateTimeOffset ProcessStartedAtUtc,
    DateTimeOffset PublishedAtUtc,
    string? ProjectName = null,
    string? ProjectUri = null);

public static class BridgeHostDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ProcessPipeName(int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return $"{BridgeProtocol.DefaultPipeName}.{processId}";
    }

    public static string DefaultRegistryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArcGISProMCP",
        "hosts");

    public static void Publish(BridgeHostRecord host, string? registryRoot = null)
    {
        Validate(host);
        var root = NormalizeRoot(registryRoot);
        Directory.CreateDirectory(root);
        var target = HostPath(root, host.ProcessId);
        var temporary = Path.Combine(root, $".host-{host.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(host, JsonOptions));
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static IReadOnlyList<BridgeHostRecord> ListLive(
        string? registryRoot = null,
        Func<BridgeHostRecord, bool>? isLive = null)
    {
        var root = NormalizeRoot(registryRoot);
        if (!Directory.Exists(root)) return [];
        isLive ??= IsMatchingLiveProcess;
        var hosts = new List<BridgeHostRecord>();
        foreach (var path in Directory.EnumerateFiles(root, "host-*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var host = JsonSerializer.Deserialize<BridgeHostRecord>(File.ReadAllText(path), JsonOptions);
                if (host is null) continue;
                Validate(host);
                if (isLive(host)) hosts.Add(host);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // Malformed or stale discovery files do not become connection targets.
            }
        }

        return hosts
            .OrderBy(host => host.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(host => host.ProcessId)
            .ToArray();
    }

    public static void Remove(int processId, string? registryRoot = null)
    {
        var path = HostPath(NormalizeRoot(registryRoot), processId);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsMatchingLiveProcess(BridgeHostRecord host)
    {
        try
        {
            using var process = Process.GetProcessById(host.ProcessId);
            if (process.HasExited) return false;
            var actualStart = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return Math.Abs((actualStart - host.ProcessStartedAtUtc).TotalSeconds) < 2;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string? registryRoot) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(registryRoot) ? DefaultRegistryRoot : registryRoot);

    private static string HostPath(string root, int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        return Path.Combine(root, $"host-{processId}.json");
    }

    private static void Validate(BridgeHostRecord host)
    {
        if (host.ProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(host));
        if (string.IsNullOrWhiteSpace(host.PipeName) || host.PipeName.Length > 256 || host.PipeName.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("A bounded nonempty pipe name is required.", nameof(host));
    }
}

public static class BridgeEndpointResolver
{
    public static string ResolvePipeName(
        Func<string, string?>? getEnvironmentVariable = null,
        IReadOnlyList<BridgeHostRecord>? liveHosts = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var explicitPipe = getEnvironmentVariable("ARCGIS_PRO_MCP_PIPE")?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitPipe)) return explicitPipe;

        liveHosts ??= BridgeHostDiscovery.ListLive();
        var pidText = getEnvironmentVariable("ARCGIS_PRO_MCP_HOST_PID")?.Trim();
        if (!string.IsNullOrWhiteSpace(pidText))
        {
            if (!int.TryParse(pidText, out var processId) || processId <= 0)
                throw new BridgeException("arcgis_host_selector_invalid", "ARCGIS_PRO_MCP_HOST_PID must be a positive ArcGIS Pro process id.");
            var selected = liveHosts.SingleOrDefault(host => host.ProcessId == processId);
            if (selected is null)
                throw new BridgeException("arcgis_host_not_found", $"No discovered ArcGIS Pro MCP host matches PID {processId}.", true);
            return selected.PipeName;
        }

        if (liveHosts.Count == 1) return liveHosts[0].PipeName;
        if (liveHosts.Count == 0) return BridgeProtocol.DefaultPipeName;

        var choices = string.Join(", ", liveHosts.Select(host =>
            $"PID {host.ProcessId} ({(string.IsNullOrWhiteSpace(host.ProjectName) ? "no project" : host.ProjectName)})"));
        throw new BridgeException(
            "arcgis_host_ambiguous",
            $"Multiple ArcGIS Pro MCP hosts are available: {choices}. Set ARCGIS_PRO_MCP_HOST_PID or ARCGIS_PRO_MCP_PIPE for this gateway process.");
    }
}

public sealed class DiscoveringBridgeClient(
    TimeSpan connectTimeout,
    TimeSpan requestTimeout,
    Func<string, string?>? getEnvironmentVariable = null,
    Func<IReadOnlyList<BridgeHostRecord>>? listLiveHosts = null) : IBridgeClient
{
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var pipeName = BridgeEndpointResolver.ResolvePipeName(
            getEnvironmentVariable,
            listLiveHosts?.Invoke());
        return await new NamedPipeBridgeClient(pipeName, connectTimeout, requestTimeout)
            .CallAsync(method, parameters, cancellationToken)
            .ConfigureAwait(false);
    }
}
