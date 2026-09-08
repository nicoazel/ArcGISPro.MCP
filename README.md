# ArcGIS Pro MCP Studio

A C# ArcGIS Pro 3.7 add-in with a small MCP gateway, searchable operation registry, thin WPF/MVVM panel, visual observations, and versioned workflows.

The model searches the registry, reads the few relevant schemas, and invokes operations by stable id. The gateway exposes 13 entry points rather than the entire operation catalog.

## Implemented

- Project inspection, open/save; map/scene creation and activation; basemap selection.
- Layer loading, visibility/transparency, simple and categorical polygon symbology, label expressions and fonts, style search, and selection clearing.
- Bounded feature-table queries and numeric statistics.
- SDK geoprocessing execution with ordered parameters and environments.
- Layout creation, map frames and extents, named text, map surrounds, activation and PNG export; map-view PNG capture.
- Immutable parameterized workflows, run history/ranking, and searchable bundled skill guidance.
- Same-user named-pipe transport, correlation, message limits, audit records and write revisions.
- Optional Rhino.Inside discovery, spatial handoff, startup, McNeel endpoint reconnect, create-only pull, preview and locally approved sync. Modeling uses McNeel's shipped Rhino MCP in the same Pro process. GIS-to-Rhino geometry updates are not yet supported by the existing sync engine.

This is a working development preview, not full coverage of the ArcGIS SDK. See the operation registry for the actual installed capability set. Advanced cartography, workspace connection management, PDF export, durable background jobs, complete cancellation, unattended approval grants and arbitrary ArcPy execution are not release features.

## Build and install

Requires Windows, a licensed ArcGIS Pro 3.7.1 installation, and .NET SDK 10. Rhino integration additionally requires Rhino 8 and the existing Rhino.Inside add-in.

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

## Live verification

```powershell
./tools/test-mcp.ps1
```

This performs a real MCP handshake, tool discovery, live state request, registry search and skill read. Pass an observation URI using `-ImageUri` to verify native MCP image content.

`tools/ArcGISProMCP.DemoRunner` runs the three-map workflow against supplied local fixtures and saves structured results and a PNG. It also supports `--call request.json result.json` for focused bridge diagnostics.

The local demonstration artifacts are in `artifacts/demo` (git-ignored). Its land-use parcels and street centerlines are test fixtures, not authoritative zoning designations or transit-service data. The proposed development is illustrative.

The detailed [live demo report](docs/master-plan-demo.md) records the successful 42-step presentation, 33 registry operations, 25 automated tests, and remaining production limitations.

## Design and limits

See [architecture](docs/architecture.md), [security](docs/security.md), and [demo](docs/master-plan-demo.md).

ArcPy is not a core dependency. The geoprocessing operation invokes the ArcGIS SDK. Optional LLM-authored ArcPy execution remains future work.

Apache-2.0. Esri and McNeel products require their own licenses.
