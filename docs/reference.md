# Reference

This page lists everything a client or operator can use. The live registry is authoritative. Call `registry_browse` or `registry_describe` for exact schemas, examples, and related operations.

## Gateway tools

The stdio gateway (`ArcGISProMCP.Server`) exposes exactly these 16 MCP tools.

| Tool | Read-only | Purpose |
| --- | :---: | --- |
| `system_get_state` | ✓ | Revisioned snapshot of the project, maps, layouts, active view, capabilities, and connection. |
| `registry_search` | ✓ | Searches operations by intent, GIS terms, aliases, domain, capability, and risk. |
| `registry_browse` | ✓ | Lists concise registry entries by domain. |
| `registry_describe` | ✓ | Returns one operation's full descriptor, including its input/output schemas, requirements, examples, risk, and related operations. |
| `registry_validate` | ✓ | Checks an operation and its arguments against the registry and live state without writing. |
| `registry_invoke` | | Runs one operation. Writes need the current revision, and risky operations also need an approval token. |
| `approval_request` | | Queues local review of one exact risky call in the ArcGIS Pro panel. It cannot approve itself. |
| `approval_status` | ✓ | Returns `pending`, `approved`, `denied`, `expired`, `cancelled`, or `consumed`. Only an `approved` status carries the single-use token. |
| `approval_cancel` | | Cancels a pending or approved request and revokes its token. |
| `workflow_list` | ✓ | Lists versioned workflows with evidence-based ranking. |
| `workflow_get` | ✓ | Returns one immutable workflow version: its parameters, steps, dependencies, and observation hints. |
| `workflow_save` | | Validates and saves a declarative, operation-allowlisted workflow as a new version. |
| `workflow_run` | | Runs a saved workflow sequentially and returns per-step observations. |
| `resource_read` | ✓ | Reads a bounded semantic or image observation by `arcgis://` handle. |
| `skill_search` | ✓ | Finds bundled skill summaries by intent. |
| `skill_get` | ✓ | Returns one skill manifest: preconditions, allowed operations, visual checks, and recovery guidance. |

## Operations

The add-in registers these operations. Risk determines the gate each one passes through:

- **ReadOnly**: runs freely.
- **SafeWrite**: requires the current workspace revision.
- **Destructive** and **ExternalSideEffect**: require the current revision *and* a local-review token, unless the host was started in [autonomous mode](security.md).

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
| `ARCGIS_PRO_MCP_AUTONOMOUS_MODE` | Pro, before startup | `true` bypasses panel review for risky operations. Only use this on trusted, unattended workstations. |
| `ARCGIS_PRO_MCP_ENABLE_ARCPY` | Pro, before startup | `true` registers the `arcpy.*` operations. |
| `ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT` | Pro | Absolute directory of approved scripts. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT` | Pro | Absolute, separate working directory tree. Required when ArcPy is enabled. |
| `ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE` | Pro | Overrides `python.exe`. Its environment must contain ArcPy. |
| `ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS` | Pro | Execution time ceiling (default 300). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS` | Pro | Cap on stdout/stderr, applied to each (default 65536). |
| `ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES` | Pro | Maximum script size (default 1048576). |

## Bundled workflows and skills

| File | Title |
| --- | --- |
| [`master-cartography.workflow.json`](../workflows/master-cartography.workflow.json) | Three-map master cartography. Its guidance is in [`master-cartography.skill.json`](../skills/master-cartography.skill.json). |
| [`pittsburgh-block-mixed-use-showcase.workflow.json`](../workflows/pittsburgh-block-mixed-use-showcase.workflow.json) | Illustrative Pittsburgh block mixed-use showcase. |
| [`urban-tod-corridor.workflow.json`](../workflows/urban-tod-corridor.workflow.json) | Urban test 1: transit-oriented corridor. |
| [`urban-green-loop.workflow.json`](../workflows/urban-green-loop.workflow.json) | Urban test 2: green loop and stormwater network. |
| [`urban-mixed-use-massing.workflow.json`](../workflows/urban-mixed-use-massing.workflow.json) | Urban test 3: mixed-use massing and public realm. |

Workflows are immutable JSON DAGs of registered operation ids and cannot contain script steps. See [architecture](architecture.md#extension-rules).

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
