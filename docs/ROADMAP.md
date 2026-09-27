# Roadmap

This roadmap comes from a review of the 0.2.0 development preview. It is ordered by priority: first make the documentation and safety claims match the code, then complete the MCP protocol surface, then improve geoprocessing usability, testing and release evidence. Items are plans, not commitments, and may change. Completed work is recorded in the [changelog](../CHANGELOG.md).

Decisions already taken:

- `feature.update`, `project.save` and `project.open` will require confirmation.
- Autonomous mode stays, documented as an opt-in expert setting that is not recommended.

## Phase 0: hygiene and one readiness story

- 0.1 Tag 0.2.0.
- 0.2 Remove Rhino-era documents and references.
- 0.3 Remove internal handoff notes and machine-specific paths.
- 0.4 Merge deployment and production-readiness documentation into one status: development preview; supported configuration is an interactive same-user workstation with dockpane approvals; autonomous mode is an opt-in expert setting, not recommended.
- 0.5 Make the security documentation reflect the implemented per-PID multi-instance discovery.
- 0.6 Add `SECURITY.md`, `CONTRIBUTING.md`, `CHANGELOG.md` and this roadmap.
- 0.7 Seed the bundled workflows on first run so `workflow_list` is not empty on a fresh install.

## Phase 1: make the safety claims true

- 1.1 Stop a workflow with `workspace_changed` and the step index when the workspace revision changes mid-run, instead of retrying.
- 1.2 Enforce an allowed-operations list for workflows and skills; block operations that execute user code (`gp.run`, `arcpy.*`) in saved workflows unless explicitly allowed.
- 1.3 Require confirmation for `feature.update`, `project.save` and `project.open`; keep batch limits on `feature.*`.
- 1.4 Be explicit that `gp.run` executes user code: document it, and show a "runs user code" warning in the approval UI for Python toolboxes and Python expressions.
- 1.5 Make tool descriptions accurate: `registry_describe` output schemas, capability and risk filters for `registry_search`, and describe `registry_validate` as a schema and revision check rather than a live-state check.
- 1.6 Audit approvals and denials, autonomous bypasses and unknown operation ids; add size-based audit log rotation.
- 1.7 Smaller fixes: duplicate type names, consistent resource wire casing, search stop-words, and idempotent execution that does not depend on the first caller's cancellation.

## Phase 2: MCP protocol completeness

- 2.1 Explicit read-only, destructive, idempotent and open-world annotations on every tool, enforced by a test.
- 2.2 Typed results with structured content and an output schema on every tool, keeping the text block.
- 2.3 `isError: true` on failures with a uniform error envelope (`code`, `message`, `revision`, `retryable`).
- 2.4 Per-operation result schemas returned by `registry_describe`, with contract tests.
- 2.5 MCP resources for project state, operations, skills, workflows and observations.
- 2.6 MCP prompts generated from workflows and skills.
- 2.7 `approval_status` can wait (`waitSeconds`, up to 120) for the dockpane decision instead of polling. Elicitation is not used for approval: it would make the client look like an approval authority, and many clients lack it. URL-mode elicitation that points at the dockpane decision remains a possible future addition.
- 2.8 Build JSON schemas with structured builders instead of string concatenation.

## Phase 3: geoprocessing usability

- 3.1 `gp.search` over installed toolboxes.
- 3.2 `gp.describe` with parameter metadata.
- 3.3 Geoprocessing risk tiers: a read-only allowlist without approval, a destructive denylist that always needs approval, and flagging of Python toolboxes and expressions.
- 3.4 `dryRun` on `registry_invoke` (geoprocessing validate-only).

## Phase 4: behavioral tests and evaluations

- 4.1 An ArcGIS service seam (geoprocessing, editing, map and layout services) with plain DTOs.
- 4.2 Replace source-text tests with behavior tests against fakes.
- 4.3 In-process MCP contract tests and a `tools/list` snapshot.
- 4.4 An agent evaluation suite of 20 to 30 tasks with a scorecard.
- 4.5 Use or remove the unused test data.

## Phase 5: evidence and release

- 5.1 A live acceptance script that records the commit, ArcGIS Pro version and DLL hashes into committed, reviewable evidence.
- 5.2 Pin GitHub Actions to commit SHAs, enable Dependabot, and add PSScriptAnalyzer and ruff.
- 5.3 0.3.0 release notes and checksums.
- 5.4 An MCP registry listing.
- 5.5 README additions: comparison with alternatives, architecture diagram, security model, and the evaluation scorecard.
