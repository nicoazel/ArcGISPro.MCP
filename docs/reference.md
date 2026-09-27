# Reference

This page lists everything a client or operator can use. The live registry is authoritative. Call `registry_browse` or `registry_describe` for exact schemas, examples, and related operations.

## Gateway tools

The stdio gateway (`ArcGISProMCP.Server`) exposes exactly these 16 MCP tools.

| Tool | Read-only | Purpose |
| --- | :---: | --- |
| `system_get_state` | ✓ | Revisioned snapshot of the project, maps, layouts, active view, capabilities, and connection. |
| `registry_search` | ✓ | Searches operations by intent, GIS terms and aliases. Optional filters: `domain`, `capabilities` (a result must require every listed capability) and `maxRisk` (highest risk to include, for example `ReadOnly`). `limit` defaults to 12 and is clamped to 1–100. Articles and prepositions such as "the", "a", "of" and "to" are ignored in the query. |
| `registry_browse` | ✓ | Lists concise registry entries by domain. Search and browse entries include `executesUserCode`. |
| `registry_describe` | ✓ | Returns one operation's full descriptor: input schema, required capabilities, examples, risk, confirmation requirement, and related operations. Operations do not declare result schemas yet. |
| `registry_validate` | ✓ | Checks arguments against the operation's input schema and, for writes, the expected revision against the current one. It does not resolve layers, maps or paths, so a valid result does not guarantee success. Never writes. |
| `registry_invoke` | | Runs one operation. Writes need the current revision, and confirmation-gated operations also need an approval token. |
| `approval_request` | | Queues local review of one exact risky call in the ArcGIS Pro panel. It cannot approve itself. |
| `approval_status` | ✓ | Returns `pending`, `approved`, `denied`, `expired`, `cancelled`, or `consumed`. Only an `approved` status carries the single-use token. |
| `approval_cancel` | | Cancels a pending or approved request and revokes its token. |
| `workflow_list` | ✓ | Lists versioned workflows with evidence-based ranking. |
| `workflow_get` | ✓ | Returns one immutable workflow version: its parameters, steps, dependencies, and observation hints. |
| `workflow_save` | | Validates a declarative workflow against the registry and its `allowedOperations` policy, then saves it as a new immutable version. |
| `workflow_run` | | Runs a saved workflow sequentially and returns per-step observations. Stops with `workspace_changed` if the project changes mid-run. |
| `resource_read` | ✓ | Reads a bounded semantic or image observation by `arcgis://` handle. Non-image payloads are returned as JSON with camelCase fields: `uri`, `mimeType`, `name`, `encoding`, `data`, `createdAt`. |
| `skill_search` | ✓ | Finds bundled skill summaries by intent. |
| `skill_get` | ✓ | Returns one skill manifest: preconditions, allowed operations, visual checks, and recovery guidance. |

## Operations

The add-in registers these operations. Risk determines the gate each one passes through:

- **ReadOnly**: runs freely.
- **SafeWrite**: requires the current workspace revision.
- **Destructive** and **ExternalSideEffect**: require the current revision *and* a local-review token, unless the host was started in [autonomous mode](security.md).
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
| `layer.add` | SafeWrite | Adds a dataset, layer file, or service URL. |
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
| `gp.run` | **ExternalSideEffect** | Runs a toolbox-qualified GP tool with bounded parameters, explicit environments, and overwrite behavior. |
| `arcpy.inspect-script` | ReadOnly | Size and SHA-256 of a script in the configured root, without running it. *Opt-in.* |
| `arcpy.run-script` | **ExternalSideEffect** | Runs a hash-pinned script in ArcGIS Pro's Python environment. *Opt-in.* |

The `arcpy.*` operations are registered only when [ArcPy is enabled](arcpy.md).

## Environment variables

| Variable | Set on | Effect |
| --- | --- | --- |
| `ARCGIS_PRO_MCP_HOST_PID` | Gateway | Selects one ArcGIS Pro process when several are running. |
| `ARCGIS_PRO_MCP_PIPE` | Gateway and Pro | Explicit pipe name override. It must match on both sides. |
| `ARCGIS_PRO_MCP_AUTONOMOUS_MODE` | Pro, before startup | `true` bypasses panel review for risky operations. Opt-in expert setting, not recommended; see [security](security.md). |
| `ARCGIS_PRO_MCP_ENABLE_ARCPY` | Pro, before startup | `true` registers the `arcpy.*` operations. |
| `ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT` | Pro | Absolute directory of approved scripts. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT` | Pro | Absolute, separate working directory tree. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE` | Pro | Overrides `python.exe`. Its environment must contain ArcPy. |
| `ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS` | Pro | Execution time ceiling (default 300). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS` | Pro | Cap on stdout/stderr, applied to each (default 65536). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES` | Pro | Maximum script size (default 1048576). |

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
| `request_cancelled` | any bridge call | The caller cancelled the request before it completed. A write may already have been accepted; check state. |
| `host_stopping` | any bridge call | ArcGIS Pro is shutting down and cancelled the request, including keyed work shared by several callers. Retryable against a new host; a write may already have been accepted, so check state first. |
| `outcome_unknown` | gateway | The connection failed after the request was sent. Inspect state before repeating. |

## Scripts

| Script | Use |
| --- | --- |
| `tools/verify-release.ps1` | Release build, portable tests, whitespace, and package inspection. Add `-Live` for a real MCP probe. |
| `tools/package-release.ps1` | Builds the full unsigned preview bundle after running the verification. |
| `tools/pack-addin.ps1` | Packs the add-in and can install it (`-Install`). |
| `tools/test-mcp.ps1` | Live handshake, tool discovery, state, registry search, and skill read. |
| `tools/run-live-feature-gp-arcpy.ps1` | Live feature, geoprocessing, and ArcPy acceptance driver. |
| `tools/run-urban-stress.ps1` | Repeats the urban layout workflows as a stress test. |
| `tools/create-*.py` | Generate the Pittsburgh showcase and urban massing fixtures. |
