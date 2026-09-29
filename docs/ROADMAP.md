# Roadmap

This roadmap comes from a review of the 0.2.0 development preview. It is ordered by priority: first make the documentation and safety claims match the code, then complete the MCP protocol surface, then improve geoprocessing usability, testing and release evidence. Items are plans, not commitments, and may change. Completed items are marked **Done**, with a note where the implementation differs from the plan; details are in the [changelog](../CHANGELOG.md).

Decisions already taken:

- `feature.update`, `project.save` and `project.open` will require confirmation.
- Autonomous mode stays, documented as an opt-in expert setting that is not recommended.

## Phase 0: hygiene and one readiness story

- **Done** 0.1 Tag 0.2.0.
- **Done** 0.2 Remove Rhino-era documents and references.
- **Done** 0.3 Remove internal handoff notes and machine-specific paths.
- **Done** 0.4 Merge deployment and production-readiness documentation into one status: development preview; supported configuration is an interactive same-user workstation with dockpane approvals; autonomous mode is an opt-in expert setting, not recommended.
- **Done** 0.5 Make the security documentation reflect the implemented per-PID multi-instance discovery.
- **Done** 0.6 Add `SECURITY.md`, `CONTRIBUTING.md`, `CHANGELOG.md` and this roadmap.
- **Done** 0.7 Seed the bundled workflows on first run so `workflow_list` is not empty on a fresh install.

## Phase 1: make the safety claims true

- **Done** 1.1 Stop a workflow with `workspace_changed` and the step index when the workspace revision changes mid-run, instead of retrying.
- **Done** 1.2 Enforce an allowed-operations list for workflows and skills; block operations that execute user code (`gp.run`, `arcpy.*`) in saved workflows unless explicitly allowed.
- **Done** 1.3 Require confirmation for `feature.update`, `project.save` and `project.open`; keep batch limits on `feature.*`.
- **Done** 1.4 Be explicit that `gp.run` executes user code: document it, and show a "runs user code" warning in the approval UI for Python toolboxes and Python expressions.
- **Done** 1.5 Make tool descriptions accurate: `registry_describe` output schemas, capability and risk filters for `registry_search`, and describe `registry_validate` as a schema and revision check rather than a live-state check.
- **Done** 1.6 Audit approvals and denials, autonomous bypasses and unknown operation ids; add size-based audit log rotation.
- **Done** 1.7 Smaller fixes: duplicate type names, consistent resource wire casing, search stop-words, and idempotent execution that does not depend on the first caller's cancellation.

## Phase 2: MCP protocol completeness

- **Done** 2.1 Explicit read-only, destructive, idempotent and open-world annotations on every tool, enforced by a test.
- **Done** 2.2 Typed results with structured content and an output schema on every tool, keeping the text block.
- **Done** 2.3 `isError: true` on failures with a uniform error envelope (`code`, `message`, `revision`, `retryable`).
- **Done** 2.4 Per-operation result schemas returned by `registry_describe`, with contract tests. `resultSchema` wraps the operation's `outputSchema`, declared by 15 stable-shape operations; the `gp.*` results stay unconstrained. The schemas are checked against representative payloads and, through the Phase 4 service seam, against fake-service results; checking them against live ArcGIS Pro results remains to be done once live evidence is recorded.
- **Done** 2.5 MCP resources for project state, operations, skills, workflows and observations.
- **Done** 2.6 MCP prompts generated from workflows and skills.
- **Done** 2.7 (replaces the planned elicitation-based approval) `approval_status` can wait (`waitSeconds`, up to 120) for the dockpane decision instead of polling. Elicitation is not used for approval: it would make the client look like an approval authority, and many clients lack it. URL-mode elicitation that points at the dockpane decision remains a possible future addition.
- **Done** 2.8 Build JSON schemas with structured builders instead of string concatenation. Every input schema is migrated and the string helper is removed.

## Phase 3: geoprocessing usability

- **Done** 3.1 `gp.search` over installed toolboxes. It indexes system toolbox metadata on disk; Python toolboxes and legacy binary `.tbx` files are reported as unindexed.
- **Done** 3.2 `gp.describe` with parameter metadata.
- **Done** 3.3 Geoprocessing risk tiers: a read-only allowlist without approval, a destructive denylist that always needs approval, and flagging of Python toolboxes and expressions. The read-only allowlist is a separate ReadOnly operation, `gp.query`, because `gp.run` stays confirmation-gated; autonomous mode refuses Destructive and UserCode tools.
- **Done** 3.4 `dryRun` on `registry_invoke` (geoprocessing validate-only). ArcGIS Pro has no validate-only geoprocessing API, so a `gp.run` dry run is static validation against the toolbox metadata plus the risk and approval flags; other operations check only their input schema.

## Phase 4: behavioral tests and evaluations

- **Done** 4.1 An ArcGIS service seam with plain DTOs, by extraction: the Esri-free `ArcGISProMCP.Operations` project holds the operation base, every schema and 21 of the 41 operations (project, map, `layer.list`, view capture, features, geoprocessing, ArcPy) behind project, map, layer, view-capture, feature and geoprocessing services. Layout, symbology, table, metadata and the remaining layer operations stay in the add-in; moving `metadata.*` behind a metadata service is the next candidate.
- **Partly done** 4.2 Replace source-text tests with behavior tests against fakes. The moved operations are tested through a fake ArcGIS Pro (real descriptors, registry and executor, fake services), and schema and descriptor checks compare against a dump of every descriptor the add-in registers. Source-text tests remain for code that still needs ArcGIS Pro (layout convergence, layer elevation, metadata rollback, ArcPy process handling, catalog wiring) or the WPF panel. Moving `metadata.*` behind an `IMetadataService` was deferred past the next preview: it touches the service record, the fake catalog, the descriptor guard and FakeHost's operation set, and is not needed for release.
- **Done** 4.3 In-process MCP contract tests: `initialize`, `tools/list`, `resources/list` and `prompts/list` snapshots, the bridge method and parameters of every tool, and success, error and malformed-argument envelopes of every tool validated against its `outputSchema`. End-to-end tests drive an MCP client through the gateway into the add-in's real request handler, registry and executor over fake ArcGIS services (`tests/ArcGISProMCP.Testing`), and `tools/ArcGISProMCP.FakeHost` serves the same runtime over the real named pipe.
- **Partly done** 4.4 An agent evaluation suite with a scorecard: registry and geoprocessing search suites (30 tasks each, plus held-out sets) run in every `dotnet test` and gate on a measured baseline; scorecards are committed under `evals/results`. Eight golden tool-call trajectories (E3) are replayed against the in-process end-to-end server on every run and graded on schema-valid arguments, approval discipline and task success; four of the eight exercise approval. After an ablation showed that search synonyms moved only the main suites' recall@5, synonyms fitted to single main-suite tasks were removed and the baselines lowered (see `evals/README.md`). The descriptor dump used by the registry suites is still edited by hand when a descriptor changes; a tool to regenerate it from the built add-in is not written yet. A live harness (against ArcGIS Pro or FakeHost) exists but is run by hand; no live-model scorecard is committed yet.
- **Done** 4.5 The test data is kept and documented (`tests/data/README.md`), with a check that each shapefile set is complete.

## Phase 5: evidence and release

- **Done (partial scope)** 5.1 A live acceptance script that records the commit, ArcGIS Pro version and DLL hashes into committed, reviewable evidence. First evidence: [`docs/acceptance/2026-09-28-5f34f16`](acceptance/2026-09-28-5f34f16/summary.md) covers verify, the loaded-DLL match, host probe, the MCP smoke test and 3 bundled workflows x 3 runs on ArcGIS Pro 3.7.1, in default (review-required) mode. The autonomous-mode `feature-gp-arcpy` section has not been recorded. The live runs found and fixed four defects (broken-layer handling in `layer.add`, NullReferenceExceptions on broken data sources, `HasGlobalID` on shapefiles, and workspace revision drift from ArcGIS event echoes).
- **Done** 5.2 Pin GitHub Actions to commit SHAs, enable Dependabot, and add PSScriptAnalyzer and ruff.
- 5.3 0.3.0 release notes and checksums. Pending the maintainer's release, after 5.1 evidence.
- **Draft** 5.4 An MCP registry listing. The gateway packs as an `McpServer` NuGet tool with `server.json` (`tools/pack-gateway.ps1`); publishing needs the maintainer's NuGet and registry accounts and has not happened.
- **Done** 5.5 README rewrite: comparison with alternative designs, architecture diagram, quick start, security model summary, quality and evaluation scorecard, and the acceptance evidence status.
