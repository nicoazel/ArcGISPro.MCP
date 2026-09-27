# ArcGIS Pro MCP Studio

A C# ArcGIS Pro 3.7 add-in with a small MCP gateway, searchable operation registry, thin WPF/MVVM panel, visual observations, and versioned workflows.

The model searches the registry, reads the few relevant schemas, and invokes operations by stable id. The gateway exposes 16 entry points rather than the entire operation catalog, including a three-tool local-review lifecycle.

## Documentation

The **[documentation hub](docs/README.md)** is the starting point, with a reading path for each task.

| | |
| --- | --- |
| **Set up** | [Deployment, status and rollback](docs/deployment.md) · [Architecture](docs/architecture.md) |
| **Look up** | [Reference](docs/reference.md): 16 gateway tools, 38 operations with risk levels, environment variables, workflows, scripts |
| **Stay safe** | [Security and limits](docs/security.md) · [ArcPy configuration](docs/arcpy.md) |
| **Release** | [Status and known limits](docs/deployment.md#status) · [Manual acceptance](docs/manual-acceptance.md) · [Changelog](CHANGELOG.md) · [Roadmap](docs/ROADMAP.md) |
| **Contribute** | [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md) |

## Implemented

- Project inspection, open/save; map/scene creation and activation; basemap selection.
- Layer loading, visibility/transparency, simple and categorical polygon symbology, label expressions and fonts, style search, and selection clearing.
- Scene-layer elevation placement with managed mode, offset and unit metadata, including ground-relative design geometry.
- Bounded feature-table queries and numeric statistics.
- Typed feature-layer inspection, bounded attribute/spatial query and selection, and SDK-native create/update/single-feature delete operations.
- Feature-layer metadata read/update for title, summary, description, tags, credits, and use limitations.
- SDK geoprocessing execution with bounded ordered parameters, explicit environments and overwrite behavior, complete messages, warnings, derived values and timing.
- Layout creation, map frames and extents, named text, map surrounds, activation and PNG export; map-view PNG capture.
- Immutable parameterized workflows, run history/ranking, and searchable bundled skill guidance.
- Same-user concurrent named-pipe transport, bounded connection slots/framing timeouts, exclusive pipe ownership, serialized host operations, audit records and write revisions.
- Expiring modeless local review, exact argument/version/revision binding, and single-use approval tokens for every destructive or external-side-effect operation plus `project.open`, `project.save` and `feature.update`. Requests that run Python are flagged "Runs user code" on the approval card. Default mode does not allow remote self-approval; an explicit host-startup autonomous mode can bypass review while retaining revision checks, warning notices and audit records.
- Workflow operation allowlists (`gp.run` and `arcpy.run-script` must be listed explicitly), and workflows that stop with `workspace_changed` instead of adopting a newer revision mid-run.

**Status: development preview.** Supported: interactive same-user workstation with dockpane approvals. Autonomous mode is an opt-in expert setting, not recommended. The operation set is curated, not full coverage of the ArcGIS SDK. See the operation registry for the actual installed capability set. Advanced cartography, workspace connection management, PDF export, durable background jobs and complete cancellation are not release features.

## Build and install

Requires Windows, a licensed ArcGIS Pro 3.7.1 installation, and .NET SDK 10. This repository has no Rhino dependency.

```powershell
dotnet build ArcGISPro.MCP.slnx -c Debug
dotnet test tests/ArcGISProMCP.Core.Tests
dotnet test tests/ArcGISProMCP.Bridge.Tests
./tools/pack-addin.ps1 -Configuration Debug -Install
```

Restart ArcGIS Pro after installing an updated add-in. Launch the gateway with:

```powershell
dotnet run --project src/ArcGISProMCP.Server --no-build
```

The MCP transport is stdio; diagnostic logging goes to stderr. The gateway uses the official ModelContextProtocol C# SDK 2.2.0, and the add-in compiles against Esri.ArcGISPro.Extensions30 3.7.0.1901.

Each ArcGIS Pro process publishes a per-process pipe and lightweight local discovery record. With one Pro host, the gateway selects it automatically. With several, start the gateway with `ARCGIS_PRO_MCP_HOST_PID=<pid>`; `ARCGIS_PRO_MCP_PIPE` remains the explicit override. Ambiguous selection fails closed and reports the available PID/project choices.

## Live verification

To build the complete unsigned preview bundle (add-in, Windows x64 gateway, skills, workflows, documentation and checksums), run `./tools/package-release.ps1`. It smoke-tests the published gateway's MCP handshake and skill lookup without Pro. See [deployment](docs/deployment.md). The bundle is unsigned. A live acceptance pass was run on the maintainer's workstation, but its evidence is local and not committed; run [manual acceptance](docs/manual-acceptance.md) on your own installation and read the [known limits](docs/deployment.md#known-limits) first.

For a fail-fast Release build, both portable suites, whitespace checks and package-content inspection, run `./tools/verify-release.ps1`. Add `-Live` for actual MCP discovery/state and a pending/cancelled local-review probe (it never approves or runs a geoprocessing tool). Pass `-ImageUri` to verify native image content. This does not replace feature, metadata, geoprocessing, ArcPy, or local-review acceptance in ArcGIS Pro.

```powershell
./tools/test-mcp.ps1
```

This performs a real MCP handshake, tool discovery, live state request, registry search and skill read. Pass an observation URI using `-ImageUri` to verify native MCP image content.

`tools/ArcGISProMCP.DemoRunner` runs the three-map workflow against supplied local fixtures and saves structured results and a PNG. It also supports `--call request.json result.json` for focused bridge diagnostics.

The local demonstration artifacts are in `artifacts/demo` (git-ignored). Its land-use parcels and street centerlines are test fixtures, not authoritative zoning designations or transit-service data. The proposed development is illustrative.

The final standalone showcase is `workflows/pittsburgh-block-mixed-use-showcase.workflow.json`. It composes a plausible Pittsburgh mixed-use block concept into 3D massing, program and public-realm maps with live layout surrounds and dynamic text. Generated evidence is under `artifacts/pittsburgh-showcase`.

## Design and limits

See [architecture](docs/architecture.md), [security](docs/security.md), [ArcPy configuration](docs/arcpy.md), and the [reference](docs/reference.md).

ArcPy is an optional, explicitly enabled external-worker escape hatch; it is not a core dependency. Normal geoprocessing continues through the ArcGIS Pro SDK.

Autonomous mode (`ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true` for the ArcGIS Pro process before startup) is an opt-in expert setting and is not recommended. It lets the connected same-user client run risky operations, including arbitrary Python through `gp.run` or ArcPy, without panel review. It is off by default, advertised in workspace capabilities and the panel, and does not bypass workspace revisions, schema validation, audit, idempotency, or operation-specific limits.

In default mode, `workflow_run` cannot execute confirmation-gated steps such as `gp.run`, `metadata.update` or `project.save`; there is no per-step approval yet. Run those operations individually through local review.

Apache-2.0. Esri products require their own licenses.
