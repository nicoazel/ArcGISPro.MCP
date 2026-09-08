# Live master-plan demonstration

Historical 0.1.0 milestone. Subsequent 0.2.0 hardening and its live acceptance status are tracked in [production readiness](production-readiness.md); limitations below describe the original run.

Validated locally on 2026-09-08 with ArcGIS Pro 3.7.1.1904, embedded Rhino 8.34.26223.11001, and McNeel Rhino-MCP-Platform 0.1.5. This is a development-preview milestone, not a production release certification.

## Result

The 42-step `workflow.east-liberty-presentation@1.0.1` completed live. It composes a 17-by-11-inch sheet with three map frames: a large proposed-building/site plan, land-use context, and a street-network context map. The workflow applies categorical colors, named text, frame extents, feature-selection cleanup, a north arrow and a scale bar, then captures a 2400-by-1553 PNG. The earlier 26-step bootstrap workflow also completed live.

The sources are local test fixtures around East Liberty, Pittsburgh. Land-use parcels are NOT authoritative zoning designations; street centerlines are NOT a transit-service dataset. The nine courtyard buildings and 12-24 m massing are illustrative redevelopment geometry, not a feasibility or regulatory assessment. Existing buildings and roads have not been cleared or redesigned to accommodate the proposal.

## Local artifacts (git-ignored)

- `artifacts/demo/east-liberty-master-plan.png`: inspected final three-map sheet.
- `artifacts/demo/MasterPlanDemo.aprx`: disposable demo project; the original source project was not overwritten.
- `artifacts/demo/MasterPlan.gdb/ProposedBuildings`: nine synchronized proposal footprints.
- `artifacts/demo/MasterPlanDesign-Final.3dm`: saved reconciled Rhino model, including context and massing.
- `artifacts/demo/MasterPlanDesign.3dm`: retained earlier model before reconciliation.
- `artifacts/demo/presentation.run.result.json`: full final workflow results.
- `artifacts/demo/rhino-bridge/out/`: earlier development-harness create/update evidence.

## Verified behavior

- New solution release build: zero warnings/errors. Core tests: 23 passed. Bridge tests: 2 passed.
- Existing Rhino.Inside add-in build: zero errors; seven existing package/framework/compiler warnings remain.
- Real MCP stdio handshake: protocol 2025-11-25, 13 gateway entry tools, live state, registry search, skill read, and a native MCP image content block.
- Live add-in registry: 33 operations, with schemas discovered on demand.
- Existing sync coordinator pulled 545 context buildings into Rhino.
- McNeel MCP authored nine proposal curves and nine massing objects in the embedded instance. The existing sync coordinator created nine GIS features, then updated all nine after the courtyard refinement.
- New peer adapter startup, McNeel endpoint reconnect, pull, preview, re-pull rejection and missing-approval rejection were exercised through the registry without the file-based TestBridge enabled.
- The reconciled proposal preview reported nine clean features and zero changes, including after a saved-document reload.
- The thin MCP dockpane was activated through the live ArcGIS SDK after fixing a read-only binding error. Full native UI screenshot automation was unavailable in this environment, so this is not a complete visual/interactivity acceptance test of the panel.

The approved-sync dialog itself was not clicked during unattended validation. The nine creates and nine updates exercise the underlying sync coordinator through the earlier harness; do not present that as a successful approved `rhino.sync` MCP call.

## Reproduction

Run the bootstrap workflow with `tools/ArcGISProMCP.DemoRunner`, supplying the five absolute input paths documented by its usage. Save and run `workflows/east-liberty-presentation.workflow.json` through the workflow gateway, passing `proposalSource` as the native absolute path to the synchronized feature class. Its prerequisite maps/layers come from the bootstrap and Rhino design steps; it is not a standalone data generator.

For focused diagnostics:

```powershell
dotnet run --project tools/ArcGISProMCP.DemoRunner -- --call request.json result.json
dotnet run --project tools/ArcGISProMCP.DemoRunner -- --image arcgis://resource/RETURNED_HANDLE output.png
./tools/test-mcp.ps1 -Configuration Release -ImageUri arcgis://resource/RETURNED_HANDLE
```

Match the McNeel router slot's PID with `system_get_state.processId`. Do not choose the first slot blindly. Use McNeel's `open_doc` import tool in an empty embedded document and verify units/earth anchor before sync. If document restoration leaves no matching router slot, invoke `rhino.mcp-reconnect` (default port 10500) and check the slots again. This runs McNeel's supported [MCPStart command](https://discourse.mcneel.com/t/rhino-mcp-server/216568/26), not a replacement modeling server. Resource handles expire on process restart.

## Issues found and remaining production work

1. **Raw pull is create-only.** Re-pulling into an existing Rhino layer created duplicate sync identities. The new adapter now rejects nonempty pull targets. The original nine proposal curves were retained, not deleted, on a hidden `Archive - Original Proposal Curves` layer; the newly imported GIS geometry is the active clean link. GlobalIDs alone do not make raw pull idempotent.
2. **GIS-to-Rhino geometry updates are not implemented by the current existing sync engine.** `PullOnly` is not a complete geometry-update path. Preview unexpected changes and stop for review; never force deletion/conflict resolution to make a demo pass.
3. **Native Rhino Open disrupted McNeel's endpoint.** In this installed platform version, replacing the document through the native Open command logged an MCP stop failure, pruned the router slot and left Pro stuck during shutdown. Both documents were saved before a scoped restart. The new `rhino.mcp-reconnect` operation successfully restored the endpoint after document restoration: McNeel adopted Pro PID 29908 and its C# tool returned the expected 572-object saved document. Recovery is explicit; native document replacement can still interrupt in-flight MCP calls. Do not claim seamless automatic lifecycle recovery.
4. **Approval and cancellation are conservative, not full job orchestration.** Confirmed operations require a fresh local Yes decision. Already-started short SDK/Rhino writes drain to completion and audit rather than pretending to cancel. Modal approval can occupy the bridge; durable jobs, an expiring review queue and a separately responsive control plane remain future work.
5. Broader SDK coverage, connection management, multi-instance routing, package signing, resource retention, comprehensive UI/E2E coverage, authoritative zoning/transit sources and optional ArcPy execution remain unfinished. The GitHub build workflow has been added but has not run remotely.
