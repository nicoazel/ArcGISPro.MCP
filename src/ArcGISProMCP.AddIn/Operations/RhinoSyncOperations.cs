using System.Text.Json;
using ArcGISProMCP.AddIn.Services;
using ArcGISProMCP.Core.Operations;

namespace ArcGISProMCP.AddIn.Operations;

internal abstract class RhinoPeerInvokeOperationBase(
    OperationDescriptor descriptor,
    string peerOperation) : ProOperationBase(descriptor)
{
    protected sealed override async Task<OperationResult> ExecuteCoreAsync(
        JsonElement arguments,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        var data = await RhinoPeerClient.InvokeAsync(peerOperation, arguments, cancellationToken).ConfigureAwait(false);
        // Once the uncancellable peer contract has completed, finish bookkeeping even when the
        // request token was cancelled while Rhino was working.
        var snapshot = await context.Workspace.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        return OperationResult.Ok(data, snapshot.Revision);
    }
}

internal sealed class RhinoStartOperation() : RhinoPeerInvokeOperationBase(
    OperationDescriptor.Create(
        "rhino.start",
        "Start Rhino.Inside",
        "Starts the existing Rhino.Inside host and reports the loaded Rhino version. This does not expose Rhino scripting or evaluation.",
        JsonSchemas.EmptyObject,
        risk: OperationRisk.SafeWrite,
        executionTarget: ExecutionTarget.RhinoUiThread,
        capabilities: ["rhino-sync"],
        tags: ["rhino", "rhino.inside", "start"],
        aliases: ["launch rhino", "start rhino"],
        examples: ["Start Rhino.Inside before pulling GIS context."],
        related: ["rhino.peer-state", "rhino.pull"],
        typicalDuration: "A few seconds"),
    "start");

internal sealed class RhinoMcpReconnectOperation() : RhinoPeerInvokeOperationBase(
    OperationDescriptor.Create(
        "rhino.mcp-reconnect", "Start or reconnect McNeel MCP",
        "Runs McNeel's fixed MCPStart command inside the existing Rhino.Inside host. May replace its current endpoint; use after document restoration when the router has no matching slot, not during active Rhino tool calls. Refresh McNeel slots to verify health.",
        JsonSchemas.ObjectSchema("\"port\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":65535}"),
        risk: OperationRisk.SafeWrite, executionTarget: ExecutionTarget.RhinoUiThread,
        capabilities: ["rhino-sync"], tags: ["rhino", "mcp", "reconnect", "McNeel"],
        related: ["rhino.start", "rhino.peer-state"]), "mcp-start");

internal sealed class RhinoPullOperation() : RhinoPeerInvokeOperationBase(
    OperationDescriptor.Create(
        "rhino.pull",
        "Pull ArcGIS layer into Rhino",
        "Imports an ArcGIS feature layer into an empty Rhino target layer. Nonempty targets are rejected to prevent duplicate identities. GIS-to-Rhino geometry updates are not yet supported by the existing sync engine.",
        JsonSchemas.ObjectSchema(
            "\"arcgisLayer\": {\"type\": \"string\", \"minLength\": 1}, \"rhinoLayer\": {\"type\": \"string\"}, \"selectedOnly\": {\"type\": \"boolean\"}",
            "arcgisLayer"),
        risk: OperationRisk.SafeWrite,
        executionTarget: ExecutionTarget.RhinoUiThread,
        capabilities: ["rhino-sync"],
        tags: ["rhino", "pull", "layer", "context"],
        aliases: ["send layer to rhino", "pull gis to rhino"],
        examples: ["Pull Parcels into the Rhino layer GIS::Parcels."],
        related: ["rhino.start", "rhino.preview", "rhino.sync"],
        typicalDuration: "Seconds to minutes"),
    "pull");

internal sealed class RhinoPreviewOperation() : RhinoPeerInvokeOperationBase(
    OperationDescriptor.Create(
        "rhino.preview",
        "Preview Rhino and ArcGIS sync",
        "Computes the existing Rhino/ArcGIS sync plan without applying feature edits. The sync stack may initialize missing Rhino earth-anchor metadata.",
        JsonSchemas.ObjectSchema(
            "\"arcgisLayer\": {\"type\": \"string\", \"minLength\": 1}, \"rhinoLayer\": {\"type\": \"string\"}, \"direction\": {\"type\": \"string\", \"enum\": [\"TwoWay\", \"PullOnly\", \"PushOnly\"]}",
            "arcgisLayer"),
        risk: OperationRisk.SafeWrite,
        executionTarget: ExecutionTarget.RhinoUiThread,
        capabilities: ["rhino-sync"],
        tags: ["rhino", "sync", "preview", "diff"],
        aliases: ["preview rhino sync", "compare rhino and arcgis"],
        examples: ["Preview a TwoWay sync between Parcels and GIS::Parcels."],
        related: ["rhino.pull", "rhino.sync"],
        typicalDuration: "Seconds to minutes"),
    "preview");

internal sealed class RhinoSyncOperation() : RhinoPeerInvokeOperationBase(
    OperationDescriptor.Create(
        "rhino.sync",
        "Sync Rhino and ArcGIS",
        "Applies the existing sync with manual conflict resolution. Conflicts and deletion cases are held; no forced deletes. Rhino-to-GIS geometry writes are supported; GIS-to-Rhino geometry updates are not yet implemented by the existing engine.",
        JsonSchemas.ObjectSchema(
            "\"arcgisLayer\": {\"type\": \"string\", \"minLength\": 1}, \"rhinoLayer\": {\"type\": \"string\"}, \"direction\": {\"type\": \"string\", \"enum\": [\"TwoWay\", \"PullOnly\", \"PushOnly\"]}",
            "arcgisLayer"),
        risk: OperationRisk.SafeWrite,
        executionTarget: ExecutionTarget.RhinoUiThread,
        capabilities: ["rhino-sync"],
        tags: ["rhino", "sync", "apply", "manual conflicts"],
        aliases: ["apply rhino sync", "synchronize rhino and arcgis"],
        examples: ["Sync Parcels with GIS::Parcels and hold conflicts for review."],
        related: ["rhino.preview", "rhino.pull"],
        requiresConfirmation: true,
        typicalDuration: "Seconds to minutes"),
    "sync");
