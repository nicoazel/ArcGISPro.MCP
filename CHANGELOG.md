# Changelog

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Before 1.0, minor versions may contain breaking changes.

## [Unreleased]

### Added

- `SECURITY.md` with supported versions, private reporting through GitHub security advisories, and a threat model summary.
- `CONTRIBUTING.md` with prerequisites, build, test and packaging commands, and pull request conventions.
- This changelog and a public [roadmap](docs/ROADMAP.md).
- Bundled workflows are embedded in `ArcGISProMCP.Core` and seeded into `%LOCALAPPDATA%\ArcGISProMCP\workflows` at start-up, so `workflow_list` is populated on a fresh install. Seeding is a no-op once a workflow is present; existing files are never overwritten.
- Workflow operation allowlists: an optional `allowedOperations` list on workflows, enforced on save and run and reported as the validation issue `operation_not_allowed`. Operation descriptors gain `executesUserCode` (true for `gp.run` and `arcpy.run-script`), which `registry_search` and `registry_browse` entries also report. Allowlist entries are operation ids and are not version-pinned.
- `registry_search` accepts `capabilities` and `maxRisk` filters.
- Approval cards show a "Runs user code" warning for `arcpy.*` runs and for `gp.run` requests that use a custom toolbox (`.pyt`/`.atbx`/`.tbx`) or a Python expression; matching `gp.run` results carry a `user_code_execution` notice.
- Audit records gain `kind` (`operation`, `dry-run` or `approval`), `autonomousBypass`, `decision` and `actor`. Unknown operation ids and every local approve/deny decision are now audited.
- Audit log rotation: `operations.jsonl` rotates at 16 MiB and the newest five rotated files are kept.
- MCP resources: `arcgis://project/state` plus templates for operations, workflows (`id@version`), skills and observation handles.
- MCP prompts: one per bundled skill (`skill.<id>`) and, while ArcGIS Pro is running, one per saved workflow (`run.<id>`) with arguments from the workflow parameters.
- Typed tool results: every tool returns `{ ok, result, error }` as `structuredContent` (and the same JSON as its text block) with an `outputSchema`, and sets `isError` on failure with `error` = `code`, `message`, `retryable`, `revision`. `registry_invoke` and `workflow_run` results with `success: false` are errors that keep `result`. Bridge results are typed records shared by the add-in and the gateway (`ArcGISProMCP.Bridge.Protocol.BridgeContracts`).
- Every tool declares `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint` (always false). Only `registry_invoke` and `workflow_run` are destructive.
- `registry_describe` returns `resultSchema`, the JSON schema of the operation's `registry_invoke` result envelope. Its `data` member is the operation's `outputSchema`, now declared by 15 stable-shape operations (`project.get`, `map.list`, `layer.list`, `feature.*`, `table.query`, `table.statistics`, `metadata.get`, `layout.list`, `layout.inspect`, `view.capture`); `data` stays unconstrained for the rest, including `gp.*`.
- `registry_invoke` accepts `dryRun`: the operation is validated statically and never executed, needs no revision or approval token, and neither queues a review nor consumes a token. A completed dry run is not `isError` even when it reports `valid: false`. Combining `dryRun` with `idempotencyKey` fails with `dry_run_idempotency_conflict`.
- `approval_status` accepts `waitSeconds` (0-120) to wait for the dockpane decision instead of polling. Each wait holds a pipe slot, so the host allows at most two concurrent waits; further calls return the current status immediately with a `waitNotice` asking the client to poll.
- `tests/ArcGISProMCP.Server.Tests`: in-process MCP client/server harness with a scriptable fake bridge and `tools/list`, `resources/list` and `prompts/list` snapshots (`UPDATE_SNAPSHOTS=1` regenerates them).
- Geoprocessing operations `gp.search` and `gp.describe` (ReadOnly) over the installed system toolbox metadata, returning execution names, risk tiers and the positional parameter `signature` `gp.run` expects.
- `gp.query` (ReadOnly, no review) runs only the allowlisted read-only system tools `management.GetCount`, `management.GetRasterProperties` and `management.GetCellValue`, resolved against the system toolboxes, with no map outputs, no overwrite and no history.
- Geoprocessing risk tiers (UserCode, Destructive, ConsumesCredits, ReadOnlyQuery, Standard). Approval cards for `gp.run` say "Modifies/deletes input data in place" or "Consumes ArcGIS Online credits"; results carry `gp_mutates_input`, `gp_consumes_credits` or `gp_tool_not_indexed` notices.
- Static `gp.run` dry runs: operations can implement `IDryRunnableOperation`; `gp.run` dry runs validate parameters against the catalog and report the tier, confirmation, user-code and autonomous-refusal flags without executing. ArcGIS Pro has no validate-only geoprocessing API, so this is static validation, not the tool's own validation.
- The registry now has 41 operations (39 without the opt-in ArcPy pair); the MCP tool count stays 16.
- Draft MCP registry packaging: `dotnet pack` on `ArcGISProMCP.Server` now produces the `ArcGISProMCP.Gateway` NuGet dotnet tool (package type `McpServer`, command `arcgis-pro-mcp`) with `.mcp/server.json` for `io.github.nicoazel/arcgis-pro-mcp` and the bundled skills. `tools/pack-gateway.ps1` packs into `artifacts/packages` and verifies the package; nothing is published. The single-file release bundle is unchanged. See [deployment](docs/deployment.md#mcp-registry-package).
- Agent evaluations (`evals/`, see [evals/README.md](evals/README.md)): deterministic suites that run in every `dotnet test` without model calls. E1 routes 30 natural-language tasks through `registry_search` over the add-in's 41 real descriptors; E2 routes 30 tasks through `gp.search` over the installed ArcGIS Pro system toolboxes (skipped when Pro is absent) plus a 5-task subset over synthetic toolboxes that always runs. Held-out sets (16 + 16 tasks) were written before search tuning. Each suite reports recall@1, recall@5, MRR and misses, fails when one more task misses the top 5 than at the baseline in `evals/baseline.json` or MRR drops more than 0.05 below it, and can write a scorecard to `evals/results/<date>-<sha7>/`. A Python live harness (`evals/live/`) runs a Claude model against a running gateway by hand; it is not part of CI.
- MCP contract tests in `tests/ArcGISProMCP.Server.Tests`: an `initialize` snapshot, the bridge method and parameter shape of every tool (snapshot), success and error envelopes of every tool validated against its `outputSchema`, and malformed-call handling.
- `tests/ArcGISProMCP.Operations.Tests`: behavior tests for the portable operations over fake ArcGIS services ("fake Pro": real descriptors, registry and executor), and a guard that every portable descriptor is identical to what the add-in registered before the operations moved.
- Live acceptance tooling: `tools/run-acceptance.ps1` (Windows PowerShell 5.1 compatible) records the commit, .NET and ArcGIS Pro facts, and the hashes of the built and loaded add-in DLLs, runs the existing live harnesses by section, and with `-Commit` writes `docs/acceptance/<date>-<sha7>/` (`manifest.json`, `summary.md`, `SHA256SUMS`). [docs/acceptance/README.md](docs/acceptance/README.md) defines the folder contract and a Core test validates every committed manifest. No evidence is committed yet: it requires a maintainer run against live ArcGIS Pro.
- `tests/ArcGISProMCP.Testing` shared test library: FakePro, the scenario loader, `FakeHostRuntime` running the add-in's real bridge request handler, and `InProcessBridgeClient`. Not shipped.
- End-to-end tests (MCP client -> gateway -> bridge handler -> registry/executor -> fake ArcGIS services) covering the approval chain for `feature.delete`, `feature.update` and `project.save`; `gp.search`, `gp.describe`, `gp.run` dry run and run; autonomous refusal; and stale-revision and `workspace_changed` handling.
- `tools/ArcGISProMCP.FakeHost` development and eval host that serves a scenario project over the real named pipe with a discovery record (interactive y/n approvals; `--auto-approve` for eval runs only; `--autonomous`, `--toolboxes`), plus the `tools/test-fakehost.ps1` smoke test. Not shipped.
- E3 golden trajectories (`evals/trajectories`, 8 tasks, 4 of which exercise approval) replayed against the end-to-end server and graded on `schemaValidArgs`, `approvalDiscipline` (null for a trajectory with no gated invoke; one approval covers one invoke; gated invokes must carry `expectedRevision`) and `taskSuccess`; [evals/README.md](evals/README.md) explains how to run live evals against FakeHost.
- `tests/data/README.md` documents the provenance, coordinate system and use of the test data; a Core test checks that each shapefile set is complete.
- `-SkipTests` for `tools/verify-release.ps1` and `tools/package-release.ps1` (CI packages without re-running tests). `pack-addin.ps1`, `verify-release.ps1` and `package-release.ps1` declare `#Requires -Version 7.0`.

### Changed

- **Breaking:** in autonomous mode `gp.run` refuses Destructive and UserCode tools, tools the toolbox catalog cannot classify (fail closed), and requests flagged as running user code, with `destructive_tool_requires_review`, unless the request carries a person-issued approval token. Curated destructive tools (`management.Delete`, `Delete_management`, ...) are classified Destructive even when the catalog has not indexed them.
- **Breaking:** tool text content is now the `{ ok, result, error }` envelope rather than the bare bridge result; read `result` (or `structuredContent.result`). Failed bridge calls are `isError` results instead of protocol errors, and `skill_get` for an unknown id returns `skill_not_found`. `registry_browse` and `workflow_run` results now always carry all their members (unused ones are null). Enum fields such as `risk` remain numbers.
- **Breaking:** `project.open`, `project.save` and `feature.update` now require a local-review approval token (or autonomous mode). Dockpane buttons that trigger them approve through the same audited approval queue.
- **Breaking:** a workflow whose workspace revision changes mid-run now stops with `workspace_changed` (reporting `stoppedAtStep`, `stepIndex`, `expectedRevision`, `currentRevision`) instead of retrying the step against the new revision. `continueOnError` does not override this, and completed steps are not rolled back.
- **Breaking:** saved workflows can no longer use `gp.run` or `arcpy.run-script` unless their `allowedOperations` lists them. When `allowedOperations` is present it is exhaustive.
- **Breaking:** skill manifests without a non-empty `allowedOperations` are skipped when loading. The bundled master-cartography skill no longer lists `project.save`.
- **Breaking:** `resource.read` payloads use camelCase field names (`mimeType`, `name`, `createdAt`) instead of PascalCase.
- `tools/test-mcp.ps1` checks every tool's hints and envelope `outputSchema` and reads results from `structuredContent`.
- Registry search ignores articles and prepositions in queries.
- Tool descriptions no longer over-promise: `registry_validate` is described as a schema and revision check that does not resolve layers or paths, and the `registry_search` limit is documented as 1–100 to match the registry clamp.
- `JsonLineAuditLog` moved to `ArcGISProMCP.Core.Infrastructure`, and the Add-In resource store facade is renamed `ProResourceStore`.
- The live feature/GP/ArcPy acceptance script documents that it requires autonomous mode and asserts the `autonomous_control` notice for `feature.update` and `project.save`.
- One release status across all documentation: development preview; supported configuration is an interactive same-user workstation with dockpane approvals; autonomous mode is an opt-in expert setting, not recommended.
- `docs/production-readiness.md` merged into `docs/deployment.md`, which now also covers the implemented surface, acceptance evidence and known limits. The 2026-09-09 live acceptance evidence is described as local only and not verifiable from the repository.
- `docs/security.md` describes the implemented per-PID multi-instance discovery, states that `gp.run` can execute arbitrary Python (Python toolboxes, script tools, Calculate Field expressions) without ArcPy, and documents that workflows cannot execute confirmation-gated steps in default mode.
- The release bundle no longer includes the roadmap in its `docs/` folder.
- Bundled workflows are now version 1.1.0 and no longer end with `project.save`; save the project with an explicit `project.save` call.
- Operation input schemas are built with a typed `JsonSchemas` builder that self-checks each schema at startup; the string-based `JsonSchemas.ObjectSchema` helper is removed. Migrated schemas are semantically identical to the originals, guarded by a captured fixture.
- Operations seam: a new Esri-free `ArcGISProMCP.Operations` project holds the operation base class, every operation schema, and 21 of the 41 operations (`project.*`, `map.*`, `layer.list`, `view.capture`, `feature.*`, `gp.*`, `arcpy.*`), which call ArcGIS Pro through synchronous DTO services (`IProjectService`, `IMapService`, `ILayerService`, `IViewCaptureService`, `IFeatureService`, `IGeoprocessingService`) implemented in the add-in. Descriptors are unchanged. Layout, symbology, table, metadata and the remaining layer operations stay in the add-in. Source-text tests for the moved operations are replaced by behavior tests against fakes; the remaining source-text tests cover code that still needs ArcGIS Pro or WPF.
- `registry_search` and `gp.search` rank with a shared search index: punctuation stripping, camel-case splitting, light stemming, whole-word over prefix over compound matches, a softened inverse document frequency and a small synonym map of general GIS vocabulary. `gp.search` gives the core system toolboxes a modest prior. The held-out gains come from the matching changes (stemming, camel-case splitting, IDF, the core-toolbox prior); an ablation showed that synonyms only moved the main suites' recall@5 (held-out recall@5 was the same with or without them), so synonym entries fitted to single main-suite tasks were removed. Measured recall@5 before tuning -> now: registry-search 0.833 -> 0.867, gp-search 0.667 -> 0.900, held-out registry-search 0.938 -> 0.938, held-out gp-search 0.688 -> 0.875 (gp suites against ArcGIS Pro 3.7.1; ablation table in [evals/README.md](evals/README.md#search-tuning-phase-4)).
- `feature.query` and `feature.select` read a light layer schema (`IFeatureService.Schema`) instead of the full description, so they no longer evaluate editability before a read. Results are unchanged.
- `tools/package-release.ps1` fails if development-only hosts (FakeHost) or test support (`ArcGISProMCP.Testing`, scenario files) reach the release archive.
- CI: GitHub Actions are pinned to commit SHAs; the workflow is split into build-test, package and lint jobs with read-only permissions; test projects are discovered instead of listed, so new test projects run without editing the workflow; Dependabot updates NuGet packages and Actions weekly; PSScriptAnalyzer and ruff lint the `tools/` scripts.
- `tools/run-acceptance.ps1` rejects an explicit `-PipeName` that is not a live ArcGIS Pro discovery record, reports ambiguous cached add-in DLLs, and records `-VisuallyInspected` in `manifest.json` (`visuallyInspected[]`).
- CI: pushes run only for `main` and `v*` tags; the concurrency group uses the PR number; build-test runs the FakeHost smoke test; the package job no longer re-runs the tests; PSScriptAnalyzer is clean across `tools/`; ruff also lints `evals/`.

### Fixed

- `feature.*` operations failed on shapefile layers with "The definition of the table ... does not support this operation": ArcGIS throws from `HasGlobalID()` for shapefiles instead of returning false. Found by live acceptance; the GlobalID check is now guarded.
- `layer.add` with `name` reused an existing same-named layer even when its data source was broken or pointed at a different dataset, and echoed the requested source for it (`Created: false`), so the next step failed. It now reuses a layer only when its connection is not `Broken` and its dataset path matches the request; otherwise it swaps the feature class in place, keeping symbology (`Repaired: true`), or removes and re-adds the layer at the same position (`Replaced: true`), and adds a `layer_repaired` notice. `Source` is now the path the layer actually reads, with the request in `RequestedSource`, plus `DataSourceStatus`. Found by live ArcGIS Pro acceptance.
- Operations on a layer with a broken data source (`feature.*`, `table.query`, `table.statistics`, `symbology.set-simple`, `symbology.set-unique-values`, `layout.set-frame-extent`) failed with `operation_failed` "Object reference not set to an instance of an object." They now fail with `layer_data_source_unavailable` and a message naming the layer and how to repair it. Operations can raise the new Core `OperationException(code, message)`, which the executor reports with its own code. Found by live ArcGIS Pro acceptance.
- `feature.layer.describe` returns `spatialReference: null` instead of failing when a feature class has no spatial reference.
- In autonomous mode a Destructive, UserCode or unclassified `gp.run` request was refused with `destructive_tool_requires_review` even when it carried a valid person-issued approval token, so a reviewed request could never run. A request with a token now takes the normal confirmation path in autonomous mode: a valid token runs it (audited with `autonomousBypass: false`), an invalid one fails with `confirmation_required` without falling back to the bypass, and only token-less requests hit the unattended refusal, whose message now says to request local review with `approval_request` and retry with the token.
- Malformed tool arguments (a missing required argument or a value of the wrong JSON type) return the standard envelope with `error.code` `invalid_arguments`, a message naming the argument and `retryable: false`, instead of the SDK's generic error text without `structuredContent`. Unknown tool names remain a JSON-RPC error.
- In-process hosts built concurrently could intermittently publish the injected bridge client as a required `bridge` tool argument (a race on the reflection `ParameterInfo` cache that the MCP SDK relies on). Tools and resources are now built once per process, and the server tests run in parallel again.
- The dockpane no longer shows a hard-coded "Skills 1" count. Skills are loaded by the MCP gateway process, which the add-in cannot see, so the expander header now shows only the workflow count.
- An audit write failure for an unknown operation id now adds the `audit_write_failed` notice, like every other invocation. A failed approval audit write in the dockpane is reported in the activity feed whatever the exception type, instead of surfacing as an error after the decision was already applied.
- `invalid_workflow` errors from `workflow_save` and `workflow_run` prefix each issue with its code (for example `operation_not_allowed: ...`), so clients can tell policy violations from other validation failures.
- A failed audit log rotation (for example while another Pro process holds `operations.jsonl`) no longer drops the audit record; rotation is skipped and the record is appended. The log is appended through a handle that shares read, write and delete access with other processes, and rotation happens only once the file exceeds 16 MiB, as documented.
- A request cancelled by ArcGIS Pro shutting down now fails with the retryable code `host_stopping` instead of the generic `bridge_request_failed`. `docs/security.md` states that keyed requests cannot be cancelled by callers once started.
- Idempotent registry and workflow requests run under the host lifetime rather than the first caller's cancellation token, so cancelling one caller no longer cancels work another caller with the same key is waiting on.
- FakeHost `--help` gave the wrong default pipe name (it is `ArcGISProMCP.v1.<pid>`).

### Security

- Confirmation now covers project replacement (`project.open`), project persistence (`project.save`) and attribute/geometry edits (`feature.update`).
- Workflows can no longer silently adopt a newer workspace revision and continue writing against state nobody reviewed. Only a successful write step advances the run's expected revision; a read-only step or a `continueOnError` failure that observes a mid-run change no longer authorizes the following writes.
- User-code operations are opt-in per workflow, and approvals that execute Python are visibly labelled.
- `gp.run` requests for Calculate Field, Calculate Fields and Calculate Value are labelled as running user code even without an explicit expression type, because ArcGIS Pro defaults it to Python 3. Python toolboxes called by an imported alias remain a documented false negative for the user-code label; autonomous mode refuses them as unclassified.
- Approval decisions, autonomous bypasses and unknown operation ids are recorded in the audit log.
- User `.atbx` toolboxes are read as untrusted archives: each metadata entry is bounded to 16 MiB of decompressed bytes whatever size it declares, an archive may hold at most 10,000 entries and 64 MiB of metadata, and script (`.py`) entries are only checked for presence, never read. An archive over a limit is reported as unreadable.
- Approvals stay in the ArcGIS Pro dockpane: MCP elicitation is intentionally not used, so the client UI cannot become the approval authority. `approval_status` `waitSeconds` only waits for the dockpane decision.
- A dockpane button click (for example **Open project**) can no longer approve an identical request that an MCP client queued for review. The panel's self-approval always creates its own approval entry (`IApprovalService.Request(..., reuseExisting: false)`), and the transient entry no longer flashes in the approval cards.
- The gateway never selects a development FakeHost automatically. Discovery records carry `hostKind` (`arcgis-pro` | `fakehost`; missing means `arcgis-pro`), and FakeHost records are used only when selected with `ARCGIS_PRO_MCP_HOST_PID`/`ARCGIS_PRO_MCP_PIPE` or the development opt-in `ARCGIS_PRO_MCP_ALLOW_FAKEHOST=true`. FakeHost prefixes its project name with `[FakeHost] ` and warns when ArcGIS Pro is running.

### Removed

- Rhino-era documents (`docs/integrated-rhino-*`, `docs/master-plan-demo.md`) and the internal `docs/FRESH_CONTEXT_HANDOFF.md`.
- Machine-specific checkout paths from the manual acceptance procedure.

## [0.2.0] - 2026-09-26

First tagged development preview of the standalone ArcGIS-only product. Unsigned; not a production release.

### Added

- ArcGIS Pro 3.7 add-in with a thin WPF/MVVM MCP Studio dockpane.
- Stdio MCP gateway built on the ModelContextProtocol C# SDK 2.2.0, exposing 16 tools: state, registry search/browse/describe/validate/invoke, local-review approval request/status/cancel, workflow list/get/save/run, skill search/get, and resource read.
- Searchable operation registry with 38 curated operations and risk levels, including:
  - project inspection, open and save; map and scene creation and activation; basemaps;
  - layer loading, visibility, transparency, simple and categorical polygon symbology, labels, style search, selection clearing, and scene elevation placement;
  - bounded feature-table queries and statistics; typed feature-layer inspection, attribute/spatial query and selection, and single-feature create, update and delete;
  - feature-layer metadata read and update that preserves unrelated XML;
  - generic SDK geoprocessing (`gp.run`) with bounded parameters, environments, messages and derived values;
  - layouts, map frames, text, legends, north arrows, scale bars, dynamic text, PNG export, and map-view capture.
- Optional ArcPy runner (`arcpy.inspect-script`, `arcpy.run-script`), disabled by default, with hash-pinned scripts, configured roots, and bounded arguments, output and time.
- Local review: expiring single-use approval tokens bound to operation version, canonical arguments and workspace revision, issued only from the dockpane.
- Opt-in autonomous mode (`ARCGIS_PRO_MCP_AUTONOMOUS_MODE`) that bypasses local review while keeping revision checks, schema validation, audit and `autonomous_control` notices.
- Same-user named-pipe bridge with versioned, length-prefixed JSON (8 MiB cap), bounded connection slots, frame timeouts, and a serialized execution gate.
- Per-PID pipes and host discovery records; the gateway auto-selects a single host and fails closed when several are ambiguous (`ARCGIS_PRO_MCP_HOST_PID`, `ARCGIS_PRO_MCP_PIPE`).
- Workspace revisions for optimistic concurrency, process-lifetime idempotency keys, `outcome_unknown` reporting, and JSON-lines audit records.
- Immutable, versioned JSON workflows with run history and ranking; five bundled workflows and a bundled skill.
- Bounded image observation store with opaque `arcgis://` handles.
- Release tooling: `tools/verify-release.ps1`, `tools/package-release.ps1` (unsigned bundle with checksums and offline MCP smoke test), `tools/test-mcp.ps1`, and a Windows CI build.

[Unreleased]: https://github.com/nicoazel/ArcGISPro.MCP/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/nicoazel/ArcGISPro.MCP/releases/tag/v0.2.0
