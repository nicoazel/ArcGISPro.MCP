# Manual workstation acceptance gate

This is the release gate for behavior that cannot be certified by the Core and Bridge unit tests.
It requires a real Windows workstation with ArcGIS Pro, the signed add-in under review, and the
Rhino.Inside peer that will be used in production. It is a procedure, not a claim that this gate
has been run.

## Safety boundary

Use a copy of an `.aprx` and a disposable file geodatabase. Use a dedicated temporary Rhino
document/layer. Do not point a geoprocessing output at a user dataset, and do not use a production
project, feature class, or Rhino document. Preserve the complete scratch directory and raw bridge
responses until the gate is reviewed.

The public MCP names are snake case; the local bridge names in parentheses are dotted:

| MCP tool | Bridge method | Contract used by this gate |
| --- | --- | --- |
| `system_get_state` | `system.get_state` | No arguments; returns the current workspace revision, project, maps, layouts, capabilities, and connection state. |
| `registry_describe` | `registry.describe` | `operationId`; returns the authoritative descriptor and JSON schema. |
| `registry_validate` | `registry.validate` | `operationId`, object `arguments`, optional `expectedRevision`; read-only validation against the live state. |
| `registry_invoke` | `registry.invoke` | `operationId`, object `arguments`, optional `expectedRevision`, `confirmationToken`, and `idempotencyKey`. |
| `approval_request` | `approval.request` | `operationId`, object `arguments`, required `expectedRevision`; queues local human review. |
| `approval_status` | `approval.status` | `requestId`; returns `pending`, `approved`, `denied`, `expired`, `cancelled`, or `consumed`. |
| `approval_cancel` | `approval.cancel` | `requestId`; revokes a pending or approved request. |
| `workflow_run` | `workflow.run` | `workflowId`, object `parameters`, optional `expectedRevision`, immutable `version`, and `idempotencyKey`; write workflows require a current initial revision before any step starts. |
| `registry_invoke` | operation `rhino.start` | Invoke through the registry with `{}` after `registry_describe`; starts the existing Rhino.Inside host. |
| `registry_invoke` | operation `rhino.peer-state` | Invoke through the registry with `{}` after `registry_describe`; confirms peer availability without exposing document paths or geometry. |
| `registry_validate` + `registry_invoke` | operation `rhino.pull` | Required `arcgisLayer`; optional `rhinoLayer` and `selectedOnly`. Target Rhino layer must be empty. |
| `registry_validate` + `registry_invoke` | operation `rhino.preview` | Required `arcgisLayer`; optional `rhinoLayer` and `direction` in `TwoWay`, `PullOnly`, `PushOnly`. |
| `approval_request` + `registry_invoke` | operation `rhino.sync` | Same shape as `rhino.preview`; `direction` is optional and the operation requires local confirmation. |
| `resource_read` | `resource.read` | `uri`; use only for an `arcgis://resource/...` handle returned by an operation. |

Use `registry_search`/`registry_browse` to discover the exact deployed operation ids if a client
does not expose the operation catalog. Rhino operations are not standalone MCP tools: invoke them
through `registry_validate` and `registry_invoke`, then call `registry_describe` before constructing
an invocation whenever the descriptor or schema is not already known. Do not infer an operation's
schema from an old example.

## 1. Start and record the baseline

1. Open the disposable ArcGIS Pro project and the disposable file geodatabase. Confirm that the
   feature class used for the sync case has GlobalIDs and that its source layer has a unique name.
   Record the initial feature count and a checksum or GlobalID list.
2. Open the **ArcGIS MCP** dockpane. Confirm the header shows the expected endpoint and a connected
   state. The visible controls are **Connect**, **Stop** (disconnect), and the gear button
   **Open MCP settings**.
3. Call `system_get_state`. Save the complete response as `state-01.json`; record
   `workspace.revision`, project name, active map/layout, and the `connected` value. This revision
   is the only revision usable for the next validation steps.
4. Call `registry_describe` for `rhino.peer-state`, `rhino.start`, `rhino.pull`, `rhino.preview`,
   `rhino.sync`, and `gp.run`. Save each response. Confirm `rhino.sync` has
   `requiresConfirmation: true`, and confirm the `gp.run` schema before supplying any real tool
   parameters.
5. Call `rhino.peer-state`, then `rhino.start` only if the peer state says Rhino.Inside is not
   available. Call `rhino.peer-state` again and record the successful peer/session observation.
   If the peer cannot be identified or the Rhino document is not the disposable document, stop.

## 2. Local approval queue gate

Use the real `rhino.sync` operation against the disposable layer. First complete the initial
`rhino.pull` in section 4 into the dedicated empty Rhino target and establish its clean preview
baseline. Do not use an empty `TwoWay` sync as proof of a clean baseline before the initial pull.
Replace every angle-bracketed
value below with the value returned by the baseline or the authoritative descriptor; these are
placeholders, not valid data:

```json
{
  "operationId": "rhino.sync",
  "arguments": {
    "arcgisLayer": "<actual disposable layer name>",
    "rhinoLayer": "<actual empty linked Rhino layer>",
    "direction": "TwoWay"
  },
  "expectedRevision": "<workspace.revision from system_get_state>"
}
```

First call `registry_validate` with exactly the `arguments` object and revision. It must report
valid before any approval request. Record the GIS feature count and the Rhino object count; no
mutation is allowed during validation or approval creation.

### Approve once

1. Call `approval_request` with the exact operation, arguments, and current revision.
2. In the dockpane, confirm the section reads **Approval required · 1** and **User action
   required** is visible. Expand **Review full arguments**. The item must show the operation id,
   version, risk, summary, workspace revision, arguments, and expiry. These values must match the
   request; otherwise use **Deny** and stop.
3. Click **Approve once** (automation name: “Approve this exact request once”). Poll
   `approval_status` until it is `approved`; only this response supplies a token.
4. Call `registry_invoke` with the unchanged arguments and revision, the returned
   `confirmationToken`, and a new descriptive `idempotencyKey`. Do not edit, reorder, or
   canonicalize the reviewed JSON between the two calls.
5. Verify the operation result, audit entry, GIS/Rhino counts, and a follow-up `rhino.preview`.
   The preview must report no changes after the operation has been re-baselined. Save the raw
   responses and a screenshot of the approval item before the click.

### Denial

Repeat the request with a fresh approval request. Click **Deny**. `approval_status` must become
`denied`. Invoke the same operation without a token using a fresh idempotency key. It must fail
closed with `confirmation_required`, and GIS/Rhino counts and the preview must remain unchanged.

### Expiry

Repeat with a fresh request and leave it pending. The add-in's local approval lifetime is two
minutes. Poll `approval_status` until it becomes `expired`; do not click an approval button after
expiry. An invocation without a newly approved token must return `confirmation_required`, with no
mutation. If expiry is not observable after a reasonable polling interval, stop and record the
timestamps rather than bypassing the queue.

### Cancellation

Repeat with a fresh request. Call `approval_cancel` using its `requestId`, then poll
`approval_status`. It must become `cancelled`. Attempting the operation without a new token must
fail with `confirmation_required`; the cancelled token must not work. Confirm no GIS/Rhino change.

### Disconnect and stale revision

For a pending request, click the dockpane **Stop** button (automation name: “Disconnect from the
MCP server”). The add-in revokes pending and approved local approvals. `approval_status` must no
longer be usable as an approval path, and new non-state calls must fail with `bridge_disabled`.
Call `system_get_state` while disconnected to confirm the state-control exception remains usable.
Click **Connect**, refresh state, and obtain a new revision before continuing. A previously issued
token must never be reused after this sequence.

For the revision check, record revision `r1`, make a harmless edit in the disposable project, and
call `system_get_state` again. Submit `registry_validate` and `approval_request` using the old
`r1`. Validation must report a revision mismatch for the write, and approval creation must fail
with `workspace_revision_mismatch`. Refresh state and use the new revision only after reviewing
the changed workspace.

Also exercise the workflow preflight with a saved workflow that contains at least one write step
(the bundled master workflow is suitable after its operation prerequisites are available). Call
`workflow_run` once without `expectedRevision`, then with the stale `r1`; both calls must be
rejected before the first step, including when the first step is read-only. Call it again with the
   current revision. If an `idempotencyKey` is supplied, include the exact immutable `version`; retry
   only the same version, parameters, initial revision, and key after an unknown transport outcome.
   This idempotency replay guarantee is limited to the same live host session while its process
   cache is present. After ArcGIS Pro or the bridge is restarted, assume the cache is gone: inspect
   the project, outputs, audit evidence, and peer state, then choose and record a deliberate recovery
   action. Never infer that replaying a cached key is safe across a restart.

## 3. In-flight disconnect and shutdown

Use only a trusted, known-duration operation with disposable outputs. The curated long-running
candidate is `gp.run`, but its schema deliberately does not prescribe toolbox parameters: call
`registry_describe`, select a toolbox-qualified tool and disposable output that are valid on the
workstation, then call `registry_validate` before requesting approval. Never paste a guessed tool
name or parameter array into this gate.

1. Capture the current revision, validate the exact `gp.run` arguments, request local approval,
   approve once in the dockpane, and invoke with an idempotency key.
2. While the operation is visibly running, click **Stop**. Record whether the operation finishes,
   its final audit/result, and the bridge state. A disconnect blocks queued/new mutations; it does
   not pretend to cancel an already accepted SDK or peer write. Do not start a replacement job.
3. If the client loses the response after transmission, classify it as `outcome_unknown`. Inspect
   the disposable output, workspace state, audit log, and peer state. During the same live host
   session, retry only the exact original operation/workflow version, arguments, initial revision,
   and idempotency key; never retry with a fresh key merely because the response was lost. If the
   host or bridge has restarted, the process-lifetime cache is gone: inspect first and perform a
   deliberate, operator-approved recovery; do not replay from the old key as though it were cached.
4. Reconnect and prove that a new request obtains a new state revision. Keep the scratch output for
   diagnosis until the acceptance record is signed.
5. Repeat the scenario while closing ArcGIS Pro normally. Allow the application to drain; do not
   force-kill it to make a test pass. On restart, reject any recovery dialog that would restore
   user data into the disposable run unless the test owner explicitly chooses it. Confirm there
   is no detached second mutation, no silent retry, and that an interrupted request is reported as
   unknown or cancelled according to the transport evidence.

## 4. Disposable file-geodatabase GIS-to-Rhino round trip

The full companion synchronization path must use a file geodatabase, not a shapefile, when testing
GlobalID identity. The repository's `tools/e2e.ps1` is an independent legacy test-bridge harness;
it creates its own scratch copy and includes a GlobalID-first scenario. It can be used as a second
evidence source:

```powershell
.\tools\e2e.ps1 -SkipStart -Project "<disposable.aprx>" `
    -Fixtures Point_Multi_Mixed -Scratch "$env:TEMP\arcgis-mcp-gdb-acceptance"
```

Do not present that script's legacy commands as MCP tool calls. The MCP acceptance itself uses the
curated operations below.

1. In the disposable `.gdb`, create or copy a feature class with GlobalIDs and at least two
   features. Include a multipart feature if the source fixture supports it (otherwise make a
   disposable multipart feature). Add it to the active ArcGIS map. Record each feature's GlobalID,
   ObjectID, geometry part count, and non-identity attributes.
2. In the empty disposable Rhino document, call `rhino.pull` with the exact layer name and a
   dedicated empty `rhinoLayer`. Verify that the result succeeds and that one tracked Rhino object
   exists for each GIS feature. Record an `expectedRhinoGUID` for every GlobalID and snapshot its
   geometry and attributes. Verify the Rhino-side identities include both GlobalID and ObjectID
   metadata where the peer reports them.
3. Call `rhino.preview` with the same layer names and `direction: "TwoWay"`. Immediately after the
   successful initial pull, this is the baseline: the peer preview must be clean (zero changed
   rows/changes in the returned summary). Preserve the complete result even if the peer uses a
   different field name than `changed`. Do not run an empty TwoWay sync before this pull as a
   substitute for the baseline.
4. GIS-to-Rhino geometry gate: in ArcGIS Pro, edit the geometry of an existing feature while
   preserving its same GlobalID. On the multipart representative, change it to a single-part
   geometry while retaining the identity. Call `rhino.preview` with `direction: "PullOnly"`; it must
   identify the expected existing feature as one GIS-originated modification. Apply through the
   reviewed `rhino.sync` PullOnly flow, then verify the unchanged `expectedRhinoGUID`, unchanged
   non-identity attributes, updated geometry and one-part result, and no extra Rhino object. Call
   `rhino.preview` again and require a clean result. If the current peer explicitly reports that
   GIS-to-Rhino geometry updates are unsupported, mark this release gate blocked/failing; do not
   treat that response as a pass or force a replacement object.
5. Rhino-to-GIS geometry gate: change the geometry of the same tracked Rhino object, then call
   `rhino.preview` with `direction: "TwoWay"`. It must identify exactly one Rhino-originated
   modification. Request local approval for `rhino.sync` with the current revision and the exact
   same TwoWay arguments; approve once in the panel and invoke with the token and an idempotency
   key. Verify that the same GIS GlobalID/feature was updated rather than a new feature being
   created, then require a clean preview.
6. Lock-failure gate: lock the tracked Rhino object (or its containing layer) and cause another
   geometry change in ArcGIS for that same GlobalID. Preview, then attempt the approved PullOnly or
   TwoWay apply. The expected result is a clear failure/held row. Verify the baseline
   `expectedRhinoGUID`, geometry, and attributes remain unchanged; object and part counts must not
   increase, and no staged or leaked replacement object may remain. Unlock, inspect the held result,
   recover only through the reviewed flow, and require a clean preview after recovery.
7. Change the staged ObjectID metadata for one tracked feature to a deliberately stale value
   without changing its GlobalID, then preview and apply through the same reviewed flow. The
   evidence must show that GlobalID matching still targets the original feature and that no
   duplicate feature is created.
8. Edit one non-identity attribute on each side in separate passes. Verify the preview labels the
   source correctly, use the declared conflict policy only when the preview is understood, and
   finish with a clean preview. For any unexpected deletion or identity conflict, stop and retain
   the scratch document rather than forcing resolution.
9. Remove temporary links/features only inside the disposable project after all evidence is saved.
   Reopen the saved disposable project and repeat `rhino.peer-state` plus a clean `rhino.preview` to
   prove that the durable link metadata survives the document lifecycle.

## Evidence and release decision

Store, at minimum, the initial and final `system_get_state` responses, every descriptor and
validation response used, approval request/status/cancel responses, panel screenshots showing
the exact arguments and button state, operation results and audit correlation ids, Rhino peer
state, preview results before/after each apply, GIS GlobalID/ObjectID/count extracts, and the
scratch paths. A release candidate passes only when every required case has a reproducible record
and no safe-stop condition occurred.

Stop immediately and mark the gate blocked if any of the following occurs: an approval item does
not match the request; a mutation occurs before approval; a stale revision is accepted; a denied,
expired, cancelled, or disconnected token is accepted; a queued mutation starts after disconnect;
an in-flight write is silently duplicated; a preview reports an unexplained conflict/deletion;
GlobalID matching creates a duplicate; the bridge or panel cannot distinguish an unknown outcome;
or the test would require touching a non-disposable file. Passing unit tests, a legacy harness,
or a standalone Rhino run is not evidence that the live embedded ArcGIS Pro path passed this gate.
