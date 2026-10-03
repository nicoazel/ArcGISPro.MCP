# Demos and fixtures

These are development aids for exercising the add-in against a live ArcGIS Pro session. None of them is acceptance evidence; see [acceptance](acceptance/README.md) for what counts.

## Demo runner

`tools/ArcGISProMCP.DemoRunner` runs the three-map workflow against supplied local fixtures and saves structured results and a PNG. It also supports `--call request.json result.json` for focused bridge diagnostics.

The local demonstration artifacts are written to `artifacts/demo` (git-ignored). Its land-use parcels and street centerlines are test fixtures, not authoritative zoning designations or transit-service data. The proposed development is illustrative.

## Pittsburgh block showcase

The standalone showcase is [`workflows/pittsburgh-block-mixed-use-showcase.workflow.json`](../workflows/pittsburgh-block-mixed-use-showcase.workflow.json). It composes a plausible Pittsburgh mixed-use block concept into 3D massing, program and public-realm maps with live layout surrounds and dynamic text. Generated evidence is written under `artifacts/pittsburgh-showcase` (git-ignored). `tools/create-pittsburgh-block-showcase.py` generates its input data.

## Fixture data

Everything under `tests/data` is synthetic: `tools/create-synthetic-test-data.py` (ArcGIS Pro Python, fixed seed) generates the street grid, parcels, buildings, boundary, proposal footprints and the `DesignSites` editing fixture, runs `tools/create-urban-massing-fixture.py` to derive the proposal massing, and writes the parcel totals the stress workflows (`tools/run-urban-stress.ps1`) check to `tests/data/expected-statistics.json`. The data, its layout and how to regenerate it are described in [tests/data/README.md](../tests/data/README.md).

## Live probes

- `./tools/test-mcp.ps1` performs a real MCP handshake, tool discovery, live state request, registry search and skill read. Pass `-ImageUri` with an observation URI to verify native MCP image content.
- `./tools/verify-release.ps1 -Live` adds actual MCP discovery/state and a pending, then cancelled, local-review probe to the portable release checks. It never approves or runs a geoprocessing tool, so it does not replace feature, metadata, geoprocessing, ArcPy or local-review acceptance in ArcGIS Pro.

For running an agent without ArcGIS Pro, use FakeHost as described in [evals/README.md](../evals/README.md#against-fakehost---host-fakehost).
