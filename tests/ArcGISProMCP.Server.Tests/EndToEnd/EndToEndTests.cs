using System.Text.Json;
using ArcGISProMCP.Core.Geoprocessing;
using ArcGISProMCP.Testing;
using Xunit;

namespace ArcGISProMCP.Server.Tests.EndToEnd;

/// <summary>
/// MCP client -> gateway -> bridge handler -> registry/executor -> fake ArcGIS services, with the
/// local approval queue in the loop. Every call's structuredContent is checked against its tool's
/// outputSchema by <see cref="EndToEndServer.CallAsync"/>.
/// </summary>
public sealed class EndToEndTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_one_feature_runs_the_full_discover_validate_approve_invoke_chain()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var parcels = server.Runtime.Table("Parcels");

        var state = await server.CallOkAsync("system_get_state", new { }, Token);
        var revision = Revision(state);
        Assert.Equal("Riverton", state.GetProperty("workspace").GetProperty("project").GetProperty("name").GetString());
        Assert.False(state.GetProperty("workspace").GetProperty("project").GetProperty("isDirty").GetBoolean());

        var hits = await server.CallOkAsync("registry_search", new { query = "delete a feature" }, Token);
        Assert.Contains(hits.EnumerateArray(), hit => hit.GetProperty("operation").GetProperty("id").GetString() == "feature.delete");

        var description = await server.CallOkAsync("registry_describe", new { operationId = "feature.delete" }, Token);
        Assert.True(description.GetProperty("requiresConfirmation").GetBoolean());

        var arguments = new { layer = "Parcels", target = new { objectId = 8 } };
        var validation = await server.CallOkAsync("registry_validate", new { operationId = "feature.delete", arguments, expectedRevision = revision }, Token);
        Assert.True(validation.GetProperty("valid").GetBoolean(), validation.GetRawText());
        Assert.True(validation.GetProperty("requiresConfirmation").GetBoolean());

        // Without a token the host refuses; nothing is deleted.
        var unapproved = await server.CallAsync("registry_invoke", new { operationId = "feature.delete", arguments, expectedRevision = revision }, Token);
        Assert.True(unapproved.IsError);
        Assert.Equal("confirmation_required", unapproved.ErrorCode);
        Assert.Equal(8, parcels.Rows.Count);

        var request = await server.CallOkAsync("approval_request", new { operationId = "feature.delete", arguments, expectedRevision = revision }, Token);
        var requestId = request.GetProperty("requestId").GetString()!;
        Assert.Equal("pending", request.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("confirmationToken").ValueKind);
        Assert.Equal("pending", (await server.CallOkAsync("approval_status", new { requestId }, Token)).GetProperty("status").GetString());

        Assert.Equal(requestId, server.ApproveAsPerson("feature.delete"));
        var approved = await server.CallOkAsync("approval_status", new { requestId }, Token);
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        var token = approved.GetProperty("confirmationToken").GetString();

        var invoke = new { operationId = "feature.delete", arguments, expectedRevision = revision, confirmationToken = token };
        var deleted = await server.CallOkAsync("registry_invoke", invoke, Token);
        Assert.True(deleted.GetProperty("success").GetBoolean());
        Assert.NotEqual(revision, deleted.GetProperty("workspaceRevision").GetString());
        Assert.Equal(7, parcels.Rows.Count);
        Assert.DoesNotContain(parcels.Rows, row => row.ObjectId == 8);
        Assert.True(server.Runtime.State.IsDirty);

        // The token was single-use.
        var replay = await server.CallAsync("registry_invoke", invoke with { expectedRevision = deleted.GetProperty("workspaceRevision").GetString()! }, Token);
        Assert.Equal("confirmation_required", replay.ErrorCode);
        Assert.Equal("consumed", (await server.CallOkAsync("approval_status", new { requestId }, Token)).GetProperty("status").GetString());
        Assert.Equal(7, parcels.Rows.Count);

        Assert.Equal(
            ["system.get_state", "registry.search", "registry.describe", "registry.validate", "registry.invoke",
             "approval.request", "approval.status", "approval.status", "registry.invoke", "registry.invoke", "approval.status"],
            server.Bridge.Calls.Select(call => call.Method));
    }

    [Fact]
    public async Task Updating_an_attribute_and_saving_the_project_each_wait_for_the_person()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));

        var update = new { layer = "Parcels", target = new { objectId = 3 }, attributes = new { ZONE = "C1" } };
        var updated = await InvokeWithApprovalAsync(server, "feature.update", update, revision, waitForDecision: true);
        Assert.Equal("C1", server.Runtime.Table("Parcels").Rows.Single(row => row.ObjectId == 3).Values["ZONE"]);
        Assert.Equal(["ZONE"], updated.GetProperty("data").GetProperty("updatedAttributes").EnumerateArray().Select(value => value.GetString()));
        Assert.True(server.Runtime.State.IsDirty);

        var state = await server.CallOkAsync("system_get_state", new { }, Token);
        Assert.True(state.GetProperty("workspace").GetProperty("project").GetProperty("isDirty").GetBoolean());
        revision = Revision(state);
        Assert.Equal(updated.GetProperty("workspaceRevision").GetString(), revision);

        await InvokeWithApprovalAsync(server, "project.save", new { }, revision, waitForDecision: false);
        Assert.False(server.Runtime.State.IsDirty);
        Assert.Contains("project.save", server.Runtime.State.Calls);
    }

    [Fact]
    public async Task Geoprocessing_is_found_described_dry_run_then_run_with_approval()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));

        var search = await server.CallOkAsync("registry_invoke", new { operationId = "gp.search", arguments = new { query = "buffer", limit = 5 } }, Token);
        Assert.Contains(search.GetProperty("data").GetProperty("tools").EnumerateArray(),
            hit => hit.GetProperty("tool").GetProperty("executionName").GetString() == "fixture.BufferZones");

        var describe = await server.CallOkAsync("registry_invoke", new { operationId = "gp.describe", arguments = new { tool = "fixture.BufferZones" } }, Token);
        Assert.Equal("in_features", describe.GetProperty("data").GetProperty("signature")[0].GetString());

        var run = new { tool = "fixture.BufferZones", parameters = new[] { "Roads", "memory\\RoadBuffer", "50 Feet" } };
        var dry = await server.CallOkAsync("registry_invoke", new { operationId = "gp.run", arguments = run, dryRun = true }, Token);
        var verdict = dry.GetProperty("data");
        Assert.True(verdict.GetProperty("dryRun").GetBoolean());
        Assert.True(verdict.GetProperty("valid").GetBoolean(), verdict.GetRawText());
        Assert.True(verdict.GetProperty("requiresConfirmation").GetBoolean());
        Assert.False(verdict.GetProperty("wouldBeRefused").GetBoolean());
        Assert.Empty(server.Runtime.Pro.Geoprocessing.Calls);
        Assert.Empty(server.Runtime.Approvals.GetPending());

        var result = await InvokeWithApprovalAsync(server, "gp.run", run, revision, waitForDecision: false);
        Assert.Equal("memory\\RoadBuffer", result.GetProperty("data").GetProperty("ReturnValue").GetString());
        var call = Assert.Single(server.Runtime.Pro.Geoprocessing.Calls);
        Assert.Equal("fixture.BufferZones", call.Tool);
        Assert.Equal(["Roads", "memory\\RoadBuffer", "50 Feet"], call.Parameters);
        Assert.NotNull(server.Runtime.FindLayer("RoadBuffer"));
    }

    [Fact]
    public async Task Autonomous_mode_refuses_a_destructive_geoprocessing_tool_but_runs_ordinary_edits()
    {
        await using var server = await EndToEndServer.StartAsync(new FakeHostOptions(Autonomous: true), cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));
        var erase = new { tool = "fixture.EraseRows", parameters = new[] { "Parcels" } };

        var dry = await server.CallOkAsync("registry_invoke", new { operationId = "gp.run", arguments = erase, dryRun = true }, Token);
        Assert.True(dry.GetProperty("data").GetProperty("autonomousMode").GetBoolean());
        Assert.True(dry.GetProperty("data").GetProperty("wouldBeRefused").GetBoolean());

        var refused = await server.CallAsync("registry_invoke", new { operationId = "gp.run", arguments = erase, expectedRevision = revision }, Token);
        Assert.True(refused.IsError);
        Assert.False(refused.Result.GetProperty("success").GetBoolean());
        Assert.Equal(GeoprocessingRunPolicy.DestructiveToolRequiresReviewCode, refused.ErrorCode);
        Assert.Empty(server.Runtime.Pro.Geoprocessing.Calls);
        Assert.Equal(8, server.Runtime.Table("Parcels").Rows.Count);

        // The same host runs a risky feature edit unattended, and says so.
        var update = await server.CallOkAsync("registry_invoke", new
        {
            operationId = "feature.update",
            arguments = new { layer = "Parcels", target = new { objectId = 1 }, attributes = new { OWNER = "Riverton Land Trust" } },
            expectedRevision = revision
        }, Token);
        Assert.Contains(update.GetProperty("notices").EnumerateArray(), notice => notice.GetProperty("code").GetString() == "autonomous_control");
        Assert.Equal("Riverton Land Trust", server.Runtime.Table("Parcels").Rows.Single(row => row.ObjectId == 1).Values["OWNER"]);
    }

    [Fact]
    public async Task A_concurrent_edit_after_approval_invalidates_the_reviewed_revision()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));
        var arguments = new { layer = "Parcels", target = new { objectId = 2 } };
        var requestId = (await server.CallOkAsync("approval_request", new { operationId = "feature.delete", arguments, expectedRevision = revision }, Token))
            .GetProperty("requestId").GetString()!;
        server.ApproveAsPerson("feature.delete");
        var token = (await server.CallOkAsync("approval_status", new { requestId }, Token)).GetProperty("confirmationToken").GetString();

        // Someone edits the project in ArcGIS Pro between the review and the invoke.
        server.Runtime.Table("Parcels").Rows.Single(row => row.ObjectId == 2).Values["OWNER"] = "Edited in Pro";
        server.Runtime.Pro.Workspace.AdvanceRevision();

        var stale = await server.CallAsync("registry_invoke", new { operationId = "feature.delete", arguments, expectedRevision = revision, confirmationToken = token }, Token);
        Assert.True(stale.IsError);
        Assert.Equal("workspace_revision_mismatch", stale.ErrorCode);
        Assert.NotEqual(revision, stale.Envelope.GetProperty("error").GetProperty("revision").GetString());
        Assert.Contains(server.Runtime.Table("Parcels").Rows, row => row.ObjectId == 2);

        // A fresh review against the new revision is needed.
        var fresh = Revision(await server.CallOkAsync("system_get_state", new { }, Token));
        var stillApproved = await server.CallAsync("registry_invoke", new { operationId = "feature.delete", arguments, expectedRevision = fresh, confirmationToken = token }, Token);
        Assert.Equal("confirmation_required", stillApproved.ErrorCode);
    }

    [Fact]
    public async Task A_workflow_stops_with_workspace_changed_when_Pro_is_edited_between_steps()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));
        // The person edits the project while the workflow's query step is reading.
        server.Runtime.Pro.Features.Observer = (action, _) =>
        {
            if (action == "query") server.Runtime.Pro.Workspace.AdvanceRevision();
        };

        var run = await server.CallAsync("workflow_run", new { workflowId = "riverton.parcel-review", parameters = new { where = "ZONE = 'C1'" }, expectedRevision = revision }, Token);

        Assert.True(run.IsError);
        Assert.Equal("workspace_changed", run.ErrorCode);
        Assert.Equal("select", run.Result.GetProperty("stoppedAtStep").GetString());
        Assert.Empty(server.Runtime.Table("Parcels").Selection);
        Assert.DoesNotContain(server.Runtime.State.Calls, call => call.StartsWith("view.capture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_workflow_runs_every_step_when_nothing_changes()
    {
        await using var server = await EndToEndServer.StartAsync(cancellationToken: Token);
        var revision = Revision(await server.CallOkAsync("system_get_state", new { }, Token));

        var run = await server.CallOkAsync("workflow_run", new { workflowId = "riverton.parcel-review", parameters = new { where = "ZONE = 'C1'" }, expectedRevision = revision }, Token);

        Assert.True(run.GetProperty("success").GetBoolean());
        Assert.Equal(["inspect", "query", "select", "capture"], run.GetProperty("results").EnumerateArray().Select(step => step.GetProperty("step").GetString()));
        Assert.Equal([4L, 5L], server.Runtime.Table("Parcels").Selection.Order());
    }

    /// <summary>approval_request, the person approves, approval_status, then registry_invoke with the token.</summary>
    private static async Task<JsonElement> InvokeWithApprovalAsync(EndToEndServer server, string operationId, object arguments, string revision, bool waitForDecision)
    {
        var request = await server.CallOkAsync("approval_request", new { operationId, arguments, expectedRevision = revision }, Token);
        var requestId = request.GetProperty("requestId").GetString()!;
        JsonElement status;
        if (waitForDecision)
        {
            // approval_status holds the call until the person decides.
            var waiting = server.CallOkAsync("approval_status", new { requestId, waitSeconds = 30 }, Token);
            await Task.Delay(100, Token);
            Assert.False(waiting.IsCompleted);
            server.ApproveAsPerson(operationId);
            status = await waiting;
        }
        else
        {
            server.ApproveAsPerson(operationId);
            status = await server.CallOkAsync("approval_status", new { requestId }, Token);
        }
        Assert.Equal("approved", status.GetProperty("status").GetString());
        var result = await server.CallOkAsync("registry_invoke", new
        {
            operationId,
            arguments,
            expectedRevision = revision,
            confirmationToken = status.GetProperty("confirmationToken").GetString()
        }, Token);
        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        return result;
    }

    private static string Revision(JsonElement state) =>
        state.GetProperty("workspace").GetProperty("revision").GetString()!;
}
