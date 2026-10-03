# ArcGIS-only manual acceptance

Use the exact packaged add-in and gateway against a disposable ArcGIS Pro project and file geodatabase. Record the package hash, installed assembly path/hash, ArcGIS Pro version, process id, project copy, request/result JSON, audit records, output datasets, and before/after screenshots. Portable tests are prerequisites, not live acceptance.

## Recording evidence

`tools/run-acceptance.ps1` automates the recordable parts of this checklist. It captures the commit, ArcGIS Pro version, package hash and the hashes of the add-in DLLs that Pro actually loaded. It runs the existing harnesses and writes `manifest.json`, `summary.md` and `SHA256SUMS`. With `-Commit` it copies them to `docs/acceptance/<yyyy-MM-dd>-<sha7>/`. It covers Baseline steps 1–3 (`smoke`), the scripted feature, metadata, geoprocessing and ArcPy runs (`feature-gp-arcpy`, **autonomous mode only**) and the urban workflow stress run (`stress`). It is read-only unless you pass `-AllowProjectMutation` with a `-DisposableRoot`. Start with `./tools/run-acceptance.ps1 -PlanOnly`. The folder contract and the full procedure are in [acceptance/README.md](acceptance/README.md). The fixture data used by `stress` is described in [tests/data/README.md](../tests/data/README.md).

The `operations` section (`tools/run-live-operations.ps1`, default mode) runs every one of the 41 operations with happy and negative cases and asks the operator at the console to approve seven review cards and deny one in the dockpane; see [acceptance/README.md](acceptance/README.md) step 5. Check its matrix with `./tools/run-live-operations.ps1 -PlanOnly`.

The scripts do not cover the remaining steps that need a person: letting reviews expire or cancelling them on purpose, judging what a card shows, a real MCP client, multi-instance routing, undo/redo, reload checks and visual inspection. Run those from the lists below and record what you inspected with `-VisuallyInspected`.

## Real MCP client

Use the installed gateway from the release bundle, not a build output, against the disposable project in default mode.

1. Register the gateway exactly as [Install (users)](deployment.md#5-connect-your-mcp-client) shows. Claude Code: `claude mcp add --scope user arcgis-pro -- "C:\ArcGISProMCP\ArcGISProMCP-<version>-win-x64-development-preview\server\arcgis-pro-mcp.exe"`, then `claude mcp list` shows `arcgis-pro` connected. Claude Desktop: add the `mcpServers.arcgis-pro.command` entry to `claude_desktop_config.json`, quit Claude Desktop from the tray and start it again, and confirm the 16 ArcGIS tools appear.
2. Ask: *"Read the ArcGIS Pro project state."* The client calls `system_get_state`; the answer names the open disposable project and its maps.
3. Ask: *"List the layers in the active map."* The answer matches the Contents pane.
4. Ask: *"Set Units to 140 on the 'Baseline Site' feature in Design Sites."* The client should describe or validate `feature.update`, call `approval_request`, and wait with `approval_status`. One card appears in the dockpane. Approve once. The attribute changes in Pro (open the attribute table) and the client reports success with a new workspace revision.
5. Repeat step 4 with a different value and **Deny** the card. The client reports the denial and the value stays unchanged.
6. Record the client name and version, the gateway path, the prompts, and the audit records (`%LOCALAPPDATA%\ArcGISProMCP\audit\operations.jsonl`) for both attempts.

## Dockpane review cards

For each confirmation-gated operation the `operations` section raises one card (two for `feature.delete`). While each card is pending, check in the ArcGIS MCP pane before clicking:

| Operation | Card must show | Then |
| --- | --- | --- |
| `gp.run` (Buffer) | Tool `analysis.Buffer`, its input and output paths and `50 Feet`, no user-code or credit warning | Approve once; the output appears in the copied `MasterPlan.gdb` and nothing is added to the map |
| `feature.update` | Layer `Design Sites`, the target GlobalID, `Units: 140` | Approve once; only that feature changes |
| `metadata.update` | Layer `Design Sites` and the new title, summary, tags, credits and use limitations | Approve once; `metadata.get` reads the new values back |
| `arcpy.run-script` | Script `hello.py`, its SHA-256, the argument `ops-matrix` and the timeout | Approve once; the run directory appears under the working root |
| `feature.delete` (first card) | Layer `Design Sites` and the GlobalID of `Ops Matrix Site` | Approve once; only that feature is deleted |
| `feature.delete` (DENY card) | Layer `Design Sites` and the GlobalID of `Baseline Site` | **Deny**; the card closes, nothing is deleted |
| `project.save` | The project path, requested through the MCP gateway | Approve once; the `.aprx` timestamp changes |
| `project.open` | The `<name>-reopen-<stamp>.aprx` path | Approve once; Pro closes the current project and opens the copy |

For every card also check: the operation id, version, risk and workspace revision are readable without scrolling; the arguments preview matches the table above; the requested and expiry times are shown; any warning on the card is legible and accurate; Approve once and Deny are both reachable by keyboard; and a decided card leaves the pending list at once. Record any confusing wording as a finding.

## Baseline

1. Run `./tools/verify-release.ps1` and `./tools/package-release.ps1` from the repository root.
2. Close ArcGIS Pro, install the exact package, reopen the disposable project, and confirm the loaded assembly hash matches the package.
3. Verify MCP initialize, 16 gateway tools, live state, registry search/describe/validate, skill lookup, and a native image observation.
4. Confirm the registry contains no `rhino.*` operation and the workspace reports no Rhino capability.
5. Verify autonomous control is disabled by default. On a separate trusted-host run, enable it explicitly, confirm the workspace/panel warning, invoke a harmless risky operation without a token, and verify the `autonomous_control` notice and audit record.
6. Open two disposable ArcGIS Pro projects concurrently. Verify distinct per-PID discovery records/pipes, an actionable ambiguity error without a selector, and stable repeated routing when each gateway uses its selected `ARCGIS_PRO_MCP_HOST_PID`.

## Feature data

Create a disposable GlobalID-enabled point, polyline, and polygon feature class with editable text, integer, double, date, and GUID fields.

1. Run `feature.layer.describe` and verify geometry, spatial reference, ObjectID, GlobalID, field types, editability, and source information.
2. Run bounded attribute and envelope queries. Verify the requested limit, ObjectID/GlobalID identity, spatial relationship, field projection, invalid-field rejection, unsafe-where rejection, and non-feature-layer rejection.
3. Exercise `feature.select` in `new` and `add` modes, then clear the selection. Verify invalid modes fail without changing selection.
4. Create one feature of each supported geometry type, read it back, update attributes and geometry by GlobalID, and prove the same feature changed without duplication.
5. Verify wrong geometry types, over-length strings, read-only/system fields, nullability violations, non-editable layers, unknown identities, and stale workspace revisions fail without mutation.
6. Request local review for one exact `feature.delete`, approve once, delete the target, and prove token reuse and changed arguments fail. Repeat with denial, expiry, cancellation, and stale revision; the feature must remain.
7. Exercise ArcGIS undo/redo where the data source supports it and record actual behavior. Do not claim transactional rollback across workflows.

## Metadata

1. Run `metadata.get` on a map-layer-owned and, where available, source-owned feature layer. Record support, editability, scope, storage, values, and XML length.
2. On an editable disposable layer, request review and update title, summary, description, tags, credits, and use limitations. Read back after save and after project reload; unrelated metadata XML must remain.
3. Verify an empty patch, unsupported layer, non-editable/source-owned metadata, oversized values, token reuse, and stale revision fail without changing metadata.

## Geoprocessing

Run at least `analysis.Buffer`, `analysis.Clip` or `analysis.Intersect`, and `management.GetCount` with explicit scratch outputs and environments.

1. Describe and validate the exact `gp.run` arguments, request local review, then invoke unchanged arguments/revision.
2. Verify output paths, overwrite behavior, add-to-map/history/refresh flags, return value, derived values/types, warnings, messages, timing, and audit correlation.
3. Repeat successful tools ten times with unique idempotency keys and deterministic scratch outputs. Verify an exact same-session retry does not duplicate work.
4. Verify invalid tool, invalid parameters, license failure where safely reproducible, cancellation, disconnect, timeout, existing-output rejection, and outcome_unknown reconciliation. Never blindly retry a possibly accepted mutation.

## ArcPy

ArcPy acceptance applies only when the optional execution surface is explicitly enabled. Use a reviewed script file inside an explicitly allowed scratch root.

1. Verify disabled-by-default behavior and rejection outside the allowed root.
2. Run a harmless version/environment probe and a scratch feature-class calculation. Record the resolved ArcGIS Python executable, script hash, working directory, timeout, exit code, bounded stdout/stderr, and output data.
3. Verify confirmation, stale revision, denial, expiry, cancellation, timeout/process-tree cleanup, output truncation, missing script, invalid working directory, and nonzero exit behavior.
4. Inspect the exact script before approval. ArcPy is arbitrary code with the signed-in user's authority; this operation is not a sandbox.

## Stability and release gate

Run the three urban workflows ten times each on the exact installed build, including their geoprocessing, feature-query/edit, metadata, layout, save, capture, and reload checks. Visually inspect every final layout and verify no blank frame, duplicated feature, leaked scratch output, stale selection, or unexpected project mutation. Observe idle operation and normal shutdown after the stress run.

Release evidence must separately state what is implemented, portable-tested, live-tested, visually inspected, and still blocked. An unsigned package, a rebuilt DLL not loaded by Pro, or successful standalone tests are not production acceptance.
