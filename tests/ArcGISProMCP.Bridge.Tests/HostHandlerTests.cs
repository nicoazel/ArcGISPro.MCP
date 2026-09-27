using System.Text.Json;
using ArcGISProMCP.AddIn.Bridge;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Bridge.Protocol;
using ArcGISProMCP.Core.Approvals;
using ArcGISProMCP.Core.Operations;
using ArcGISProMCP.Core.Registry;
using ArcGISProMCP.Core.Workflows;
using ArcGISProMCP.Core.Workspaces;
using Xunit;

namespace ArcGISProMCP.Bridge.Tests;

public sealed class HostHandlerTests
{
    private static readonly string[] MapsOnly = ["MAPS"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] MapsAndLayouts = ["maps", "layouts"];

    [Fact]
    public async Task Approval_is_local_single_use_and_bound_to_revision()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var denied = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", idempotencyKey = "same" });
        Assert.Equal("confirmation_required", denied.Result!.Value.GetProperty("errorCode").GetString());
        var stale = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "old" });
        Assert.Equal("workspace_revision_mismatch", stale.Error!.Code);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        Assert.Equal("pending", pending.Result.Value.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.Result.Value.GetProperty("confirmationToken").ValueKind);
        Assert.Equal("method_not_found", (await fixture.Call("approval.resolve", new { requestId })).Error!.Code);
        Assert.True(fixture.Approvals.TryResolve(requestId, ApprovalResolution.ApproveOnce));
        var status = await fixture.Call("approval.status", new { requestId });
        var token = status.Result!.Value.GetProperty("confirmationToken").GetString();
        var invoke = new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", confirmationToken = token, idempotencyKey = "same" };
        Assert.True((await fixture.Call("registry.invoke", invoke)).Result!.Value.GetProperty("success").GetBoolean());
        Assert.True((await fixture.Call("registry.invoke", invoke)).Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.Operation.CallCount);
        var replay = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", confirmationToken = token });
        Assert.Equal("confirmation_required", replay.Result!.Value.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Concurrent_duplicate_keys_execute_once_and_conflicts_fail()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var request = new { operationId = "test.write", arguments = new { }, expectedRevision = "r1", idempotencyKey = "key" };
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => fixture.Call("registry.invoke", request)));
        Assert.All(responses, response => Assert.True(response.Result!.Value.GetProperty("success").GetBoolean()));
        Assert.Equal(1, fixture.Operation.CallCount);
        var conflict = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "other", idempotencyKey = "key" });
        Assert.Equal("idempotency_conflict", conflict.Error!.Code);
    }

    [Fact]
    public async Task Workflow_save_reports_validation_failures_as_invalid_workflow()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("unknown-flow", "1.0.0", "Unknown", "Test", [], [], [],
            [new("one", "does.not-exist", arguments, [])]);
        var response = await fixture.Call("workflow.save", new { workflow });
        Assert.Equal("invalid_workflow", response.Error!.Code);
    }

    [Fact]
    public async Task Workflow_run_rejects_an_on_disk_user_code_step_without_an_allowlist()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        // Bypass save-time validation, as a hand-edited or older library file would.
        var workflow = new WorkflowDefinition("disk-flow", "1.0.0", "Disk", "Hand-edited file", [], [], [],
            [new("script", fixture.ScriptOperation.Descriptor.Id, JsonSerializer.SerializeToElement(new { }), [])]);
        Directory.CreateDirectory(fixture.WorkflowDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.WorkflowDirectory, "disk-flow@1.0.0.workflow.json"),
            JsonSerializer.Serialize(workflow, WebJson),
            TestContext.Current.CancellationToken);

        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });

        Assert.Equal("invalid_workflow", response.Error!.Code);
        Assert.Contains("operation_not_allowed", response.Error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.ScriptOperation.CallCount);
    }

    [Fact]
    public async Task Search_and_browse_report_whether_an_operation_executes_user_code()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);

        var search = await fixture.Call("registry.search", new { query = "script" });
        var browse = await fixture.Call("registry.browse", new { domain = "sample" });

        var hit = Assert.Single(search.Result!.Value.EnumerateArray()).GetProperty("operation");
        Assert.True(hit.GetProperty("executesUserCode").GetBoolean());
        var operations = browse.Result!.Value.GetProperty("operations").EnumerateArray().ToArray();
        Assert.Contains(operations, operation => operation.GetProperty("id").GetString() == "sample.script" &&
                                                 operation.GetProperty("executesUserCode").GetBoolean());
        Assert.Contains(operations, operation => operation.GetProperty("id").GetString() == "sample.project-save" &&
                                                 !operation.GetProperty("executesUserCode").GetBoolean());
    }

    [Theory]
    [InlineData("sample.feature-update")]
    [InlineData("sample.project-save")]
    public async Task Confirmation_gated_safe_write_requires_an_approved_token(string operationId)
    {
        // Mirrors feature.update and project.save: SafeWrite risk plus requiresConfirmation.
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var operation = fixture.GatedOperations[operationId];

        var denied = await fixture.Call("registry.invoke", new { operationId, arguments = new { }, expectedRevision = "r1" });
        Assert.Equal("confirmation_required", denied.Result!.Value.GetProperty("errorCode").GetString());
        Assert.Equal(0, operation.CallCount);

        var pending = await fixture.Call("approval.request", new { operationId, arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        Assert.True(fixture.Approvals.TryResolve(requestId, ApprovalResolution.ApproveOnce));
        var token = (await fixture.Call("approval.status", new { requestId })).Result!.Value.GetProperty("confirmationToken").GetString();
        var approved = await fixture.Call("registry.invoke", new { operationId, arguments = new { }, expectedRevision = "r1", confirmationToken = token });

        Assert.True(approved.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, operation.CallCount);
    }

    [Fact]
    public async Task Workflow_continuation_preserves_failure_and_skips_dependants()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        fixture.Operation.Fail = true;
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("test-flow", "1.0.0", "Test", "Test", [], [], [],
            [new("one", "test.write", arguments, [], true), new("two", "test.write", arguments, ["one"])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });
        Assert.False(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.Operation.CallCount);
        Assert.Equal("dependency_failed", response.Result.Value.GetProperty("results")[1].GetProperty("errorCode").GetString());
        var rank = Assert.Single(await fixture.Workflows.RankAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, rank.SuccessfulRuns);
        Assert.Equal(1, rank.FailedRuns);
    }

    [Fact]
    public async Task Workflow_retry_key_replays_result_and_requires_immutable_version()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("retry-flow", "1.0.0", "Retry", "Test", [], [], [],
            [new("one", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var unpinned = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1", idempotencyKey = "key" });
        Assert.Equal("workflow_version_required", unpinned.Error!.Code);
        var request = new { workflowId = workflow.Id, version = "1.0.0", parameters = new { }, expectedRevision = "r1", idempotencyKey = "key" };
        var runs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => fixture.Call("workflow.run", request)));
        Assert.All(runs, run => Assert.True(run.Result!.Value.GetProperty("success").GetBoolean()));
        Assert.Equal(1, fixture.Operation.CallCount);
        var rank = Assert.Single(await fixture.Workflows.RankAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, rank.SuccessfulRuns);
        var conflict = await fixture.Call("workflow.run", new { workflowId = workflow.Id, version = "1.0.0", parameters = new { }, expectedRevision = "changed", idempotencyKey = "key" });
        Assert.Equal("idempotency_conflict", conflict.Error!.Code);
    }

    [Theory]
    [InlineData(null, "workspace_revision_required")]
    [InlineData("old", "workspace_revision_mismatch")]
    public async Task Workflow_rejects_initial_revision_before_a_leading_read_can_refresh_it(string? revision, string error)
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("guarded-flow", "1.0.0", "Guarded", "Test", [], [], [],
            [new("read", "test.read", JsonSerializer.SerializeToElement(new { }), [], true),
             new("write", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = revision });
        Assert.Equal(error, response.Error!.Code);
        Assert.Equal(0, fixture.ReadOperation.CallCount);
        Assert.Equal(0, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Read_only_workflow_does_not_require_a_write_revision()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("read-flow", "1.0.0", "Read", "Test", [], [], [],
            [new("read", "test.read", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);
        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { } });
        Assert.True(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(1, fixture.ReadOperation.CallCount);
    }

    [Fact]
    public async Task Workflow_stops_with_workspace_changed_when_revision_advances_before_a_step()
    {
        // Initial workflow check sees r1; the executor then observes r2 before the first step runs.
        var workspace = new SequenceWorkspace("r1", "r2", "r2");
        using var fixture = new Fixture(OperationRisk.SafeWrite, workspace);
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("settling-flow", "1.0.0", "Settling", "Test", [], [], [],
            [new("write", "test.write", arguments, [], true),
             new("second", "test.write", arguments, [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);

        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });

        var result = response.Result!.Value;
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_changed", result.GetProperty("errorCode").GetString());
        Assert.Equal("write", result.GetProperty("stoppedAtStep").GetString());
        Assert.Equal(0, result.GetProperty("stepIndex").GetInt32());
        Assert.Equal("r1", result.GetProperty("expectedRevision").GetString());
        Assert.Equal("r2", result.GetProperty("currentRevision").GetString());
        // No retry at the newer revision, and ContinueOnError does not let the second step run.
        Assert.Equal(0, fixture.Operation.CallCount);
        var step = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal("workspace_revision_mismatch", step.GetProperty("errorCode").GetString());
        var rank = Assert.Single(await fixture.Workflows.RankAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, rank.SuccessfulRuns);
        Assert.Equal(1, rank.FailedRuns);
    }

    [Fact]
    public async Task Read_only_step_does_not_launder_a_mid_run_revision_change()
    {
        // Initial check r1; the read step's executor and operation observe r2; the write sees r2.
        var workspace = new SequenceWorkspace("r1", "r2", "r2", "r2");
        using var fixture = new Fixture(OperationRisk.SafeWrite, workspace);
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("laundering-flow", "1.0.0", "Laundering", "Test", [], [], [],
            [new("read", "test.read", arguments, []),
             new("write", "test.write", arguments, [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);

        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });

        var result = response.Result!.Value;
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("workspace_changed", result.GetProperty("errorCode").GetString());
        Assert.Equal("write", result.GetProperty("stoppedAtStep").GetString());
        Assert.Equal("r1", result.GetProperty("expectedRevision").GetString());
        Assert.Equal("r2", result.GetProperty("currentRevision").GetString());
        Assert.Equal(1, fixture.ReadOperation.CallCount);
        Assert.Equal(0, fixture.Operation.CallCount);
    }

    [Fact]
    public async Task Successful_write_advances_the_expected_revision_for_the_next_write()
    {
        // Initial check r1; write one starts at r1 and produces r2; write two starts at r2.
        var workspace = new SequenceWorkspace("r1", "r1", "r2", "r2");
        using var fixture = new Fixture(OperationRisk.SafeWrite, workspace);
        var arguments = JsonSerializer.SerializeToElement(new { });
        var workflow = new WorkflowDefinition("advancing-flow", "1.0.0", "Advancing", "Test", [], [], [],
            [new("one", "test.write", arguments, []),
             new("two", "test.write", arguments, ["one"])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);

        var response = await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" });

        Assert.True(response.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Equal(2, fixture.Operation.CallCount);
    }

    [Theory]
    [InlineData("ReadOnly")]
    [InlineData("read_only")]
    [InlineData("read-only")]
    public async Task Search_max_risk_excludes_write_operations(string maxRisk)
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);

        var all = await fixture.Call("registry.search", new { query = "test" });
        var readOnly = await fixture.Call("registry.search", new { query = "test", maxRisk });

        Assert.Equal(["test.read", "test.write"], OperationIds(all).Order(StringComparer.Ordinal));
        Assert.Equal(["test.read"], OperationIds(readOnly));
    }

    [Fact]
    public async Task Search_capabilities_filter_is_passed_to_the_registry()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);

        var maps = await fixture.Call("registry.search", new { query = "test", capabilities = MapsOnly });
        var none = await fixture.Call("registry.search", new { query = "test", capabilities = MapsAndLayouts });

        Assert.Equal(["test.read"], OperationIds(maps));
        Assert.Empty(OperationIds(none));
    }

    [Theory]
    [InlineData("maxRisk", "\"Dangerous\"")]
    [InlineData("maxRisk", "\"1\"")]
    [InlineData("capabilities", "\"maps\"")]
    [InlineData("capabilities", "[1]")]
    public async Task Search_rejects_malformed_filters(string name, string json)
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var parameters = JsonDocument.Parse($"{{\"query\":\"test\",\"{name}\":{json}}}").RootElement;

        var response = await fixture.Call("registry.search", parameters);

        Assert.Equal("invalid_parameters", response.Error!.Code);
    }

    [Fact]
    public async Task Approval_status_wait_returns_as_soon_as_a_person_decides()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var waiting = fixture.Call("approval.status", new { requestId, waitSeconds = 60 });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.True(fixture.Approvals.TryResolve(requestId, ApprovalResolution.ApproveOnce));
        var status = await waiting;

        Assert.True(status.Success, status.Error?.Message);
        Assert.Equal("approved", status.Result!.Value.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(status.Result.Value.GetProperty("confirmationToken").GetString()));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Approval_status_wait_reports_pending_when_the_wait_elapses()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var status = await fixture.Call("approval.status", new { requestId, waitSeconds = 1 });

        Assert.True(status.Success, status.Error?.Message);
        Assert.Equal("pending", status.Result!.Value.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, status.Result.Value.GetProperty("confirmationToken").ValueKind);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task Approval_status_wait_does_not_hold_decided_or_unknown_requests()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        Assert.True(fixture.Approvals.TryCancel(requestId));

        var cancelled = await fixture.Call("approval.status", new { requestId, waitSeconds = 120 }).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var unknown = await fixture.Call("approval.status", new { requestId = "missing", waitSeconds = 120 }).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("cancelled", cancelled.Result!.Value.GetProperty("status").GetString());
        Assert.Equal("approval_not_found", unknown.Error!.Code);
    }

    [Fact]
    public async Task Approval_status_wait_stops_when_the_caller_cancels()
    {
        using var fixture = new Fixture(OperationRisk.Destructive);
        var pending = await fixture.Call("approval.request", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var requestId = pending.Result!.Value.GetProperty("requestId").GetString()!;
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var status = await fixture.CallWithToken("approval.status", new { requestId, waitSeconds = 120 }, caller.Token)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("request_cancelled", status.Error!.Code);
    }

    [Fact]
    public async Task Describe_returns_a_result_schema_that_accepts_real_invoke_results()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);

        var describe = await fixture.Call("registry.describe", new { operationId = "test.write" });
        var invoke = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" });
        var failed = await fixture.Call("registry.invoke", new { operationId = "test.write", arguments = new { }, expectedRevision = "stale" });

        var description = describe.Result!.Value.Deserialize<OperationDescription>(BridgeJson.Options)!;
        Assert.Equal("test.write", description.Id);
        Assert.Null(description.OutputSchema);
        Assert.Equal(JsonValueKind.Object, description.ResultSchema.ValueKind);
        Assert.Empty(OperationArgumentValidator.Validate(invoke.Result!.Value, description.ResultSchema));
        Assert.False(failed.Result!.Value.GetProperty("success").GetBoolean());
        Assert.Empty(OperationArgumentValidator.Validate(failed.Result.Value, description.ResultSchema));
    }

    [Fact]
    public async Task Bridge_results_round_trip_through_the_shared_contracts()
    {
        using var fixture = new Fixture(OperationRisk.SafeWrite);
        var workflow = new WorkflowDefinition("contract-flow", "1.0.0", "Contract", "Test", [], [], [],
            [new("one", "test.write", JsonSerializer.SerializeToElement(new { }), [])]);
        await fixture.Workflows.SaveAsync(workflow, TestContext.Current.CancellationToken);

        var state = Read<SystemStateResult>(await fixture.Call("system.get_state", new { }));
        var hits = Read<RegistrySearchHit[]>(await fixture.Call("registry.search", new { query = "test" }));
        var root = Read<RegistryBrowseResult>(await fixture.Call("registry.browse", new { }));
        var domain = Read<RegistryBrowseResult>(await fixture.Call("registry.browse", new { domain = "sample" }));
        var validation = Read<RegistryValidationResult>(await fixture.Call("registry.validate", new { operationId = "test.write", arguments = new { }, expectedRevision = "r1" }));
        var workflows = Read<WorkflowSummary[]>(await fixture.Call("workflow.list", new { }));
        var run = Read<WorkflowRunResult>(await fixture.Call("workflow.run", new { workflowId = workflow.Id, parameters = new { }, expectedRevision = "r1" }));

        Assert.Equal("r1", state.Workspace.Revision);
        Assert.Contains(hits, hit => hit.Operation.Id == "test.write" && hit.Operation.Risk == OperationRisk.SafeWrite);
        Assert.NotNull(root.Total);
        Assert.Null(root.Operations);
        Assert.Equal("sample", domain.Domain);
        Assert.NotEmpty(domain.Operations!);
        Assert.True(validation.Valid);
        Assert.Contains(workflows, item => item.Id == workflow.Id);
        Assert.True(run.Success);
        Assert.Null(run.ErrorCode);
        Assert.Equal("one", Assert.Single(run.Results).Step);
    }

    private static T Read<T>(BridgeResponse response)
    {
        Assert.True(response.Success, response.Error?.Message);
        return response.Result!.Value.Deserialize<T>(BridgeJson.Options)!;
    }

    private static string[] OperationIds(BridgeResponse response)
    {
        Assert.True(response.Success, response.Error?.Message);
        return response.Result!.Value.EnumerateArray()
            .Select(hit => hit.GetProperty("operation").GetProperty("id").GetString()!)
            .ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public ApprovalService Approvals { get; } = new();
        public TestOperation Operation { get; }
        public TestOperation ReadOperation { get; } = new(OperationRisk.ReadOnly, "test.read", ["maps"]);
        // Ids and titles avoid the word "test" so they stay out of the registry.search assertions.
        public TestOperation ScriptOperation { get; } = new(OperationRisk.ExternalSideEffect, "sample.script",
            title: "Sample script runner", summary: "Runs a script.", requiresConfirmation: true, executesUserCode: true);
        public IReadOnlyDictionary<string, TestOperation> GatedOperations { get; } = new[]
        {
            new TestOperation(OperationRisk.SafeWrite, "sample.feature-update", title: "Sample feature update", summary: "Updates one feature.", requiresConfirmation: true),
            new TestOperation(OperationRisk.SafeWrite, "sample.project-save", title: "Sample project save", summary: "Saves the project.", requiresConfirmation: true)
        }.ToDictionary(operation => operation.Descriptor.Id, StringComparer.Ordinal);
        public FileWorkflowLibrary Workflows { get; }
        public string WorkflowDirectory => Path.Combine(_root, "workflows");
        private readonly ProBridgeRequestHandler _handler;
        // Test files are isolated and intentionally retained for post-failure diagnosis.
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ArcGISProMCP.Tests", Guid.NewGuid().ToString("N"));
        public Fixture(OperationRisk risk, IWorkspaceStateProvider? workspace = null)
        {
            Operation = new(risk);
            var registry = new OperationRegistry();
            registry.Register(Operation);
            registry.Register(ReadOperation);
            registry.Register(ScriptOperation);
            foreach (var gated in GatedOperations.Values) registry.Register(gated);
            Workflows = new FileWorkflowLibrary(WorkflowDirectory, registry);
            var context = new OperationContext(new Dispatcher(), workspace ?? new Workspace(), Approvals, new Audit(), "test", CancellationToken.None);
            _handler = new ProBridgeRequestHandler(registry, context, Workflows, new ProResourceStore(Path.Combine(_root, "resources")), new BridgeAccessState());
        }
        public Task<BridgeResponse> Call(string method, object parameters) => CallWithToken(method, parameters, TestContext.Current.CancellationToken);
        public Task<BridgeResponse> CallWithToken(string method, object parameters, CancellationToken cancellationToken) => _handler.HandleAsync(
            new(BridgeProtocol.Version, Guid.NewGuid().ToString("N"), method, JsonSerializer.SerializeToElement(parameters), DateTimeOffset.UtcNow), cancellationToken);
        public void Dispose() { _handler.Dispose(); Approvals.Dispose(); Workflows.Dispose(); }
    }

    private sealed class TestOperation(
        OperationRisk risk,
        string id = "test.write",
        string[]? capabilities = null,
        string title = "Test operation",
        string summary = "Test",
        bool? requiresConfirmation = null,
        bool executesUserCode = false) : IOperation
    {
        public int CallCount { get; private set; }
        public bool Fail { get; set; }
        public OperationDescriptor Descriptor { get; } = OperationDescriptor.Create(id, title, summary, JsonSchemas.EmptyObject, risk: risk, capabilities: capabilities,
            requiresConfirmation: requiresConfirmation ?? risk == OperationRisk.Destructive, executesUserCode: executesUserCode);
        public async Task<OperationResult> ExecuteAsync(JsonElement arguments, OperationContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            // Like real operations, report the workspace revision observed after running.
            var revision = (await context.Workspace.GetSnapshotAsync(cancellationToken)).Revision;
            return Fail ? OperationResult.Fail("test_failure", "Deliberate failure", revision) : OperationResult.Ok(JsonSerializer.SerializeToElement(new { }), revision);
        }
    }

    private sealed class Workspace : IWorkspaceStateProvider
    {
        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceSnapshot("r1", DateTimeOffset.UtcNow, new("Test", "test.aprx", false, true), [], [], null, null, []));
    }

    private sealed class SequenceWorkspace(params string[] revisions) : IWorkspaceStateProvider
    {
        private int _index = -1;

        public Task<WorkspaceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _index), revisions.Length - 1);
            return Task.FromResult(new WorkspaceSnapshot(revisions[index], DateTimeOffset.UtcNow, new("Test", "test.aprx", false, true), [], [], null, null, []));
        }
    }
    private sealed class Dispatcher : IOperationDispatcher
    {
        public Task<T> OnMainCimThreadAsync<T>(Func<T> action, CancellationToken cancellationToken) => Task.FromResult(action());
        public Task OnMainCimThreadAsync(Action action, CancellationToken cancellationToken) { action(); return Task.CompletedTask; }
        public Task<T> OnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken) => action();
    }
    private sealed class Audit : IOperationAuditLog
    {
        public ValueTask WriteAsync(OperationAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
