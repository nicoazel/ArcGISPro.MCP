# ArcGIS Pro MCP Studio documentation

ArcGIS Pro MCP Studio lets a Model Context Protocol client drive a running ArcGIS Pro 3.7 session through a small gateway, a searchable operation registry, and a local review panel. Start with the path that matches what you are doing.

**Status: development preview.** Supported: interactive same-user workstation with dockpane approvals. Autonomous mode is an opt-in expert setting, not recommended. See [deployment](deployment.md#status).

```mermaid
flowchart LR
    client["MCP client"] -->|stdio| server["ArcGISProMCP.Server<br/>16 gateway tools"]
    server -->|"same-user named pipe<br/>length-prefixed JSON"| addin["ArcGISProMCP.AddIn<br/>inside ArcGIS Pro"]
    addin --> registry["Operation registry<br/>41 operations"]
    registry --> sdk["ArcGIS Pro SDK<br/>MCT / UI thread"]
    addin -.->|"risky operations"| panel["MCP Studio panel<br/>local review"]
    registry -.->|"opt-in only"| arcpy["ArcPy worker"]
```

## Choose a path

| I want to... | Read |
| --- | --- |
| Build, install, and connect a client | [Main README quick start](../README.md#quick-start), then [deployment and rollback](deployment.md) |
| Understand how requests flow and where code lives | [Architecture](architecture.md) |
| Look up a tool, operation, environment variable, or workflow | [Reference](reference.md) |
| Decide whether it is safe to run on a workstation | [Security and operational limits](security.md) |
| Enable the optional Python escape hatch | [ArcPy configuration](arcpy.md) |
| Check release status, evidence, and known limits | [Deployment: status and known limits](deployment.md#status) |
| Sign off a build on a live ArcGIS Pro install | [Manual acceptance](manual-acceptance.md), then record it as [acceptance evidence](acceptance/README.md) |
| Measure how well an agent finds and uses operations | [Evaluations](../evals/README.md) |
| Try the demos or run an agent without ArcGIS Pro | [Demos and fixtures](demos.md), [FakeHost](../evals/README.md#against-fakehost---host-fakehost) |

## Guides

### Get started
- **[Deployment, status and rollback](deployment.md)**: the release status and supported configuration, building and verifying the bundle, configuring the MCP client, acceptance evidence, known limits, and rollback.
- **[Architecture](architecture.md)**: process boundaries, the six-step operation lifecycle, threading rules, and how to add a new operation.

### Operate safely
- **[Security and operational limits](security.md)**: the trust boundary, revision checks, approval tokens, workflow operation allowlists, geoprocessing risk tiers, audit records, autonomous mode, idempotency, pipe limits, and `outcome_unknown` semantics.
- **[ArcPy configuration](arcpy.md)**: turning on hash-pinned ArcPy scripts, with their script and working roots, limits, and trust boundary.

### Verify and release
- **[Manual acceptance](manual-acceptance.md)**: the live checklist for feature data, metadata, geoprocessing, ArcPy, and stability on a disposable project.
- **[Acceptance evidence](acceptance/README.md)**: the committed evidence folder contract, how `tools/run-acceptance.ps1` produces an entry, and what the manifest test checks. No entry is committed yet.
- **[Evaluations](../evals/README.md)**: retrieval suites (E1 registry search, E2 geoprocessing search), golden trajectories (E3), the current scorecard, and the live harness against ArcGIS Pro or FakeHost.
- **[Demos and fixtures](demos.md)**: the demo runner, the Pittsburgh showcase workflow, fixture generators, and the live probe scripts.
- **[Roadmap](https://github.com/nicoazel/ArcGISPro.MCP/blob/main/docs/ROADMAP.md)**: planned work, in priority order. Released changes are in the [changelog](https://github.com/nicoazel/ArcGISPro.MCP/blob/main/CHANGELOG.md).

## How a model uses the gateway

The gateway exposes only 16 tools. The full catalog of GIS operations stays server-side and reaches the model only when the model searches for it.

1. `system_get_state`: read the project snapshot and keep its **workspace revision**.
2. `registry_search` / `registry_browse`: find candidate operations by intent.
3. `registry_describe`: load the input schema and `resultSchema` for the few operations the task needs.
4. `registry_validate`: check arguments against the schema and the current workspace revision without changing anything. It does not resolve layers or paths. `registry_invoke` with `dryRun: true` goes further for operations with static validation, such as `gp.run`, without executing, needing a revision or consuming an approval.
5. `registry_invoke`: run the operation with the revision. Confirmation-gated operations (every destructive or external-side-effect operation, plus `project.open`, `project.save` and `feature.update`) first need `approval_request`, a person approving in the panel, and `approval_status` (which can wait for the decision with `waitSeconds`) to return a single-use token. Every tool returns `{ ok, result, error }` as structured content; failures set `isError`.
6. `resource_read`: fetch returned images and larger observations by `arcgis://` handle.

Reusable multi-step recipes go through `workflow_list` → `workflow_get` → `workflow_run`, and `skill_search` / `skill_get` supply the guidance for them. In default mode a workflow cannot run confirmation-gated steps, because there is no per-step approval yet. A workflow stops with `workspace_changed` if the project changes while it runs. The same state, descriptors, workflows, skills and observations are also exposed as read-only `arcgis://` MCP resources, and each skill and saved workflow is offered as an MCP prompt. See the [reference](reference.md) for every tool, operation, resource and prompt.
