using System.Text.Json;
using System.Text.Json.Nodes;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Bridge.Transport;

return await RunWithErrorBoundaryAsync(args);

static async Task<int> RunWithErrorBoundaryAsync(string[] arguments)
{
    try
    {
        return await RunAsync(arguments);
    }
    catch (BridgeException exception)
    {
        var outcome = string.Equals(exception.Code, "outcome_unknown", StringComparison.Ordinal)
            ? "unknown"
            : "known";
        return WriteError(exception.Code, exception.Message, exception.Retryable, outcome, 1);
    }
    catch (OperationCanceledException exception)
    {
        return WriteError("request_cancelled", exception.Message, true, "known", 1);
    }
    catch (JsonException exception)
    {
        return WriteError("invalid_json", exception.Message, false, "known", 2);
    }
    catch (Exception exception) when (exception is ArgumentException or FormatException)
    {
        return WriteError("invalid_input", exception.Message, false, "known", 2);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return WriteError(
            "local_io_error",
            exception.Message + " A host request may already have completed; verify ArcGIS Pro state before retrying.",
            false,
            "unknown",
            2);
    }
    catch (Exception exception)
    {
        return WriteError("runner_failed", exception.Message, false, "unknown", 1);
    }
}

static int WriteError(
    string code,
    string message,
    bool retryable,
    string outcome,
    int exitCode)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        success = false,
        error = new { code, message, retryable, outcome },
        exitCode
    }));
    return exitCode;
}

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 2 && args[0] == "--save-workflow")
    {
        using var saveWorkflowDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
        using var saveLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var saveClient = CreateClient();
        var saved = await saveClient.CallAsync(
            "workflow.save",
            new { workflow = saveWorkflowDocument.RootElement.Clone() },
            saveLifetime.Token);
        Console.WriteLine(saved);
        return 0;
    }

    if (args.Length == 3 && args[0] == "--image")
    {
        using var captureLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var captureClient = CreateClient();
        var capturedResource = await captureClient.CallAsync("resource.read", new { uri = args[1] }, captureLifetime.Token);
        await File.WriteAllBytesAsync(Path.GetFullPath(args[2]), Convert.FromBase64String(capturedResource.GetProperty("data").GetString()!), captureLifetime.Token);
        Console.WriteLine($"Image saved: {Path.GetFullPath(args[2])}");
        return 0;
    }

    if (args.Length == 3 && args[0] == "--call")
    {
        using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[1]));
        var request = requestDocument.RootElement;
        using var requestLifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var client = CreateClient();
        var method = request.GetProperty("method").GetString()!;
        var parameters = JsonNode.Parse(request.GetProperty("parameters").GetRawText())!.AsObject();
        if ((method == "registry.invoke" || method == "workflow.run" || method == "approval.request") && !parameters.ContainsKey("expectedRevision"))
        {
            var currentState = await client.CallAsync("system.get_state", new { }, requestLifetime.Token);
            parameters["expectedRevision"] = Required(currentState, "workspace", "revision").GetString();
        }
        var result = await client.CallAsync(method, parameters, requestLifetime.Token);
        await WriteJsonAsync(Path.GetFullPath(args[2]), result, requestLifetime.Token);
        Console.WriteLine(result);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False ? 1 : 0;
    }

    if (args.Length == 4 && args[0] == "--call-timeout")
    {
        if (!int.TryParse(args[1], out var timeoutMilliseconds) || timeoutMilliseconds is < 1 or > 300_000)
            throw new ArgumentException("--call-timeout milliseconds must be between 1 and 300000.");
        using var requestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(args[2]));
        var request = requestDocument.RootElement;
        using var requestLifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMilliseconds));
        var client = CreateClient();
        var method = request.GetProperty("method").GetString()!;
        var parameters = JsonNode.Parse(request.GetProperty("parameters").GetRawText())!.AsObject();
        if ((method == "registry.invoke" || method == "workflow.run" || method == "approval.request") && !parameters.ContainsKey("expectedRevision"))
        {
            var currentState = await client.CallAsync("system.get_state", new { }, requestLifetime.Token);
            parameters["expectedRevision"] = Required(currentState, "workspace", "revision").GetString();
        }
        try
        {
            var result = await client.CallAsync(method, parameters, requestLifetime.Token);
            await WriteJsonAsync(Path.GetFullPath(args[3]), result, CancellationToken.None);
            Console.WriteLine(result);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False ? 1 : 0;
        }
        catch (BridgeException exception)
        {
            var evidence = JsonSerializer.SerializeToElement(new
            {
                success = false,
                error = new
                {
                    code = exception.Code,
                    message = exception.Message,
                    exception.Retryable,
                    outcome = string.Equals(exception.Code, "outcome_unknown", StringComparison.Ordinal) ? "unknown" : "known"
                }
            });
            await WriteJsonAsync(Path.GetFullPath(args[3]), evidence, CancellationToken.None);
            throw;
        }
    }

    static DiscoveringBridgeClient CreateClient() => new(
        TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(7));

    if (args.Length != 6)
    {
        Console.Error.WriteLine("Usage: ArcGISProMCP.DemoRunner --save-workflow <workflow.json>");
        Console.Error.WriteLine("   or: ArcGISProMCP.DemoRunner --call <request.json> <result.json>");
        Console.Error.WriteLine("   or: ArcGISProMCP.DemoRunner --call-timeout <milliseconds> <request.json> <result.json>");
        Console.Error.WriteLine("   or: ArcGISProMCP.DemoRunner --image <resource-uri> <output.png>");
        Console.Error.WriteLine("   or: ArcGISProMCP.DemoRunner <workflow.json> <zoning.shp> <transit.shp> <buildings.shp> <site-design.shp> <output-directory>");
        return 2;
    }

    var inputs = args.Take(5).Select(Path.GetFullPath).ToArray();
    foreach (var input in inputs)
    {
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"Input does not exist: {input}");
            return 2;
        }
    }

    var outputDirectory = Path.GetFullPath(args[5]);
    Directory.CreateDirectory(outputDirectory);
    using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    var bridge = new DiscoveringBridgeClient(
        TimeSpan.FromSeconds(3),
        TimeSpan.FromMinutes(7));

    Console.WriteLine("Waiting for the ArcGIS Pro MCP add-in...");
    var state = await WaitForBridgeAsync(bridge, lifetime.Token);
    var initialRevision = Required(state, "workspace", "revision").GetString()
        ?? throw new InvalidOperationException("ArcGIS Pro returned an empty workspace revision.");
    Console.WriteLine($"Connected to ArcGIS Pro process {state.GetProperty("processId")}; registry has {state.GetProperty("operationCount")} operations.");

    using var workflowDocument = JsonDocument.Parse(await File.ReadAllTextAsync(inputs[0], lifetime.Token));
    var workflow = workflowDocument.RootElement.Clone();
    var workflowId = workflow.GetProperty("id").GetString()!;
    var workflowVersion = workflow.GetProperty("version").GetString()!;
    var listed = await bridge.CallAsync("workflow.list", new { }, lifetime.Token);
    var alreadyInstalled = listed.ValueKind == JsonValueKind.Array && listed.EnumerateArray().Any(item =>
        string.Equals(item.GetProperty("id").GetString(), workflowId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(item.GetProperty("version").GetString(), workflowVersion, StringComparison.OrdinalIgnoreCase));
    if (!alreadyInstalled)
    {
        await bridge.CallAsync("workflow.save", new { workflow }, lifetime.Token);
        Console.WriteLine($"Installed workflow {workflowId}@{workflowVersion}.");
    }
    else
    {
        Console.WriteLine($"Using installed workflow {workflowId}@{workflowVersion}.");
    }

    var discovery = await bridge.CallAsync(
        "registry.search",
        new { query = "style and compose three planning maps with labels, query their attributes, then capture a layout", limit = 12 },
        lifetime.Token);
    await WriteJsonAsync(Path.Combine(outputDirectory, "registry-search.json"), discovery, lifetime.Token);

    var run = await bridge.CallAsync(
        "workflow.run",
        new
        {
            workflowId,
            expectedRevision = initialRevision,
            parameters = new
            {
                zoningSource = inputs[1],
                transitSource = inputs[2],
                buildingsSource = inputs[3],
                siteDesignSource = inputs[4],
                zoningLabelExpression = "$feature.land_use_1",
                transitLabelExpression = "$feature.FULL_NAME",
                siteLabelExpression = "$feature.Name"
            }
        },
        lifetime.Token);

    await WriteJsonAsync(Path.Combine(outputDirectory, "workflow-run.json"), run, lifetime.Token);
    foreach (var step in run.GetProperty("results").EnumerateArray())
        Console.WriteLine($"{step.GetProperty("step")}: {step.GetProperty("success")} {step.GetProperty("message")}");
    if (!run.GetProperty("success").GetBoolean()) return 1;

    var capture = run.GetProperty("results").EnumerateArray().Last();
    var uri = Required(capture, "data", "resource").GetString()!;
    var resource = await bridge.CallAsync("resource.read", new { uri }, lifetime.Token);
    var imagePath = Path.Combine(outputDirectory, "master-plan.png");
    await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(resource.GetProperty("data").GetString()!), lifetime.Token);
    string[] queryFields = ["id", "land_use_1", "area_gross"];
    var query = await InvokeAsync(bridge, "table.query", new { map = "Zoning", layer = "Zoning", fields = queryFields, limit = 5 }, null, lifetime.Token);
    await WriteJsonAsync(Path.Combine(outputDirectory, "attribute-query.json"), query, lifetime.Token);
    var stats = await InvokeAsync(bridge, "table.statistics", new { map = "Zoning", layer = "Zoning", field = "area_gross" }, null, lifetime.Token);
    await WriteJsonAsync(Path.Combine(outputDirectory, "statistics.json"), stats, lifetime.Token);
    Console.WriteLine($"Layout exported: {imagePath}");

    return 0;
}

static async Task<JsonElement> WaitForBridgeAsync(IBridgeClient bridge, CancellationToken cancellationToken)
{
    Exception? last = null;
    for (var attempt = 1; attempt <= 60; attempt++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await bridge.CallAsync("system.get_state", new { }, cancellationToken);
        }
        catch (BridgeException exception) when (exception.Retryable)
        {
            last = exception;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    throw new InvalidOperationException("ArcGIS Pro MCP bridge did not become available.", last);
}

static async Task<JsonElement> InvokeAsync(
    IBridgeClient bridge,
    string operationId,
    object arguments,
    string? expectedRevision,
    CancellationToken cancellationToken) =>
    await bridge.CallAsync(
        "registry.invoke",
        new { operationId, arguments, expectedRevision, idempotencyKey = $"demo-{operationId}-{Guid.NewGuid():N}" },
        cancellationToken);

static JsonElement Required(JsonElement root, params string[] path)
{
    var current = root;
    foreach (var segment in path)
    {
        if (!current.TryGetProperty(segment, out current))
            throw new InvalidOperationException($"Response is missing '{string.Join(".", path)}'.");
    }
    return current;
}

static Task WriteJsonAsync(string path, JsonElement value, CancellationToken cancellationToken) =>
    File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
