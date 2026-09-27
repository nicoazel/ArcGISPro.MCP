using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Execution;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Workflows;

namespace ArcGISProMCP.AddIn.Bridge;

internal sealed class ProBridgeRequestHandler(
    IOperationRegistry registry,
    OperationContext baseContext,
    IWorkflowLibrary workflows,
    ProResourceStore resources,
    BridgeAccessState access) : IBridgeRequestHandler, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.Options;
    private const int MaximumApprovalWaitSeconds = 120;
    private readonly ConcurrentDictionary<string, IdempotencyEntry> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowIdempotencyEntry> _workflowIdempotency = new(StringComparer.Ordinal);
    private readonly object _idempotencyGate = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private int _runningOperations;
    public int RunningOperationCount => Volatile.Read(ref _runningOperations);

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
                "approval.request" => await RequestApprovalAsync(parameters, cancellationToken).ConfigureAwait(false),
                "approval.status" => await ApprovalStatusAsync(parameters, cancellationToken).ConfigureAwait(false),
                "approval.cancel" => CancelApproval(parameters),
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
        catch (OperationCanceledException) when (baseContext.ApplicationStopping.IsCancellationRequested)
        {
            // Checked before caller cancellation: the pipe server cancels in-flight requests with its own
            // stopping token on shutdown, and keyed work runs under the host lifetime.
            return Failure(request.RequestId, "host_stopping", "ArcGIS Pro is shutting down, so the request did not complete. A write may already have been accepted; check state on the next host before repeating it.", true);
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
        return JsonSerializer.SerializeToElement(new SystemStateResult(
            BridgeProtocol.Version,
            Environment.ProcessId,
            workspace,
            registry.Descriptors.Count,
            RunningOperationCount,
            access.Enabled), JsonOptions);
    }

    private JsonElement Search(JsonElement parameters)
    {
        var query = new OperationQuery(
            OptionalString(parameters, "query"),
            OptionalString(parameters, "domain"),
            OptionalStringSet(parameters, "capabilities"),
            OptionalRisk(parameters, "maxRisk"),
            Integer(parameters, "limit", 12));
        RegistrySearchHit[] hits = [.. registry.Search(query).Select(hit =>
            new RegistrySearchHit(OperationSummary.From(hit.Descriptor), hit.Score, [.. hit.MatchedTerms]))];
        return JsonSerializer.SerializeToElement(hits, JsonOptions);
    }

    private JsonElement Browse(JsonElement parameters)
    {
        var domain = OptionalString(parameters, "domain");
        var limit = Math.Clamp(Integer(parameters, "limit", 30), 1, 100);
        if (domain is null)
        {
            RegistryDomainCount[] domains = [.. registry.Descriptors
                .GroupBy(descriptor => descriptor.Id.Split('.')[0], StringComparer.OrdinalIgnoreCase)
                .Select(group => new RegistryDomainCount(group.Key, group.Count()))
                .OrderBy(group => group.Domain, StringComparer.Ordinal)];
            return JsonSerializer.SerializeToElement(
                new RegistryBrowseResult(registry.Descriptors.Count, domains, null, null), JsonOptions);
        }

        OperationSummary[] entries = [.. registry.Search(new OperationQuery(Domain: domain, Limit: limit))
            .Select(hit => OperationSummary.From(hit.Descriptor))];
        return JsonSerializer.SerializeToElement(new RegistryBrowseResult(null, null, domain, entries), JsonOptions);
    }

    private JsonElement Describe(JsonElement parameters)
    {
        var operationId = RequiredString(parameters, "operationId");
        if (!registry.TryGet(operationId, out var operation))
            throw new BridgeException("operation_not_found", $"Unknown operation '{operationId}'.");
        return JsonSerializer.SerializeToElement(OperationDescription.From(operation.Descriptor), JsonOptions);
    }

    private async Task<JsonElement> ValidateAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var operationId = RequiredString(parameters, "operationId");
        if (!registry.TryGet(operationId, out var operation))
            throw new BridgeException("operation_not_found", $"Unknown operation '{operationId}'.");
        var arguments = RequiredObject(parameters, "arguments");
        var workspace = await baseContext.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var issues = new List<ValidationIssue>();
        var expected = OptionalString(parameters, "expectedRevision");
        if (operation.Descriptor.Risk != OperationRisk.ReadOnly && string.IsNullOrWhiteSpace(expected))
            issues.Add(new("workspace_revision_required", "A current workspace revision is required for write operations."));
        else if (operation.Descriptor.Risk != OperationRisk.ReadOnly && expected != workspace.Revision)
            issues.Add(new("workspace_revision_mismatch", $"Expected '{expected}', current '{workspace.Revision}'."));
        foreach (var issue in OperationArgumentValidator.Validate(arguments, operation.Descriptor.InputSchema))
            issues.Add(new("invalid_arguments", $"{issue.Path}: {issue.Message}"));
        return JsonSerializer.SerializeToElement(new RegistryValidationResult(
            issues.Count == 0,
            OperationSummary.From(operation.Descriptor),
            workspace.Revision,
            issues,
            operation.Descriptor.RequiresConfirmation), JsonOptions);
    }

    private async Task<JsonElement> InvokeAsync(JsonElement parameters, string correlationId, CancellationToken cancellationToken)
    {
        var operationId = RequiredString(parameters, "operationId");
        var arguments = RequiredObject(parameters, "arguments");
        var expectedRevision = OptionalString(parameters, "expectedRevision");
        var idempotencyKey = OptionalString(parameters, "idempotencyKey");
        var dryRun = Boolean(parameters, "dryRun", false);
        // A dry run executes nothing, so there is nothing to deduplicate; caching it under a key
        // would also let a later real invocation with that key replay the dry-run result.
        if (dryRun && idempotencyKey is not null)
            throw new BridgeException("dry_run_idempotency_conflict", "dryRun cannot be combined with idempotencyKey; a dry run executes nothing and is never cached.");
        var request = new OperationRequest(
            operationId,
            arguments,
            expectedRevision,
            OptionalString(parameters, "confirmationToken"),
            idempotencyKey,
            dryRun);
        OperationResult result;
        if (idempotencyKey is null)
        {
            result = await ExecuteAsync(request, correlationId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (idempotencyKey.Length > 128)
                throw new BridgeException("invalid_idempotency_key", "Idempotency keys must be at most 128 characters.");
            var fingerprint = Fingerprint(operationId, arguments, expectedRevision);
            // Keyed work is shared by every caller presenting the same key, so it must not be bound
            // to whichever caller happened to arrive first. It runs under the host lifetime; each
            // caller's own token only stops that caller from waiting.
            var candidate = new IdempotencyEntry(
                fingerprint,
                new Lazy<Task<OperationResult>>(
                    () => ExecuteAsync(request, correlationId, baseContext.ApplicationStopping),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            IdempotencyEntry entry;
            lock (_idempotencyGate)
            {
                if (!_idempotency.TryGetValue(idempotencyKey, out entry!))
                {
                    if (_idempotency.Count + _workflowIdempotency.Count >= 2048)
                        throw new BridgeException("idempotency_capacity", "This Pro session reached its 2048-key idempotency limit. Existing keys remain valid until restart.");
                    _idempotency[idempotencyKey] = entry = candidate;
                }
            }
            if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new BridgeException("idempotency_conflict", "The idempotency key was already used with a different operation, arguments, or workspace revision.");
            // Keep faulted executions cached too: an exception may follow an accepted write.
            // Eviction must not turn an uncertain outcome into an accidental duplicate mutation.
            result = await entry.Result.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.ErrorCode == "confirmation_required")
                _idempotency.TryRemove(new KeyValuePair<string, IdempotencyEntry>(idempotencyKey, entry));
        }
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private IApprovalService Approvals => baseContext.Confirmation as IApprovalService
        ?? throw new BridgeException("approval_unavailable", "This host has no interactive approval service.");

    private async Task<JsonElement> RequestApprovalAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var operationId = RequiredString(parameters, "operationId");
        if (!registry.TryGet(operationId, out var operation))
            throw new BridgeException("operation_not_found", $"Unknown operation '{operationId}'.");
        var arguments = RequiredObject(parameters, "arguments");
        var issues = OperationArgumentValidator.Validate(arguments, operation.Descriptor.InputSchema);
        if (issues.Count != 0)
            throw new BridgeException("invalid_arguments", string.Join(" ", issues.Select(issue => $"{issue.Path}: {issue.Message}")));
        var workspace = await baseContext.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (RequiredString(parameters, "expectedRevision") != workspace.Revision)
            throw new BridgeException("workspace_revision_mismatch", "Refresh workspace state before requesting approval.");
        return SerializeApproval(Approvals.Request(operation.Descriptor, arguments, workspace));
    }

    /// <summary>
    /// Returns the request's status. With <c>waitSeconds</c> (clamped to 0-120) a pending request is
    /// held until its state changes or the wait elapses, and the current status is returned either
    /// way; waiting never decides anything. Other bridge requests keep running meanwhile.
    /// </summary>
    private async Task<JsonElement> ApprovalStatusAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var approvals = Approvals;
        var requestId = RequiredString(parameters, "requestId");
        var waitSeconds = Math.Clamp(Integer(parameters, "waitSeconds", 0), 0, MaximumApprovalWaitSeconds);
        var approval = GetApproval(approvals, requestId);
        if (waitSeconds == 0 || approval.State != ApprovalRequestState.Pending)
            return SerializeApproval(approval);

        var changed = NewSignal();
        void OnChanged(object? sender, EventArgs args) => Volatile.Read(ref changed).TrySetResult();
        approvals.Changed += OnChanged;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, baseContext.ApplicationStopping);
            wait.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
            while (true)
            {
                // Arm a fresh signal before reading the state, so a change that lands between the
                // read and the await is never lost.
                var signal = NewSignal();
                Volatile.Write(ref changed, signal);
                approval = GetApproval(approvals, requestId);
                if (approval.State != ApprovalRequestState.Pending)
                    return SerializeApproval(approval);
                try
                {
                    await signal.Task.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
                                                         !baseContext.ApplicationStopping.IsCancellationRequested)
                {
                    // The wait elapsed: report whatever the state is now (usually still pending).
                    return SerializeApproval(GetApproval(approvals, requestId));
                }
            }
        }
        finally
        {
            approvals.Changed -= OnChanged;
        }

        static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static ApprovalRequestSnapshot GetApproval(IApprovalService approvals, string requestId) =>
        approvals.GetStatus(requestId)
        ?? throw new BridgeException("approval_not_found", "Approval is unknown or no longer retained in this host session.");

    private JsonElement CancelApproval(JsonElement parameters)
    {
        var requestId = RequiredString(parameters, "requestId");
        return JsonSerializer.SerializeToElement(new ApprovalCancelResult(requestId, Approvals.TryCancel(requestId)), JsonOptions);
    }

    private static JsonElement SerializeApproval(ApprovalRequestSnapshot approval) => JsonSerializer.SerializeToElement(new ApprovalStatusResult(
        approval.Id,
        approval.OperationId,
        approval.OperationVersion,
        approval.WorkspaceRevision,
        approval.State.ToString().ToLowerInvariant(),
        approval.RequestedAt,
        approval.ExpiresAt,
        approval.ConfirmationToken,
        approval.State == ApprovalRequestState.Pending
            ? "A person must approve or deny this exact request in the ArcGIS Pro MCP panel. Call approval_status again, optionally with waitSeconds (up to 120) to wait for the decision; there is no remote approval operation."
            : null), JsonOptions);

    private async Task<JsonElement> ListWorkflowsAsync(CancellationToken cancellationToken)
    {
        var definitions = await workflows.ListAsync(cancellationToken).ConfigureAwait(false);
        var rankings = await workflows.RankAsync(cancellationToken).ConfigureAwait(false);
        var rankingByKey = rankings.ToDictionary(rank => (rank.WorkflowId, rank.Version));
        WorkflowSummary[] items = [.. definitions.Select(workflow => new WorkflowSummary(
            workflow.Id,
            workflow.Version,
            workflow.Title,
            workflow.Summary,
            [.. workflow.Tags],
            rankingByKey.GetValueOrDefault((workflow.Id, workflow.Version))))];
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
        try
        {
            await workflows.SaveAsync(workflow, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new BridgeException("invalid_workflow", exception.Message);
        }
        return JsonSerializer.SerializeToElement(new WorkflowSaveResult(true, workflow.Id, workflow.Version), JsonOptions);
    }

    private async Task<JsonElement> RunWorkflowAsync(JsonElement parameters, string correlationId, CancellationToken cancellationToken)
    {
        var id = RequiredString(parameters, "workflowId");
        var key = OptionalString(parameters, "idempotencyKey");
        var requestedVersion = OptionalString(parameters, "version");
        if (key is not null && (key.Length > 128 || string.IsNullOrWhiteSpace(key)))
            throw new BridgeException("invalid_idempotency_key", "Idempotency keys must contain 1 to 128 characters.");
        if (key is not null && string.IsNullOrWhiteSpace(requestedVersion))
            throw new BridgeException("workflow_version_required", "Pin the immutable workflow version when using an idempotency key.");
        var workflow = await workflows.GetAsync(id, requestedVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new BridgeException("workflow_not_found", $"Workflow '{id}' was not found.");
        Task<JsonElement> Run(CancellationToken token) => WithExecutionGateAsync(
            () => RunWorkflowCoreAsync(workflow, parameters, correlationId, token), token);
        if (key is null) return await Run(cancellationToken).ConfigureAwait(false);
        var fingerprint = Fingerprint(id + "@" + workflow.Version, RequiredObject(parameters, "parameters"), OptionalString(parameters, "expectedRevision"));
        WorkflowIdempotencyEntry entry;
        lock (_idempotencyGate)
        {
            if (!_workflowIdempotency.TryGetValue(key, out entry!))
            {
                if (_workflowIdempotency.Count + _idempotency.Count >= 2048)
                    throw new BridgeException("idempotency_capacity", "This Pro session reached its 2048-key idempotency limit.");
                _workflowIdempotency[key] = entry = new(fingerprint, new Lazy<Task<JsonElement>>(
                    // Shared keyed run: bound to the host lifetime, not the first caller's token.
                    () => Run(baseContext.ApplicationStopping), LazyThreadSafetyMode.ExecutionAndPublication));
            }
        }
        if (entry.Fingerprint != fingerprint)
            throw new BridgeException("idempotency_conflict", "The workflow key is bound to different parameters, version or revision.");
        return await entry.Result.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> RunWorkflowCoreAsync(WorkflowDefinition workflow, JsonElement parameters, string correlationId, CancellationToken cancellationToken)
    {
        var validation = await workflows.ValidateAsync(workflow, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
            throw new BridgeException("invalid_workflow", string.Join(" ", validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
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

        // Check the caller's original revision before any step can refresh it. Otherwise a
        // leading read (or a continued stale-write failure) could launder a stale/missing
        // revision into a current revision and authorize subsequent workflow mutations.
        var revision = OptionalString(parameters, "expectedRevision");
        if (workflow.Steps.Any(step => registry.TryGet(step.Operation, out var operation)
                                      && operation.Descriptor.Risk != OperationRisk.ReadOnly))
        {
            if (string.IsNullOrWhiteSpace(revision))
                throw new BridgeException("workspace_revision_required", "A current initial workspace revision is required for a workflow containing writes.");
            var initialWorkspace = await baseContext.Workspace.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(revision, initialWorkspace.Revision, StringComparison.Ordinal))
                throw new BridgeException("workspace_revision_mismatch", "The workflow's initial revision is stale. Refresh state and review before starting it.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failedSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeededCount = 0;
        var failedCount = 0;
        string? historyWarning = null;
        var results = new List<WorkflowStepResult>();
        for (var stepIndex = 0; stepIndex < workflow.Steps.Length; stepIndex++)
        {
            var step = workflow.Steps[stepIndex];
            if (step.DependsOn.Any(dependency => !completed.Contains(dependency)))
                throw new BridgeException("workflow_order_invalid", $"Step '{step.Id}' appears before one of its dependencies.");
            if (step.DependsOn.Any(failedSteps.Contains))
            {
                results.Add(WorkflowStepResult.Skipped(step.Id, step.Operation, "dependency_failed", "Skipped because a required step failed."));
                completed.Add(step.Id);
                failedSteps.Add(step.Id);
                failedCount++;
                continue;
            }
            JsonElement stepArguments;
            try
            {
                stepArguments = WorkflowBinder.ResolveArguments(step.Arguments, boundParameters);
            }
            catch (ArgumentException exception)
            {
                throw new BridgeException("invalid_workflow_parameters", exception.Message, false, exception);
            }
            var result = await ExecuteCoreAsync(new OperationRequest(step.Operation, stepArguments, revision), $"{correlationId}:{step.Id}", cancellationToken).ConfigureAwait(false);
            if (!result.Success &&
                string.Equals(result.ErrorCode, "workspace_revision_mismatch", StringComparison.Ordinal))
            {
                // The workspace changed between serialized steps (a person edited the project, or
                // ArcGIS Pro published a delayed notification). The mismatch is detected before the
                // step executes, so nothing ran. Never silently adopt the newer revision: that would
                // authorize the remaining writes against state nobody reviewed. Stop regardless of
                // ContinueOnError and let the caller refresh state and decide how to proceed.
                results.Add(WorkflowStepResult.From(step.Id, step.Operation, result));
                failedCount++;
                await RecordRunAsync("failed", startedAt, succeededCount, failedCount).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(new WorkflowRunResult(
                    false,
                    "workspace_changed",
                    $"The workspace changed before step '{step.Id}' (index {stepIndex}) executed. Completed steps are not rolled back. Refresh state with system_get_state and review before continuing.",
                    workflow.Id,
                    workflow.Version,
                    StoppedAtStep: step.Id,
                    StepIndex: stepIndex,
                    ExpectedRevision: revision,
                    CurrentRevision: result.WorkspaceRevision,
                    results,
                    Revision: result.WorkspaceRevision,
                    historyWarning), JsonOptions);
            }
            results.Add(WorkflowStepResult.From(step.Id, step.Operation, result));
            // Only a successful write advances the expected revision, to the revision it produced.
            // A read-only step or a failed (continueOnError) step reports whatever the workspace
            // currently is; adopting that would launder an unreviewed mid-run change into
            // authorization for the remaining writes.
            if (result.Success && registry.TryGet(step.Operation, out var executed) &&
                executed.Descriptor.Risk != OperationRisk.ReadOnly)
                revision = result.WorkspaceRevision;
            if (result.Success) succeededCount++;
            else { failedCount++; failedSteps.Add(step.Id); }
            if (!result.Success && !step.ContinueOnError)
            {
                await RecordRunAsync("failed", startedAt, succeededCount, failedCount).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(RunResult(false), JsonOptions);
            }
            completed.Add(step.Id);
        }
        await RecordRunAsync(failedCount == 0 ? "succeeded" : "failed", startedAt, succeededCount, failedCount).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(RunResult(failedCount == 0), JsonOptions);

        WorkflowRunResult RunResult(bool success) => new(
            success, null, null, workflow.Id, workflow.Version, null, null, null, null, results, revision, historyWarning);

        async Task RecordRunAsync(string outcome, DateTimeOffset start, int succeeded, int failed)
        {
            try
            {
                await workflows.RecordRunAsync(new WorkflowRunSummary(
                    Guid.NewGuid().ToString("N"), workflow.Id, workflow.Version, start, DateTimeOffset.UtcNow, outcome, succeeded, failed), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.TraceError("Workflow history write failed: {0}", exception);
                historyWarning = "Workflow outcome is known but its history record could not be persisted.";
            }
        }
    }

    private Task<OperationResult> ExecuteAsync(OperationRequest request, string correlationId, CancellationToken cancellationToken) =>
        WithExecutionGateAsync(() => ExecuteCoreAsync(request, correlationId, cancellationToken), cancellationToken);

    private Task<OperationResult> ExecuteCoreAsync(OperationRequest request, string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!access.Enabled) throw new BridgeException("bridge_disabled", "MCP access was disconnected before this operation started.");
        return new OperationExecutor(registry, baseContext with { CorrelationId = correlationId }).ExecuteAsync(request, cancellationToken);
    }

    private async Task<T> WithExecutionGateAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _runningOperations);
        try
        {
            // Recheck access and workspace revision after acquiring the resource-owner gate.
            if (!access.Enabled) throw new BridgeException("bridge_disabled", "MCP access was disconnected while this request was queued.");
            return await action().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _runningOperations);
            _executionGate.Release();
        }
    }

    private static JsonElement RequiredObject(JsonElement parameters, string name) =>
        parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Clone()
            : throw new BridgeException("invalid_parameters", $"'{name}' must be a JSON object.");

    private static string RequiredString(JsonElement parameters, string name) =>
        OptionalString(parameters, name) ?? throw new BridgeException("invalid_parameters", $"'{name}' is required.");

    private static string? OptionalString(JsonElement parameters, string name) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static ImmutableHashSet<string>? OptionalStringSet(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Array)
            throw new BridgeException("invalid_parameters", $"'{name}' must be an array of strings.");
        var items = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new BridgeException("invalid_parameters", $"'{name}' must be an array of non-empty strings.");
            items.Add(item.GetString()!.Trim());
        }
        return items.Count == 0 ? null : items.ToImmutable();
    }

    private static OperationRisk? OptionalRisk(JsonElement parameters, string name)
    {
        var text = OptionalString(parameters, name);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        // Accept ReadOnly, readOnly, read_only, read-only; reject numeric aliases.
        var normalized = text.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        foreach (var risk in Enum.GetValues<OperationRisk>())
        {
            if (string.Equals(risk.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
                return risk;
        }
        throw new BridgeException("invalid_parameters",
            $"'{name}' must be one of {string.Join(", ", Enum.GetNames<OperationRisk>())}.");
    }

    private static int Integer(JsonElement parameters, string name, int defaultValue) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : defaultValue;

    private static bool Boolean(JsonElement parameters, string name, bool defaultValue) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : defaultValue;

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
    private sealed record WorkflowIdempotencyEntry(string Fingerprint, Lazy<Task<JsonElement>> Result);

    // Owner must first stop accepting and drain the bridge before disposing its handler.
    public void Dispose() => _executionGate.Dispose();
}
