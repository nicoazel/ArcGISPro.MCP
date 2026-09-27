# Production readiness

Target: standalone ArcGIS Pro 3.7.1 workstation automation.

Status on 2026-09-09: **accepted for a trusted, same-user, autonomous workstation deployment within the documented feature scope.** It remains an unsigned development-preview distribution, not a signed public release. Durable jobs, explicit remote cancellation of an accepted SDK geoprocessing call, and several broader editing/catalog capabilities remain outside the current contract.

## Acceptance result

| Gate | Result | Evidence |
| --- | --- | --- |
| Portable release | Passed | Core 99/99 and Bridge 24/24; complete Release solution built with 0 warnings and 0 errors; `git diff --check` passed |
| Exact package and loaded assembly | Passed | Final registered deterministic add-in SHA-256 `B7A175F591A5DCBEF06065466B896A6FA6085338F3BB45A855CF4F6DF5C9102C`; two consecutive package builds matched; packaged and loaded `ArcGISProMCP.AddIn.dll` SHA-256 both `42C53E716BA363DD4A5995ABA7680A42809B2D7431F7CA0F88BAD714798E7281`; see `artifacts/live-acceptance/final-repackaged-loaded-hash.json` |
| MCP protocol and discovery | Passed | Two fresh stdio clients completed initialize, 16-tool discovery, live state, registry search/describe/validate, bundled skill retrieval, native image content, pending/cancel review, and reconnect; see `artifacts/live-acceptance/final-mcp-protocol-reconnect.json` |
| Multiple ArcGIS Pro instances | Passed | Ambiguous discovery failed closed and explicit PID selectors stayed bound to their intended project; see `artifacts/live-acceptance/multi-instance-ambiguous.result.json` and `multi-instance-selected-*.result.json` |
| Feature editing | Passed for current geometry scope | A disposable GlobalID file geodatabase proved create, query, spatial query/select, update and delete; wrong geometry and stale revision failed closed; the retained feature survived project save and Pro reload |
| Layer metadata | Passed for map-layer metadata | Title/summary/description/tags/credits/use limitations survived save and reload. All 11 unrelated XML leaf values compared before and after remained; see `artifacts/live-acceptance/final-package-reload/04-unrelated-xml-preservation.json` |
| Layer spatial metadata | Passed | `layer.set-elevation` manages scene elevation mode, offset and unit. The mixed-use and Pittsburgh massing layers were placed `RelativeToGround`, preventing geometry from being hidden below the scene surface |
| SDK geoprocessing | Passed except explicit remote cancel | Buffer and GetCount ran ten times with deterministic outputs/counts, returned messages/derived values/types, rejected overwrite, and byte-identically replayed a completed same-key request without re-execution. Cancellation is cooperative during host shutdown; a client disconnect after acceptance is deliberately reported as `outcome_unknown`, not as cancellation |
| ArcPy escape hatch | Passed in autonomous mode | Hash-pinned inspect/run imported ArcPy 3.7.1, created scratch data, rejected missing/changed scripts, returned nonzero exit 7, killed a timed-out process tree, bounded stdout/stderr to 65,536 characters each, and reconciled an uncertain same-key result |
| Urban layout stress | Passed | TOD corridor, green loop and mixed-use massing each completed ten iterations through a 2400x1553 final layout. Structural inspection rejected duplicate names and required three frames plus legend/north arrow/scale bar/dynamic text; final images were visually inspected for blank frames and duplication |
| Pittsburgh showcase | Passed | A generated EPSG:2272 block plan produced 12 buildings, five public spaces, street/block context, ground-relative 3D massing, program and public-realm maps, and a composed 17x11-inch layout. Final inspection found three unique map frames and all required live surrounds |
| Idle/shutdown | Qualified pass | One exact-package host remained responsive and stable during an idle sample and closed normally in 10.08 seconds. Two disposable Pro instances also showed intermittent slow shutdown; one required a PID-scoped forced close after 75 seconds. This is retained as a vendor-host lifecycle risk, not hidden as a clean universal pass |

## Implemented surface

- Searchable registry with curated project, map, scene, layer, cartography, feature, table, metadata, geoprocessing, layout, observation, workflow and optional ArcPy operations.
- Typed point, single-part polyline and single-part polygon feature CRUD with GlobalID-first addressing, bounded queries, spatial filters, selection and revision checks.
- Metadata read/update that preserves unrelated ArcGIS XML, plus scene elevation placement metadata.
- Live legends, north arrows, scale bars and dynamic project/date/map-frame-scale text.
- Optional trusted autonomous mode. It bypasses panel review only; schemas, path policy, revisions, auditing, bounded output and idempotency still apply.
- Per-PID host discovery with fail-closed ambiguity for concurrent ArcGIS Pro projects.

## Known scope limits

- Feature editing excludes batch edits, multipoint construction, multipart construction and complete subtype/domain/range validation.
- Metadata update targets a map layer's ArcGIS metadata API; standalone catalog-item metadata editing is not claimed.
- Once an SDK geoprocessing write is accepted, the public protocol has no durable job id or explicit remote cancel command. A disconnected caller receives `outcome_unknown` and must inspect state or retry with the same idempotency key.
- Process-lifetime idempotency, workflow history and resources are not a durable cross-restart job store. Workflows are not transactional or resumable.
- Advanced renderers, workspace connection management and PDF export are outside the curated registry.
- ArcPy is arbitrary trusted local code, not a sandbox. Autonomous mode should be enabled only for a trusted same-user client and constrained script roots.
- The package is unsigned. Wider production distribution requires an organizational signing certificate, publisher policy and another installed-package verification pass over the signed artifact.
- ArcGIS Pro shutdown was not perfectly repeatable across disposable instances. Automation must keep PID-scoped ownership, save first, request normal close, wait, and only terminate a verified disposable process as a last resort.

## Acceptance artifacts

- `artifacts/live-acceptance/final-package-evidence-2/summary.json`
- `artifacts/live-acceptance/final-repackaged-loaded-hash.json`
- `artifacts/live-acceptance/final-mcp-protocol-reconnect.json`
- `artifacts/live-acceptance/final-package-reload/`
- `artifacts/urban-stress/final-package-tod-green-10x/`
- `artifacts/urban-stress/final-package-mixed-10x/`
- `artifacts/pittsburgh-showcase/`
- `workflows/pittsburgh-block-mixed-use-showcase.workflow.json`
