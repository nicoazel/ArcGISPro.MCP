using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.FakeHost;
using ArcGISProMCP.Testing;

return await FakeHostProgram.RunAsync(args).ConfigureAwait(false);

namespace ArcGISProMCP.FakeHost
{
    internal sealed record FakeHostArguments(
        string Scenario,
        bool AutoApprove,
        bool Autonomous,
        string? PipeName,
        string? ToolboxRoot,
        bool PublishDiscovery);

    internal static class FakeHostProgram
    {
        /// <summary>Prefixed to the project name in the discovery record so listings show a FakeHost for what it is.</summary>
        public const string FakeHostProjectPrefix = "[FakeHost] ";

        private const string Usage = """
            ArcGISProMCP.FakeHost - an ArcGIS Pro MCP host without ArcGIS Pro.

            Runs the add-in's real bridge request handler, operation registry, executor and approval
            queue over a fake in-memory project, on the real named pipe with a per-PID discovery
            record, so the published gateway (arcgis-pro-mcp) and any MCP client work against it.
            For development, demos and evals only; it is not shipped in the release bundle.

            Usage: ArcGISProMCP.FakeHost [options]

              --scenario <name|path>  Scenario JSON: a name under scenarios/ or a file path (default: riverton).
              --auto-approve          Approve every approval request automatically. For eval runs only:
                                      it removes the person from the loop that the product relies on.
              --autonomous            Report autonomous-control, as the add-in does when a person enables it,
                                      to exercise unattended execution and its refusals.
              --pipe <name>           Pipe name (default: ArcGISProMCP.v1.<pid>, as the add-in uses).
              --toolboxes <dir>       Toolbox root for gp.* (default: the synthetic fixture toolboxes). Point it at
                                      ArcGIS Pro's Resources\ArcToolBox\toolboxes for real tool metadata.
              --no-discovery          Do not publish the discovery record under %LOCALAPPDATA%\ArcGISProMCP\hosts.
              --help                  Show this help.

            Without --auto-approve each request is printed and waits for you: type y and press Enter to
            approve the oldest waiting request, or n to deny it. Other commands: p (pending requests),
            s (project state), q (quit). End of input (Ctrl+Z, a closed stdin) or Ctrl+C also stops the host.
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            FakeHostArguments options;
            try
            {
                options = Parse(args);
            }
            catch (ArgumentException exception)
            {
                await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
                await Console.Error.WriteLineAsync("Run with --help for usage.").ConfigureAwait(false);
                return 2;
            }
            if (options.Scenario == "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }

            var scenarioPath = ResolveScenario(options.Scenario);
            if (scenarioPath is null)
            {
                await Console.Error.WriteLineAsync($"Scenario '{options.Scenario}' was not found (looked for a file and for scenarios/{options.Scenario}.json).").ConfigureAwait(false);
                return 2;
            }

            WarnIfArcGisProIsRunning();

            using var stopping = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopping.Cancel();
            };

            var scenario = FakeProScenario.Load(scenarioPath);
            using var runtime = await FakeHostRuntime.StartAsync(
                scenario,
                new FakeHostOptions(Autonomous: options.Autonomous, ToolboxRoot: options.ToolboxRoot),
                stopping.Token).ConfigureAwait(false);
            var processId = Environment.ProcessId;
            var pipeName = options.PipeName ?? BridgeHostDiscovery.ProcessPipeName(processId);
            using var handler = new ConsoleRequestHandler(runtime.Handler);
            var server = new NamedPipeBridgeServer(handler, pipeName);
            try
            {
                server.Start();
                if (options.PublishDiscovery)
                {
                    using var process = Process.GetCurrentProcess();
                    BridgeHostDiscovery.Publish(new BridgeHostRecord(
                        processId,
                        pipeName,
                        new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
                        DateTimeOffset.UtcNow,
                        // The prefix and kind keep FakeHost apart from ArcGIS Pro in every listing;
                        // the gateway never selects a fakehost record automatically.
                        FakeHostProjectPrefix + runtime.State.ProjectName,
                        runtime.State.ProjectUri,
                        BridgeHostKinds.FakeHost));
                }

                PrintBanner(options, scenario, scenarioPath, runtime, pipeName, processId);
                using var approvals = new ApprovalConsole(runtime.Approvals, options.AutoApprove);
                var approvalLoop = approvals.RunAsync(stopping.Token);
                _ = ReadCommandsAsync(approvals, runtime, stopping);
                await approvalLoop.ConfigureAwait(false);
            }
            finally
            {
                if (options.PublishDiscovery) BridgeHostDiscovery.Remove(processId);
                await server.DisposeAsync().ConfigureAwait(false);
                Console.WriteLine("FakeHost stopped.");
            }
            return 0;
        }

        private static void WarnIfArcGisProIsRunning()
        {
            var processes = Process.GetProcessesByName("ArcGISPro");
            try
            {
                if (processes.Length == 0) return;
                var ids = string.Join(", ", processes.Select(process => process.Id));
                Console.Error.WriteLine("WARNING: ArcGIS Pro is running (PID " + ids + ").");
                Console.Error.WriteLine("WARNING: FakeHost serves a FAKE project. It publishes a 'fakehost' discovery record that the gateway");
                Console.Error.WriteLine("WARNING: never selects automatically; select it only with ARCGIS_PRO_MCP_HOST_PID, ARCGIS_PRO_MCP_PIPE or");
                Console.Error.WriteLine("WARNING: " + BridgeEndpointResolver.AllowFakeHostVariable + "=true, and check which host a client is attached to.");
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }

        private static FakeHostArguments Parse(string[] args)
        {
            string scenario = "riverton";
            string? pipe = null;
            string? toolboxes = null;
            bool autoApprove = false, autonomous = false, discovery = true;
            for (var index = 0; index < args.Length; index++)
            {
                string Value() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
                switch (args[index])
                {
                    case "--scenario": scenario = Value(); break;
                    case "--auto-approve": autoApprove = true; break;
                    case "--autonomous": autonomous = true; break;
                    case "--pipe": pipe = Value(); break;
                    case "--toolboxes": toolboxes = Value(); break;
                    case "--no-discovery": discovery = false; break;
                    case "--help" or "-h" or "/?": return new FakeHostArguments("--help", false, false, null, null, false);
                    default: throw new ArgumentException($"Unknown option '{args[index]}'.");
                }
            }
            return new FakeHostArguments(scenario, autoApprove, autonomous, pipe, toolboxes, discovery);
        }

        private static string? ResolveScenario(string scenario)
        {
            if (File.Exists(scenario)) return Path.GetFullPath(scenario);
            var bundled = Path.Combine(AppContext.BaseDirectory, "scenarios", scenario + ".json");
            return File.Exists(bundled) ? bundled : null;
        }

        private static void PrintBanner(FakeHostArguments options, FakeProScenario scenario, string scenarioPath, FakeHostRuntime runtime, string pipeName, int processId)
        {
            Console.WriteLine($"FakeHost: scenario '{scenario.Name}' ({scenarioPath})");
            Console.WriteLine($"  project   {runtime.State.ProjectName} ({runtime.State.ProjectUri})");
            Console.WriteLine($"  maps      {string.Join(", ", runtime.State.Maps.Select(map => $"{map.Name} [{map.Layers.Count} layers]"))}");
            Console.WriteLine($"  registry  {runtime.Pro.Registry.Descriptors.Count} operations; workflows: {string.Join(", ", (scenario.Workflows ?? []).Select(workflow => workflow.Id))}");
            Console.WriteLine($"  pipe      {pipeName}");
            Console.WriteLine($"  pid       {processId}" + (options.PublishDiscovery ? " (discovery record published)" : " (no discovery record)"));
            Console.WriteLine($"  approvals {(options.AutoApprove ? "AUTO-APPROVE (eval runs only)" : "interactive: type y + Enter to approve, n + Enter to deny")}");
            if (options.Autonomous) Console.WriteLine("  mode      autonomous-control reported");
            Console.WriteLine($"Point the gateway at this host with ARCGIS_PRO_MCP_HOST_PID={processId} (or ARCGIS_PRO_MCP_PIPE={pipeName}).");
            Console.WriteLine($"The gateway never selects a FakeHost automatically (unless {BridgeEndpointResolver.AllowFakeHostVariable}=true).");
            Console.WriteLine("Commands: y, n, p (pending), s (state), q (quit).");
        }

        private static async Task ReadCommandsAsync(ApprovalConsole approvals, FakeHostRuntime runtime, CancellationTokenSource stopping)
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    var line = await Console.In.ReadLineAsync(stopping.Token).ConfigureAwait(false);
                    if (line is null) break;
                    // Letters only: Windows PowerShell writes a UTF-8 BOM ahead of the first redirected
                    // line, which the console code page decodes as stray characters.
                    switch (new string([.. line.Where(char.IsAsciiLetter)]).ToLowerInvariant())
                    {
                        case "y" or "yes": approvals.DecideOldest(approve: true); break;
                        case "n" or "no": approvals.DecideOldest(approve: false); break;
                        case "p": approvals.PrintPending(); break;
                        case "s": PrintState(runtime); break;
                        case "q" or "quit" or "exit": await stopping.CancelAsync().ConfigureAwait(false); return;
                        case "": break;
                        default: Console.WriteLine("Commands: y, n, p (pending), s (state), q (quit)."); break;
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        private static void PrintState(FakeHostRuntime runtime)
        {
            var state = runtime.State;
            Console.WriteLine($"{state.ProjectName} dirty={state.IsDirty} revision={runtime.Pro.Workspace.Revision} activeMap={state.ActiveMapName}");
            foreach (var map in state.Maps)
            {
                foreach (var layer in map.Layers)
                {
                    var rows = layer.Table is { } table ? $"{table.Rows.Count} rows, {table.Selection.Count} selected" : layer.Type;
                    Console.WriteLine($"  {map.Name} / {layer.Name}: {rows}");
                }
            }
        }
    }

    /// <summary>
    /// Serializes requests that touch the in-memory project (the fakes are not thread-safe, unlike
    /// ArcGIS Pro's own thread model) and logs each one. approval.status and approval.cancel bypass
    /// the gate so a waiting status call never blocks other requests.
    /// </summary>
    internal sealed class ConsoleRequestHandler(IBridgeRequestHandler inner) : IBridgeRequestHandler, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
        {
            BridgeResponse response;
            if (request.Method is "approval.status" or "approval.cancel")
            {
                response = await inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    response = await inner.HandleAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }
            }
            Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {Describe(request)} -> {Outcome(response)}");
            return response;
        }

        private static string Describe(BridgeRequest request)
        {
            if (request.Parameters is { ValueKind: JsonValueKind.Object } parameters)
            {
                foreach (var name in new[] { "operationId", "workflowId", "requestId", "uri" })
                {
                    if (parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                        return $"{request.Method} {value.GetString()}" +
                               (parameters.TryGetProperty("dryRun", out var dry) && dry.ValueKind == JsonValueKind.True ? " (dry run)" : string.Empty);
                }
            }
            return request.Method;
        }

        private static string Outcome(BridgeResponse response)
        {
            if (!response.Success) return $"error {response.Error?.Code}";
            if (response.Result is { ValueKind: JsonValueKind.Object } result &&
                result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return $"failed {(result.TryGetProperty("errorCode", out var code) ? code.GetString() : null)}";
            if (response.Result is { ValueKind: JsonValueKind.Object } status &&
                status.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.String)
                return state.GetString()!;
            return "ok";
        }

        public void Dispose() => _gate.Dispose();
    }

    /// <summary>
    /// The FakeHost stand-in for the ArcGIS Pro panel: announces each new request and either
    /// approves it at once (--auto-approve) or waits for a y/n typed at the console.
    /// </summary>
    internal sealed class ApprovalConsole(FakeHostApprovals approvals, bool autoApprove) : IDisposable
    {
        private readonly SemaphoreSlim _changed = new(0);
        private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
        private readonly List<string> _waiting = [];
        private readonly object _gate = new();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            void OnChanged(object? sender, EventArgs args) => _changed.Release();
            approvals.Changed += OnChanged;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Announce();
                    try
                    {
                        // Changed wakes the loop; the timeout also catches expirations.
                        await _changed.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { return; }
                }
            }
            finally
            {
                approvals.Changed -= OnChanged;
            }
        }

        private void Announce()
        {
            foreach (var request in approvals.GetPending())
            {
                lock (_gate)
                {
                    if (!_announced.Add(request.Id)) continue;
                }
                Console.WriteLine();
                Console.WriteLine($"[approval] {request.OperationId} ({request.Risk}) - {request.OperationTitle}");
                Console.WriteLine($"  request   {request.Id}");
                Console.WriteLine($"  revision  {request.WorkspaceRevision}; expires {request.ExpiresAt.ToLocalTime():HH:mm:ss}");
                Console.WriteLine($"  arguments {request.Arguments.GetRawText()}");
                if (autoApprove)
                {
                    var approved = approvals.TryResolve(request.Id, ApprovalResolution.ApproveOnce);
                    Console.WriteLine(approved ? "  -> approved automatically (--auto-approve)" : "  -> could not approve (no longer pending)");
                }
                else
                {
                    lock (_gate) _waiting.Add(request.Id);
                    Console.WriteLine("  Type y and press Enter to approve, n to deny.");
                }
            }
        }

        public void DecideOldest(bool approve)
        {
            while (true)
            {
                string id;
                lock (_gate)
                {
                    if (_waiting.Count == 0)
                    {
                        Console.WriteLine("No request is waiting for a decision.");
                        return;
                    }
                    id = _waiting[0];
                    _waiting.RemoveAt(0);
                }
                if (approvals.TryResolve(id, approve ? ApprovalResolution.ApproveOnce : ApprovalResolution.Deny))
                {
                    Console.WriteLine($"  {(approve ? "approved" : "denied")} {id}");
                    return;
                }
                Console.WriteLine($"  {id} is no longer pending (expired or cancelled); skipping it.");
            }
        }

        public void PrintPending()
        {
            var pending = approvals.GetPending();
            if (pending.Count == 0) Console.WriteLine("No pending requests.");
            foreach (var request in pending)
                Console.WriteLine($"  {request.Id} {request.OperationId} {request.Arguments.GetRawText()}");
        }

        public void Dispose() => _changed.Dispose();
    }
}
