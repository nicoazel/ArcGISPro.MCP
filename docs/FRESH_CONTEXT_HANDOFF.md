# Fresh-context handoff

This repository is the standalone ArcGIS Pro MCP product. Do not fold it back into Rhino.Inside-ArcGIS. The three intended products are:

1. `ArcGISPro.MCP`: ArcGIS Pro add-in, stdio gateway, registry, workflows and optional ArcPy.
2. `Rhino.Inside-ArcGIS`: companion integration product that may include or coordinate the ArcGIS MCP package.
3. McNeel Rhino MCP: Rhino's own modeling endpoint.

## Current position

Branch: `codex/initial-mcp` in `D:\_11_Git\ArcGISPro.MCP`. No remote is configured. The worktree intentionally contains the standalone extraction and hardening work; preserve it and do not reset user changes.

The ArcGIS-only runtime, feature editing, map-layer metadata editing, scene elevation controls, geoprocessing, optional hash-pinned ArcPy, multi-instance discovery, layouts and workflows are implemented and covered by portable tests. Exact-package live acceptance and a three-workflow 30-run visual stress pass were completed on 2026-09-09. The Pittsburgh block showcase is the final integrated demonstration.

Read `docs/production-readiness.md` first. It is the authoritative acceptance matrix and scope boundary. Useful evidence is under:

- `artifacts/live-acceptance/`
- `artifacts/urban-stress/final-package-tod-green-10x/`
- `artifacts/urban-stress/final-package-mixed-10x/`
- `artifacts/pittsburgh-showcase/`

## Important operational facts

- Each ArcGIS Pro process publishes its own named pipe and discovery record. With multiple projects open, require `ARCGIS_PRO_MCP_HOST_PID=<pid>` or an explicit pipe. Ambiguity must fail closed.
- Scene geometry that should sit on the surface must expose manageable layer elevation metadata. Use `layer.set-elevation` with `RelativeToGround` when zero-Z or design-height geometry would otherwise be hidden under the scene ground.
- GlobalID acceptance requires a disposable file geodatabase, not a shapefile.
- A client timeout after a mutation is transmitted does not cancel accepted work. Report `outcome_unknown`; reconcile by observation or the same idempotency key.
- ArcPy is a trusted escape hatch. Keep it disabled by default; when enabled, restrict script roots and require an inspected SHA-256 hash.
- Autonomous mode is the intended mode for the user's agent-controlled workstation. It does not disable validation, revisions, auditing, path bounds or idempotency.
- Do not alter or close an ArcGIS Pro or Rhino process unless it was created for the test and its exact PID/title were verified.

## Remaining product work

These are explicit future-scope items, not hidden acceptance failures:

- durable cross-restart jobs and idempotency;
- explicit remote cancellation for an accepted ArcGIS SDK geoprocessing job;
- batch, multipoint and multipart feature editing plus complete domain/subtype enforcement;
- catalog-item metadata editing;
- PDF layout export, advanced renderers and workspace connection management;
- organization signing and publisher deployment policy;
- investigation of intermittent slow ArcGIS Pro shutdown in disposable instances.

## Verification commands

Run from `D:\_11_Git\ArcGISPro.MCP`:

```powershell
dotnet test tests/ArcGISProMCP.Core.Tests -c Release --no-restore
dotnet test tests/ArcGISProMCP.Bridge.Tests -c Release --no-restore
dotnet build ArcGISPro.MCP.slnx -c Release --no-restore
git diff --check
./tools/package-release.ps1
```

Do not claim a rebuilt DLL is loaded in an existing ArcGIS Pro process. For new host acceptance, install the exact package, restart Pro, and compare the packaged and loaded assembly hashes.

## Pittsburgh showcase

- Workflow: `workflows/pittsburgh-block-mixed-use-showcase.workflow.json`
- Generator: `tools/create-pittsburgh-block-showcase.py`
- Generated geodatabase and final layout PNG: `artifacts/pittsburgh-showcase/`
- Saved ArcGIS Pro project: `artifacts/pittsburgh-showcase/Pittsburgh-Block-Mixed-Use-Showcase.aprx`

The design is an illustrative planning concept, not an entitlement, survey or feasibility claim. It uses local projected coordinates and plausible program assumptions to prove the toolchain rather than claiming authoritative city decisions.
