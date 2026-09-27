# Changelog

All notable changes to this project are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Before 1.0, minor versions may contain breaking changes.

## [Unreleased]

### Added

- `SECURITY.md` with supported versions, private reporting through GitHub security advisories, and a threat model summary.
- `CONTRIBUTING.md` with prerequisites, build, test and packaging commands, and pull request conventions.
- This changelog and a public [roadmap](docs/ROADMAP.md).
- Bundled workflows are embedded in `ArcGISProMCP.Core` and seeded into `%LOCALAPPDATA%\ArcGISProMCP\workflows` on first load, so `workflow_list` is populated on a fresh install. Existing files are never overwritten.

### Changed

- One release status across all documentation: development preview; supported configuration is an interactive same-user workstation with dockpane approvals; autonomous mode is an opt-in expert setting, not recommended.
- `docs/production-readiness.md` merged into `docs/deployment.md`, which now also covers the implemented surface, acceptance evidence and known limits. The 2026-09-09 live acceptance evidence is described as local only and not verifiable from the repository.
- `docs/security.md` describes the implemented per-PID multi-instance discovery, states that `gp.run` can execute arbitrary Python (Python toolboxes, script tools, Calculate Field expressions) without ArcPy, and documents that workflows cannot execute confirmation-gated steps in default mode.
- The release bundle no longer includes the roadmap in its `docs/` folder.
- Bundled workflows are now version 1.1.0 and no longer end with `project.save`; save the project with an explicit, approved `project.save` call.

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
