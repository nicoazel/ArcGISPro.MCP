# Reference

This page lists everything a client or operator can use. The live registry is authoritative. Call `registry_browse` or `registry_describe` for exact schemas, examples, and related operations.

## Gateway tools

The stdio gateway (`ArcGISProMCP.Server`) exposes exactly these 16 MCP tools.

Every tool declares all four MCP hints. `openWorldHint` is always false: nothing reaches beyond the local ArcGIS Pro session. Read-only tools are also idempotent and non-destructive.

| Tool | Read-only | Destructive | Idempotent | `result` | Purpose |
| --- | :---: | :---: | :---: | --- | --- |
| `system_get_state` | ✓ | | ✓ | state | Revisioned snapshot of the project, maps, layouts, active view, capabilities, and connection. |
| `registry_search` | ✓ | | ✓ | hit[] | Searches operations by intent, GIS terms and aliases. Optional filters: `domain`, `capabilities` (a result must require every listed capability) and `maxRisk` (highest risk to include, for example `ReadOnly`). `limit` defaults to 12 and is clamped to 1–100. Articles and prepositions such as "the", "a", "of" and "to" are ignored in the query. |
| `registry_browse` | ✓ | | ✓ | browse | Without `domain`: `total` and per-domain `domains` counts. With `domain`: that domain's `operations`. The unused pair is null. Search and browse entries include `executesUserCode`. |
| `registry_describe` | ✓ | | ✓ | descriptor | Returns one operation's full descriptor: input schema, required capabilities, examples, risk, confirmation requirement, related operations, and `resultSchema`, the JSON schema of that operation's `registry_invoke` result (`success`, `data`, `errorCode`, `message`, `workspaceRevision`, `notices`, `resources`). `outputSchema` describes `data` of a successful result and is null while the operation declares none; `data` is then unconstrained. When an operation declares `outputSchema`, `resultSchema` requires a typed `data`, so a failed result of that operation (`success: false`, `data: null`) does not validate against it: check `success` first and validate only successful results. 15 stable-shape operations declare one (`project.get`, `map.list`, `layer.list`, `feature.*`, `table.query`, `table.statistics`, `metadata.get`, `layout.list`, `layout.inspect`, `view.capture`); the `gp.*` operations do not, because a `gp.run` dry run and a real run return different shapes and tool results mirror open-ended Esri values. |
| `registry_validate` | ✓ | | ✓ | validation | Checks arguments against the operation's input schema and, for writes, the expected revision against the current one. It does not resolve layers, maps or paths, so a valid result does not guarantee success. Never writes. |
| `registry_invoke` | | ✓ | | operation result | Runs one operation. Writes need the current revision, and confirmation-gated operations also need an approval token. An operation result with `success: false` makes the call an error that still carries the result. `dryRun: true` validates without executing (see [dry runs](#dry-runs)). |
| `approval_request` | | | | approval | Queues local review of one exact risky call in the ArcGIS Pro panel. It cannot approve itself. |
| `approval_status` | ✓ | | ✓ | approval | Returns `pending`, `approved`, `denied`, `expired`, `cancelled`, or `consumed`. Only an `approved` status carries the single-use token. `waitSeconds` (0–120, clamped) holds a pending request until a person decides or the wait elapses, then returns the status either way; keep it below your client's tool-call timeout. The host holds at most two waits at once; a further waiting call returns immediately with `waitNotice` set, so poll again. |
| `approval_cancel` | | | ✓ | `{requestId, cancelled}` | Cancels a pending or approved request and revokes its token. |
| `workflow_list` | ✓ | | ✓ | workflow[] | Lists versioned workflows with evidence-based ranking and their `parameters` (older hosts omit `parameters`). |
| `workflow_get` | ✓ | | ✓ | workflow | Returns one immutable workflow version: its parameters, steps, dependencies, and observation hints. |
| `workflow_save` | | | ✓ | `{saved, id, version}` | Validates a declarative workflow against the registry and its `allowedOperations` policy, then saves it as a new immutable version. |
| `workflow_run` | | ✓ | | run | Runs a saved workflow sequentially and returns per-step observations. Stops with `workspace_changed` if the project changes mid-run. A run with `success: false` makes the call an error that still carries every step. |
| `resource_read` | ✓ | | ✓ | resource | Reads a bounded semantic or image observation by `arcgis://` handle. The result has camelCase fields `uri`, `mimeType`, `name`, `encoding`, `data` (base64), `createdAt`. Images are returned as an MCP image block and `data` is then null. |
| `skill_search` | ✓ | | ✓ | skill[] | Finds bundled skill summaries by intent. |
| `skill_get` | ✓ | | ✓ | skill | Returns one skill manifest: preconditions, allowed operations, visual checks, and recovery guidance. |

### Tool results

Every tool returns the same envelope as `structuredContent`, and the same JSON as its text block for clients without structured-content support. Each tool's `outputSchema` describes the envelope with its typed `result`:

```json
{ "ok": true, "result": { }, "error": null }
{ "ok": false, "result": null, "error": { "code": "arcgis_unavailable", "message": "...", "retryable": true, "revision": null } }
```

- `ok: false` always comes with `isError: true` and an `error` (`code`, `message`, `retryable`, `revision`). `retryable` means the same call may succeed later, for example once ArcGIS Pro is running.
- `registry_invoke` and `workflow_run` keep `result` on failure: the operation or workflow ran (or was refused) and reported `success: false`. `error.code` repeats its `errorCode` and `error.revision` is the workspace revision it reported.
- Field names are camelCase and null members are written. `risk` and `executionTarget` are numbers: risk `0` ReadOnly, `1` SafeWrite, `2` Destructive, `3` ExternalSideEffect.
- Tool arguments that do not bind (a missing required argument, or a value of the wrong JSON type) return the envelope with `error.code` `invalid_arguments`, a message naming the argument, and `retryable: false`; nothing reaches ArcGIS Pro. An unknown tool name is a JSON-RPC `invalid params` error, not a tool result.
- Any other unexpected gateway exception is reported by the MCP SDK as a plain error result without an envelope.

## Operations

The add-in registers these 41 operations: 39 always, plus the two `arcpy.*` operations when [ArcPy is enabled](arcpy.md). Risk determines the gate each one passes through:

- **ReadOnly**: runs freely.
- **SafeWrite**: requires the current workspace revision.
- **Destructive** and **ExternalSideEffect**: require the current revision *and* a local-review token, unless the host was started in [autonomous mode](security.md). A token sent to an autonomous host is still validated, and an invalid one fails with `confirmation_required` instead of falling back to the bypass.
- **SafeWrite + approval**: `project.open`, `project.save` and `feature.update` are SafeWrite but also require a local-review token.

`gp.run` and `arcpy.run-script` execute user code (`executesUserCode: true` in search, browse and describe results). A saved workflow may use them only when its `allowedOperations` lists them explicitly (see [security](security.md)). Allowlist entries are operation ids only; they do not pin an operation version.

### Project and maps

| Id | Risk | Does |
| --- | --- | --- |
| `project.get` | ReadOnly | Project identity, path, dirty state, and revision. |
| `project.open` | SafeWrite + **approval** | Opens an existing `.aprx`, replacing the current project. |
| `project.save` | SafeWrite + **approval** | Saves the current project to disk. |
| `map.list` | ReadOnly | Every map and scene, with handles, view state, type, and layer counts. |
| `map.ensure` | SafeWrite | Returns a named map, or creates it as 2D/3D with the requested basemap. |
| `map.activate` | SafeWrite | Opens or activates a map view. |
| `map.clear-selection` | SafeWrite | Clears selected features without editing data. |
| `basemap.set` | SafeWrite | Sets a map's basemap by ArcGIS Pro basemap name. |

### Layers and cartography

| Id | Risk | Does |
| --- | --- | --- |
| `layer.list` | ReadOnly | Flattened layer tree with handles and appearance state. |
| `layer.add` | SafeWrite | Adds a dataset, layer file, or service URL. With `name`, a same-named layer whose data connection is healthy is never removed: it is reused when it reads the requested source (paths compared ignoring case, trailing separators, `/` vs `\` and an optional `.shp`, then by workspace plus dataset name, so a file geodatabase feature class matches with or without its feature dataset), reused with a `source_unverified` warning when ArcGIS reports no dataset path for it (for example some service layers), and otherwise only repaired in place (feature class swapped, symbology kept; `Repaired: true`) or, when ArcGIS cannot swap it, left unchanged with a `layer_source_mismatch` warning. Only a layer whose connection is broken is repaired in place or, as a last resort, removed and re-added at the same position (`Replaced: true`, symbology not kept), with a `layer_repaired` notice. `Source` is what the layer actually reads, `RequestedSource` echoes the request, and `DataSourceStatus` is `ok`, `broken`, `unverified` or `mismatch`. A UNC path and a mapped drive for the same share are not recognized as equal; such a layer is repaired in place. A same-named group layer is never replaced. |
| `layer.set-appearance` | SafeWrite | Visibility and transparency (0–100 %). |
| `layer.set-elevation` | SafeWrite | Scene elevation mode, offset, and vertical exaggeration. |
| `symbology.set-simple` | SafeWrite | Single-symbol renderer for a point, line, or polygon layer. |
| `symbology.set-unique-values` | SafeWrite | Explicit colors and labels for values in one polygon field. |
| `label.configure` | SafeWrite | Turns labels on or off and sets the Arcade expression, font, size, and color. |
| `style.search` | ReadOnly | Searches symbols in referenced project styles. |

### Features, tables, and metadata

| Id | Risk | Does |
| --- | --- | --- |
| `feature.layer.describe` | ReadOnly | Schema, geometry type, stable id fields, and editability. |
| `feature.query` | ReadOnly | Bounded query with a safe where clause and an optional spatial envelope. |
| `feature.select` | SafeWrite | Replaces or adds to a selection from a bounded query. |
| `feature.create` | SafeWrite | Creates one point, single-part polyline, or single-part polygon. |
| `feature.update` | SafeWrite + **approval** | Updates one feature, addressed by ObjectID or GlobalID. |
| `feature.delete` | **Destructive** | Deletes exactly one feature by stable id. There is intentionally no where-clause delete. |
| `table.query` | ReadOnly | Bounded attribute rows plus schema metadata. |
| `table.statistics` | ReadOnly | Count, null count, min, max, sum, and mean for one numeric field. |
| `metadata.get` | ReadOnly | Title, summary, description, tags, credits, and use limitations. |
| `metadata.update` | **ExternalSideEffect** | Updates those fields and preserves unrelated metadata XML. |

### Layouts and observation

| Id | Risk | Does |
| --- | --- | --- |
| `layout.list` | ReadOnly | Layouts and their map frames. |
| `layout.inspect` | ReadOnly | Page size and flattened element geometry, including frame bindings and cameras. |
| `layout.ensure` | SafeWrite | Returns a named layout, or creates it at the requested size in inches. |
| `layout.add-map-frame` | SafeWrite | Adds a map frame at page coordinates in inches. |
| `layout.set-frame-extent` | SafeWrite | Fits a frame to a layer, with padding and heading/pitch overrides. |
| `layout.set-text` | SafeWrite | Creates or updates a named point-text element. |
| `layout.ensure-surround` | SafeWrite | Legend, north arrow, or scale bar linked to a map frame. |
| `layout.activate` | SafeWrite | Opens or activates a layout view. |
| `view.capture` | ReadOnly | PNG of the active map view or a named layout, returned as a resource handle. |

### Geoprocessing and ArcPy

| Id | Risk | Does |
| --- | --- | --- |
| `gp.search` | ReadOnly | Searches installed system toolbox metadata; returns `alias.ToolName` execution names with risk tiers. Never runs a tool. |
| `gp.describe` | ReadOnly | One tool's parameters (types, required/optional/derived, defaults, coded values, ranges), positional `signature`, environments and risk tier with reasons. |
| `gp.query` | ReadOnly | Runs one allowlisted read-only system tool (`management.GetCount`, `management.GetRasterProperties`, `management.GetCellValue`) without review; no map outputs, overwrite or history. |
| `gp.run` | **ExternalSideEffect** | Runs a toolbox-qualified GP tool with bounded positional parameters, explicit environments, and overwrite behavior. Always reviewed; autonomous mode refuses Destructive and UserCode tools unless the request carries a local-review token. Supports a static dry run. |
| `arcpy.inspect-script` | ReadOnly | Size and SHA-256 of a script in the configured root, without running it. *Opt-in.* |
| `arcpy.run-script` | **ExternalSideEffect** | Runs a hash-pinned script in ArcGIS Pro's Python environment. *Opt-in.* |

Geoprocessing parameters are positional in the `signature` order that `gp.describe` returns (definition order with derived outputs removed), not the tool dialog's display order. Use `null` or `"#"` to leave an optional value unset; a JSON array becomes a `;`-separated multivalue. See [geoprocessing risk tiers](security.md#geoprocessing-risk-tiers).

### Dry runs

`registry_invoke` with `dryRun: true` validates the call without executing it. The input schema is still checked, but no revision or confirmation token is needed, and a dry run neither queues a review nor consumes a token. It cannot be combined with `idempotencyKey` (`dry_run_idempotency_conflict`), so a dry run is never cached or replayed. A dry run that ran its validation returns `success: true` and reports its verdict in `data`, for example `valid: false`, so it is not an MCP error; only failures before validation (unknown operation, schema violations, unparseable arguments) set `isError`. Dry runs are audited with kind `dry-run`.

Operations without their own static validation return a generic description (`valid`, `operation`, `risk`, `executionTarget`, `workspaceRevision`) that checks only the input schema. A dry run of `gp.run` never executes the tool. It returns `valid`, the static-validation `issues` (unknown or deprecated tool, too many values, missing required values, coded-value membership, boolean/integer/double parsing and ranges, checked per multivalue element), `riskTier`, `mutatesInput`, `executesUserCode`, `consumesCredits`, `requiresConfirmation`, `wouldBeRefused` (autonomous mode) and the `approvalWarning` the dockpane would show. Passing static validation does not guarantee that the tool's own validation accepts the request.

The `arcpy.*` operations are registered only when [ArcPy is enabled](arcpy.md).

## Environment variables

| Variable | Set on | Effect |
| --- | --- | --- |
| `ARCGIS_PRO_MCP_HOST_PID` | Gateway | Selects one discovered host by process id when several are running. It is also the way to select a development FakeHost. |
| `ARCGIS_PRO_MCP_ALLOW_FAKEHOST` | Gateway | Development only. `true` lets automatic host selection consider FakeHost records, which it otherwise ignores. |
| `ARCGIS_PRO_MCP_PIPE` | Gateway and Pro | Explicit pipe name override. It must match on both sides. |
| `ARCGIS_PRO_MCP_AUTONOMOUS_MODE` | Pro, before startup | `true` bypasses panel review for risky operations. Opt-in expert setting, not recommended; see [security](security.md). |
| `ARCGIS_PRO_MCP_REVISION_LOG` | Pro, before startup | `1`/`true` appends each workspace revision change, the host event behind each revision advance and settle timeouts to `%LOCALAPPDATA%\ArcGISProMCP\diagnostics\revisions-<pid>.log` (one file per ArcGIS Pro process). Lines contain the project path (URI) and map and layout names; writing stops at 50 MB. Diagnostics for unexpected `workspace_revision_mismatch` or `workspace_changed`; off by default. See [deployment](deployment.md#diagnostics). |
| `ARCGIS_PRO_MCP_ENABLE_ARCPY` | Pro, before startup | `true` registers the `arcpy.*` operations. |
| `ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT` | Pro | Absolute directory of approved scripts. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT` | Pro | Absolute, separate working directory tree. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE` | Pro | Overrides `python.exe`. Its environment must contain ArcPy. |
| `ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS` | Pro | Execution time ceiling (default 300). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS` | Pro | Cap on stdout/stderr, applied to each (default 65536). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES` | Pro | Maximum script size (default 1048576). |

Each host publishes a discovery record under `%LOCALAPPDATA%\ArcGISProMCP\hosts` with a `hostKind` of `arcgis-pro` (the add-in; a record without `hostKind` is treated the same) or `fakehost` (`tools/ArcGISProMCP.FakeHost`, whose project name is also prefixed `[FakeHost] `). Without `ARCGIS_PRO_MCP_PIPE` or `ARCGIS_PRO_MCP_HOST_PID`, the gateway selects automatically among `arcgis-pro` records only, so an ordinary client configuration never attaches to a FakeHost; with only FakeHost records present it fails with `arcgis_host_not_found`.

## Bundled workflows and skills

All bundled workflows are version 1.1.0 and none of them save the project; save with an explicit, approved `project.save` call. They are seeded into `%LOCALAPPDATA%\ArcGISProMCP\workflows` on first load without overwriting existing files.

| File | Title |
| --- | --- |
| [`master-cartography.workflow.json`](../workflows/master-cartography.workflow.json) | Three-map master cartography. Its guidance is in [`master-cartography.skill.json`](../skills/master-cartography.skill.json). |
| [`pittsburgh-block-mixed-use-showcase.workflow.json`](../workflows/pittsburgh-block-mixed-use-showcase.workflow.json) | Illustrative Pittsburgh block mixed-use showcase. |
| [`urban-tod-corridor.workflow.json`](../workflows/urban-tod-corridor.workflow.json) | Urban test 1: transit-oriented corridor. |
| [`urban-green-loop.workflow.json`](../workflows/urban-green-loop.workflow.json) | Urban test 2: green loop and stormwater network. |
| [`urban-mixed-use-massing.workflow.json`](../workflows/urban-mixed-use-massing.workflow.json) | Urban test 3: mixed-use massing and public realm. |

Workflows are immutable JSON DAGs of registered operation ids and cannot contain script steps. See [architecture](architecture.md#extension-rules).

## Resources and prompts

Read-only MCP resources mirror the read-only tools. Only project state is listed by `resources/list`; the rest are URI templates.

| URI | Content |
| --- | --- |
| `arcgis://project/state` | Same snapshot as `system_get_state` (`application/json`). |
| `arcgis://operations/{id}` | Operation descriptor, as `registry_describe`. |
| `arcgis://workflows/{id}` | Workflow definition, as `workflow_get`. Use `id@version` for an immutable version. |
| `arcgis://skills/{id}` | Bundled skill manifest, as `skill_get`. Served without ArcGIS Pro. |
| `arcgis://resource/{id}` | Observation handle, as `resource_read`. Images are returned as blobs, JSON and text observations as text. |

Prompts are generated at list time:

- `skill.<skillId>`, one per bundled skill, with an optional `goal` argument. Always available.
- `run.<workflowId>`, one per saved workflow, with one argument per workflow parameter. Listed only while an ArcGIS Pro host answers; `run.<workflowId>@<version>` pins a version. Required parameters without a default must be supplied.

Each prompt walks the model through `system_get_state`, `workflow_get`, `workflow_run` with explicit parameters and the current revision, then a review of the returned observations against the skill's visual checks.

## Error codes

Codes a client should handle. The message carries the details.

| Code | Where | Meaning |
| --- | --- | --- |
| `workspace_revision_required` | invoke, workflow run | A write was sent without `expectedRevision`. |
| `workspace_revision_mismatch` | invoke, workflow run | The expected revision is stale. Nothing ran. Refresh state and review. |
| `workspace_changed` | workflow run result | The project changed before a later write step. The run stopped at `stoppedAtStep`/`stepIndex`; earlier steps are not rolled back and the run is not repeated. |
| `confirmation_required` | invoke, workflow step | The operation needs an approval token bound to these arguments and revision. |
| `operation_not_allowed` | workflow validation issue | A step's operation is not permitted by the workflow's `allowedOperations`. The issue is returned as `operation_not_allowed: <message>` inside an `invalid_workflow` error on both `workflow_save` and `workflow_run`. |
| `invalid_workflow` | workflow save and run | The definition failed validation; the message lists each issue as `<code>: <message>`. |
| `operation_not_found` | describe, invoke | Unknown operation id. Invocations with unknown ids are audited. |
| `idempotency_conflict` | invoke, workflow run | The `idempotencyKey` was already used with a different operation, arguments or revision in this Pro session. |
| `dry_run_idempotency_conflict` | invoke | `dryRun` was combined with `idempotencyKey`. Nothing ran and the key was not recorded. |
| `destructive_tool_requires_review` | `gp.run` result | Autonomous mode refused a Destructive, UserCode or unclassified geoprocessing tool sent without a token. Request local review with `approval_request` and retry with the token. |
| `tool_not_found`, `tool_not_query_allowed`, `invalid_tool_name` | `gp.describe`, `gp.query` results | Unknown tool, a tool outside the `gp.query` allowlist, or a malformed `alias.ToolName`. |
| `geoprocessing_failed`, `geoprocessing_cancelled` | `gp.run`, `gp.query` results | ArcGIS Pro reported a failed or cancelled tool run; its messages are in the result. |
| `request_cancelled` | any bridge call | The caller cancelled the request before it completed. A write may already have been accepted; check state. |
| `host_stopping` | any bridge call | ArcGIS Pro is shutting down and cancelled the request, including keyed work shared by several callers. Retryable against a new host; a write may already have been accepted, so check state first. |
| `outcome_unknown` | gateway | The connection failed after the request was sent. Inspect state before repeating. |
| `arcgis_unavailable` | gateway | No ArcGIS Pro host accepted the connection. Retryable. |
| `approval_not_found` | approval status | Unknown or no longer retained approval id. Fails closed. |
| `bridge_contract_mismatch` | gateway | The add-in returned a result this gateway cannot read. Install matching add-in and gateway versions. |
| `operation_failed`, `workflow_step_failed` | invoke, workflow run | Fallback `error.code` when a failed result carries no `errorCode` (for example a workflow step failed and the run stopped). |
| `skill_not_found` | skill get | Unknown bundled skill id. |
| `layer_data_source_unavailable` | invoke, workflow step | The target layer's data source is broken or cannot be opened (for example relative paths after a project was copied). The message names the layer; repair its data source in ArcGIS Pro, or re-add it with `layer.add` using the same name and a valid source. |

## Scripts

| Script | Use |
| --- | --- |
| `tools/verify-release.ps1` | Release build, portable tests, whitespace, and package inspection. Add `-Live` for a real MCP probe. PowerShell 7. |
| `tools/package-release.ps1` | Builds the full unsigned preview bundle after running the verification. `-SkipTests` (used by CI after its test job) builds and packages without re-running the tests. PowerShell 7. |
| `tools/test-fakehost.ps1` | Starts FakeHost and the gateway and runs a short MCP session, as CI does. No ArcGIS Pro needed. |
| `tools/pack-addin.ps1` | Packs the add-in and can install it (`-Install`). PowerShell 7. |
| `tools/test-mcp.ps1` | Live handshake, tool discovery, state, registry search, and skill read. |
| `tools/run-live-feature-gp-arcpy.ps1` | Live feature, geoprocessing, and ArcPy acceptance driver. |
| `tools/run-urban-stress.ps1` | Repeats the urban layout workflows as a stress test. |
| `tools/create-*.py` | Generate the Pittsburgh showcase and urban massing fixtures. |
