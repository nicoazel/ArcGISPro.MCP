using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.AddIn.Bridge;

internal sealed class ProBridgeRequestHandler(
    IOperationRegistry registry,
    OperationContext baseContext,
    IWorkflowLibrary workflows,
    FileResourceStore resources,
    BridgeAccessState access) : IBridgeRequestHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ConcurrentDictionary<string, IdempotencyEntry> _idempotency = new(StringComparer.Ordinal);

    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!access.Enabled && request.Method != "system.get_state")
                throw new BridgeException("bridge_disabled", "MCP access is disconnected in the ArcGIS Pro panel.");
            var parameters = request.Parameters?.Clone() ?? JsonSerializer.SerializeToElement(new { });
            var result = request.Method switch
            {
                "system.get_state" => await GetStateAsync(cancellationToken).ConfigureAwait(false),
                "registry.search" => Search(parameters),
                "registry.browse" => Browse(parameters),
                "registry.describe" => Describe(parameters),
                "registry.validate" => await ValidateAsync(parameters, cancellationToken).ConfigureAwait(false),
                "registry.invoke" => await InvokeAsync(parameters, request.RequestId, cancellationToken).ConfigureAwait(false),
                "workflow.list" => await ListWorkflowsAsync(cancellationToken).ConfigureAwait(false),
                "workflow.get" => await GetWorkflowAsync(parameters, cancellationToken).ConfigureAwait(false),
                "workflow.save" => await SaveWorkflowAsync(parameters, cancellationToken).ConfigureAwait(false),
                "workflow.run" => await RunWorkflowAsync(parameters, request.RequestId, cancellationToken).ConfigureAwait(false),
                "resource.read" => await resources.ReadAsync(RequiredString(parameters, "uri"), cancellationToken).ConfigureAwait(false),
                _ => throw new BridgeException("method_not_found", $"Unknown bridge method '{request.Method}'.")
            };
            return Success(request.RequestId, result);
        }
        catch (BridgeException exception)
        {
            return Failure(request.RequestId, exception.Code, exception.Message, exception.Retryable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request.RequestId, "request_cancelled", "The bridge request was cancelled.", true);
        }
        catch (Exception exception)
        {
            return Failure(request.RequestId, "bridge_request_failed", exception.Message);
        }
    }

    private async Task<JsonElement> GetStateAsync(CancellationToken cancellationToken)
    {
        var workspace = await baseContext.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            bridgeProtocol = BridgeProtocol.Version,
            processId = Environment.ProcessId,
            workspace,
            operationCount = registry.Descriptors.Count,
            connected = access.Enabled
        }, JsonOptions);
    }

    private JsonElement Search(JsonElement parameters)
    {
        var query = new OperationQuery(
            OptionalString(parameters, "query"),
            OptionalString(parameters, "domain"),
            Limit: Integer(parameters, "limit", 12));
        var hits = registry.Search(query).Select(hit => new
        {
            operation = Compact(hit.Descriptor), hit.Score, hit.MatchedTerms
        });
        return JsonSerializer.SerializeToElement(hits, JsonOptions);
    }

    private JsonElement Browse(JsonElement parameters)
    {
        var domain = OptionalString(parameters, "domain");
        var limit = Math.Clamp(Integer(parameters, "limit", 30), 1, 100);
        if (domain is null)
        {
            var domains = registry.Descriptors
                .GroupBy(descriptor => descriptor.Id.Split('.')[0], StringComparer.OrdinalIgnoreCase)
                .Select(group => new { domain = group.Key, count = group.Count() })
                .OrderBy(group => group.domain, StringComparer.Ordinal)
                .ToArray();
            return JsonSerializer.SerializeToElement(new { total = registry.Descriptors.Count, domains }, JsonOptions);
        }

        var entries = registry.Search(new OperationQuery(Domain: domain, Limit: limit))
            .Select(hit => Compact(hit.Descriptor));
        return JsonSerializer.SerializeToElement(new { domain, operations = entries }, JsonOptions);
    }

    private JsonElement Describe(JsonElement parameters)
    {
        var operationId = RequiredString(parameters, "operationId");
        if (!registry.TryGet(operationId, out var operation))
            throw new BridgeException("operation_not_found", $"Unknown operation '{operationId}'.");
        return JsonSerializer.SerializeToElement(operation.Descriptor, JsonOptions);
    }

    private async Task<JsonElement> ValidateAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var operationId = RequiredString(parameters, "operationId");
        if (!registry.TryGet(operationId, out var operation))
            throw new BridgeException("operation_not_found", $"Unknown operation '{operationId}'.");
        var arguments = RequiredObject(parameters, "arguments");
        var workspace = await baseContext.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var issues = new List<object>();
        var expected = OptionalString(parameters, "expectedRevision");
        if (operation.Descriptor.Risk != OperationRisk.ReadOnly && string.IsNullOrWhiteSpace(expected))
            issues.Add(new { code = "workspace_revision_required", message = "A current workspace revision is required for write operations." });
        else if (operation.Descriptor.Risk != OperationRisk.ReadOnly && expected != workspace.Revision)
            issues.Add(new { code = "workspace_revision_mismatch", message = $"Expected '{expected}', current '{workspace.Revision}'." });
        foreach (var issue in OperationArgumentValidator.Validate(arguments, operation.Descriptor.InputSchema))
            issues.Add(new { code = "invalid_arguments", message = $"{issue.Path}: {issue.Message}" });
        return JsonSerializer.SerializeToElement(new
        {
            valid = issues.Count == 0, operation = Compact(operation.Descriptor), workspace.Revision, issues,
            requiresConfirmation = operation.Descriptor.RequiresConfirmation
        }, JsonOptions);
    }

    private async Task<JsonElement> InvokeAsync(JsonElement parameters, string correlationId, CancellationToken cancellationToken)
    {
        var operationId = RequiredString(parameters, "operationId");
        var arguments = RequiredObject(parameters, "arguments");
        var expectedRevision = OptionalString(parameters, "expectedRevision");
        var idempotencyKey = OptionalString(parameters, "idempotencyKey");
        var request = new OperationRequest(
            operationId,
            arguments,
            expectedRevision,
            OptionalString(parameters, "confirmationToken"),
            idempotencyKey);
        OperationResult result;
        if (idempotencyKey is null)
        {
            result = await ExecuteAsync(request, correlationId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (idempotencyKey.Length > 128)
                throw new BridgeException("invalid_idempotency_key", "Idempotency keys must be at most 128 characters.");
            if (_idempotency.Count >= 2048 && !_idempotency.ContainsKey(idempotencyKey))
                throw new BridgeException("idempotency_capacity", "This Pro session reached its 2048-key idempotency limit.");
            var fingerprint = Fingerprint(operationId, arguments, expectedRevision);
            var candidate = new IdempotencyEntry(
                fingerprint,
                new Lazy<Task<OperationResult>>(
                    () => ExecuteAsync(request, correlationId, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            var entry = _idempotency.GetOrAdd(idempotencyKey, candidate);
            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new BridgeException("idempotency_conflict", "The idempotency key was already used with a different operation, arguments, or workspace revision.");
            try
            {
                result = await entry.Result.Value.ConfigureAwait(false);
            }
            catch
            {
                _idempotency.TryRemove(new KeyValuePair<string, IdempotencyEntry>(idempotencyKey, entry));
                throw;
            }
        }
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private async Task<JsonElement> ListWorkflowsAsync(CancellationToken cancellationToken)
    {
        var definitions = await workflows.ListAsync(cancellationToken).ConfigureAwait(false);
        var rankings = await workflows.RankAsync(cancellationToken).ConfigureAwait(false);
        var rankingByKey = rankings.ToDictionary(rank => (rank.WorkflowId, rank.Version));
        var items = definitions.Select(workflow => new
        {
            workflow.Id, workflow.Version, workflow.Title, workflow.Summary, workflow.Tags,
            ranking = rankingByKey.GetValueOrDefault((workflow.Id, workflow.Version))
        });
        return JsonSerializer.SerializeToElement(items, JsonOptions);
    }

    private async Task<JsonElement> GetWorkflowAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "workflowId");
        var workflow = await workflows.GetAsync(id, OptionalString(parameters, "version"), cancellationToken).ConfigureAwait(false)
            ?? throw new BridgeException("workflow_not_found", $"Workflow '{id}' was not found.");
        return JsonSerializer.SerializeToElement(workflow, JsonOptions);
    }

    private async Task<JsonElement> SaveWorkflowAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("workflow", out var element) || element.ValueKind != JsonValueKind.Object)
            throw new BridgeException("invalid_workflow", "workflow must be a JSON object.");
        var workflow = element.Deserialize<WorkflowDefinition>(JsonOptions)
            ?? throw new BridgeException("invalid_workflow", "Workflow definition could not be parsed.");
        await workflows.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { saved = true, workflow.Id, workflow.Version }, JsonOptions);
    }

    private async Task<JsonElement> RunWorkflowAsync(JsonElement parameters, string correlationId, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "workflowId");
        var workflow = await workflows.GetAsync(id, null, cancellationToken).ConfigureAwait(false)
            ?? throw new BridgeException("workflow_not_found", $"Workflow '{id}' was not found.");
        var validation = await workflows.ValidateAsync(workflow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
            throw new BridgeException("invalid_workflow", string.Join(" ", validation.Issues.Select(issue => issue.Message)));
        var suppliedParameters = RequiredObject(parameters, "parameters");
        IReadOnlyDictionary<string, JsonElement> boundParameters;
        try
        {
            boundParameters = WorkflowBinder.BindParameters(workflow, suppliedParameters);
        }
        catch (ArgumentException exception)
        {
            throw new BridgeException("invalid_workflow_parameters", exception.Message, false, exception);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();
        var revision = OptionalString(parameters, "expectedRevision");
        foreach (var step in workflow.Steps)
        {
            if (step.DependsOn.Any(dependency => !completed.Contains(dependency)))
                throw new BridgeException("workflow_order_invalid", $"Step '{step.Id}' appears before one of its dependencies.");
            JsonElement stepArguments;
            try
            {
                stepArguments = WorkflowBinder.ResolveArguments(step.Arguments, boundParameters);
            }
            catch (ArgumentException exception)
            {
                throw new BridgeException("invalid_workflow_parameters", exception.Message, false, exception);
            }
            var result = await ExecuteAsync(new OperationRequest(step.Operation, stepArguments, revision), $"{correlationId}:{step.Id}", cancellationToken).ConfigureAwait(false);
            results.Add(new { step = step.Id, operation = step.Operation, result.Success, result.ErrorCode, result.Message, result.WorkspaceRevision, result.Data, result.Resources });
            revision = result.WorkspaceRevision;
            if (!result.Success && !step.ContinueOnError)
            {
                await RecordRunAsync("failed", startedAt, completed.Count, 1).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new { success = false, workflow.Id, workflow.Version, results, revision }, JsonOptions);
            }
            completed.Add(step.Id);
        }
        await RecordRunAsync("succeeded", startedAt, completed.Count, 0).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new { success = true, workflow.Id, workflow.Version, results, revision }, JsonOptions);

        async Task RecordRunAsync(string outcome, DateTimeOffset start, int succeeded, int failed) =>
            await workflows.RecordRunAsync(new WorkflowRunSummary(
                Guid.NewGuid().ToString("N"), workflow.Id, workflow.Version, start, DateTimeOffset.UtcNow, outcome, succeeded, failed), cancellationToken).ConfigureAwait(false);
    }

    private Task<OperationResult> ExecuteAsync(OperationRequest request, string correlationId, CancellationToken cancellationToken) =>
        new OperationExecutor(registry, baseContext with { CorrelationId = correlationId }).ExecuteAsync(request, cancellationToken);

    private static object Compact(OperationDescriptor descriptor) => new
    {
        descriptor.Id, descriptor.Version, descriptor.Title, descriptor.Summary, descriptor.Risk,
        descriptor.ExecutionTarget, descriptor.Tags, descriptor.RequiredCapabilities, descriptor.RequiresConfirmation,
        descriptor.TypicalDuration
    };

    private static JsonElement RequiredObject(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : throw new BridgeException("invalid_parameters", $"'{name}' must be a JSON object.");

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name) ?? throw new BridgeException("invalid_parameters", $"'{name}' is required.");

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static int Integer(JsonElement parameters, string name, int defaultValue) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : defaultValue;

    private static string Fingerprint(string operationId, JsonElement arguments, string? expectedRevision)
    {
        var material = $"{operationId}\n{expectedRevision}\n{arguments.GetRawText()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static BridgeResponse Success(string requestId, JsonElement result) =>
        new(BridgeProtocol.Version, requestId, true, result.Clone(), null, DateTimeOffset.UtcNow);

    private static BridgeResponse Failure(string requestId, string code, string message, bool retryable = false) =>
        new(BridgeProtocol.Version, requestId, false, null, new BridgeError(code, message, retryable), DateTimeOffset.UtcNow);

    private sealed record IdempotencyEntry(string Fingerprint, Lazy<Task<OperationResult>> Result);
}
