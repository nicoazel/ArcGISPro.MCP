# ArcGIS Pro MCP Studio

[![Build and test](https://github.com/nicoazel/ArcGISPro.MCP/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/nicoazel/ArcGISPro.MCP/actions/workflows/build.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![Status: development preview](https://img.shields.io/badge/status-development%20preview-orange.svg)](#arcgis-pro-mcp-studio)

<!-- mcp-name: io.github.nicoazel/arcgis-pro-mcp -->

An ArcGIS Pro 3.7 add-in and a small stdio MCP gateway that let an MCP client (Claude Desktop, Claude Code or any other) work inside the ArcGIS Pro session you already have open. The model sees 16 gateway tools, searches a registry of 41 typed, schema-validated operations and the 2,200+ installed geoprocessing tools, and invokes them by stable id. Every write is checked against the current workspace revision, every risky write waits for a person to approve those exact arguments in an ArcGIS Pro dockpane, and every invocation is audited.

**Status: development preview (0.3.0, unsigned).** Supported: an interactive, same-user Windows workstation with dockpane approvals. Autonomous mode is an opt-in expert setting and is not recommended. The latest live acceptance evidence, [`docs/acceptance/2026-09-29-96f6a5b`](docs/acceptance/2026-09-29-96f6a5b/summary.md), covers default mode on ArcGIS Pro 3.7.1; see [Verification](#verification-and-acceptance-evidence) for what it does and does not cover.

## Why this design

Most ArcGIS MCP servers take one of two approaches. This project trades some breadth for control over what runs in the user's live session.

| | This project | arcpy-subprocess MCP servers | Generic code-execution servers |
| --- | --- | --- | --- |
| Live Pro session state (open project, maps, selection, layouts) | Yes, in-process add-in | Usually no; works on files on disk | Only what the executed code can reach |
| Typed, schema-validated operations | 41 operations with input and result schemas | Per-tool, varies | No; the model writes code |
| Geoprocessing gateway | `gp.search` / `gp.describe` / `gp.run` over 2,200+ installed tools, with risk tiers | Usually a hand-picked subset | Anything, unclassified |
| Local-review approvals bound to exact arguments and revision | Yes, single-use tokens from the dockpane | Rarely | Rarely |
| Optimistic revision concurrency | Every write names the revision it read | No | No |
| Audit log | JSON lines: invocations, approvals, bypasses | Varies | Varies |
| MCP structured output, resources and prompts | Output schemas, `arcgis://` resources, prompts from skills and workflows | Varies | Varies |
| Evaluation suite | Retrieval evals, golden trajectories, live harness | Rare | Rare |

The rows for other approaches describe typical designs, not any particular project.

## Architecture

```mermaid
flowchart LR
    client["MCP client<br/>Claude Desktop / Claude Code"] -->|stdio| gateway["arcgis-pro-mcp.exe<br/>gateway, 16 tools"]
    gateway -->|"same-user named pipe<br/>per-PID discovery"| handler
    subgraph host["ArcGIS Pro add-in host"]
        handler["Bridge request handler"] --> registry["Operation registry<br/>41 operations"]
        registry --> executor["Executor<br/>schema, revision, approval, audit"]
        executor --- approvals["Approval queue"]
        approvals --- dockpane["MCP Studio dockpane<br/>a person approves"]
    end
    executor --> sdk["ArcGIS Pro SDK<br/>MCT / UI thread"]
    fake["FakeHost and test runtime<br/>same handler, fake ArcGIS services"] -.->|"tests and evals"| handler
```

The gateway holds no GIS logic; the add-in owns the registry, the executor and the approval queue, and only the dockpane can resolve an approval. FakeHost runs the add-in's real handler, registry and executor over fake ArcGIS services, so the end-to-end tests and evals exercise production code without ArcGIS Pro. Details: [architecture](docs/architecture.md).

## Quick start

Requirements: Windows x64, a licensed ArcGIS Pro 3.7.1, and the .NET 10 x64 runtime.

1. Get the bundle `ArcGISProMCP-<version>-win-x64-development-preview.zip` from GitHub Releases, or build it with `./tools/package-release.ps1` (see [deployment](docs/deployment.md#build-the-bundle)). Check `checksums.sha256`, then extract it, for example to `C:\ArcGISProMCP\0.3.0`.
2. Close ArcGIS Pro and install `ArcGISProMCP.AddIn.esriAddinX` (double-click it). The add-in is unsigned; your organization's add-in policy may block it.
3. Start ArcGIS Pro, open a **disposable copy** of a project, and open **Add-In > ArcGIS MCP > MCP Studio**.
4. Point your MCP client at the gateway. Claude Desktop (`claude_desktop_config.json`):

   ```json
   {
     "mcpServers": {
       "arcgis-pro": {
         "command": "C:\\ArcGISProMCP\\0.3.0\\server\\arcgis-pro-mcp.exe"
       }
     }
   }
   ```

   Claude Code:

   ```powershell
   claude mcp add arcgis-pro -- C:\ArcGISProMCP\0.3.0\server\arcgis-pro-mcp.exe
   ```

5. Try a first prompt: *"Read the ArcGIS Pro project state and list the layers in the active map."* Then ask for an edit, such as updating one attribute, and approve the card that appears in the dockpane.

With several ArcGIS Pro processes open, set `ARCGIS_PRO_MCP_HOST_PID` for the gateway; ambiguous selection fails closed. All settings are in the [reference](docs/reference.md#environment-variables).

## Security model

| Control | Behaviour |
| --- | --- |
| Risk tiers | ReadOnly runs freely. SafeWrite needs the current workspace revision. Destructive and ExternalSideEffect operations, plus `project.open`, `project.save` and `feature.update`, also need a local-review token. |
| Confirmation | `approval_request` queues a card in the dockpane; a person approves or denies it; `approval_status` returns a short-lived, single-use token bound to the operation, its version, the exact arguments and the revision. The gateway has no way to approve its own request. |
| `gp.run` tiers | Read-only allowlisted tools run through `gp.query` without review. Every `gp.run` is reviewed, and the card warns when a tool modifies input in place, consumes credits or runs user code (Python toolboxes, expressions). |
| Dry run | `registry_invoke` with `dryRun` validates statically (for `gp.run`, against toolbox metadata) and never executes or consumes a token. |
| Autonomous mode | `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true`, opt-in and **not recommended**: it bypasses review for most risky operations. It still refuses Destructive, UserCode and unclassified `gp.run` requests unless they carry a person-issued token; revisions, schemas and audit still apply. |
| Audit | Every invocation, approval decision and autonomous bypass is appended to `%LOCALAPPDATA%\ArcGISProMCP\audit\operations.jsonl` (rotated at 16 MiB). |
| Transport | Same-user named pipe per ArcGIS Pro process, with bounded connection slots and framing timeouts. |

`gp.run` can execute arbitrary Python through Python toolboxes, script tools and expressions, with your GIS authority. Read [security and limits](docs/security.md) before connecting a client you do not fully trust. Vulnerabilities: [SECURITY.md](SECURITY.md).

## Quality

Build: .NET 10 with `TreatWarningsAsErrors`; 0 warnings. Tests (xUnit v3), all passing at the time of writing:

| Project | Tests | What it covers |
| --- | ---: | --- |
| `ArcGISProMCP.Core.Tests` | 482 | Executor policy, approvals, schemas, search, toolbox catalog and risk tiers, workflows and their layout placement, audit, acceptance manifests and the operation matrix plan, synthetic test data |
| `ArcGISProMCP.Operations.Tests` | 223 | Operation behaviour over fake ArcGIS services, and a guard that every portable descriptor matches the add-in's |
| `ArcGISProMCP.Server.Tests` | 123 | MCP contract snapshots, envelopes against output schemas, end-to-end runs through the real bridge handler, E3 trajectories |
| `ArcGISProMCP.Bridge.Tests` | 67 | Pipe framing, discovery, host scheduling and the request handler |
| `ArcGISProMCP.Evals.Tests` | 19 | Retrieval suites gated on a measured baseline, scorecard writing, descriptor dump checks |
| **Total** | **914** | |

Eval scorecard ([evals/README.md](evals/README.md); gp suites measured against ArcGIS Pro 3.7.1, 2,210 system tools):

| Suite | Tasks | recall@5 | Held-out tasks | Held-out recall@5 |
| --- | ---: | ---: | ---: | ---: |
| Registry search (E1) | 30 | 0.867 | 16 | 0.938 |
| Geoprocessing search (E2) | 30 | 0.900 | 16 | 0.875 |
| Golden trajectories (E3) (from `TrajectoryTests` on every `dotnet test`; not a committed scorecard) | 8 / 8 passed; 4 of 8 exercise approval | schemaValidArgs 1.0 | approvalDiscipline 1.0 (over the 4) | taskSuccess 1.0 |

Held-out tasks were written before search tuning and never tuned against; they are the numbers to trust. The held-out gains come from matching changes (stemming, camel-case splitting, IDF, a core-toolbox prior); an ablation showed that search synonyms only moved the main suites' recall@5 (held-out recall@5 was the same with or without them), so synonyms fitted to single main-suite tasks were removed and the main-suite numbers above went down with them (see the [ablation table](evals/README.md#search-tuning-phase-4)). No live-model scorecard is committed yet; the live harness in `evals/live` runs by hand against ArcGIS Pro or FakeHost.

CI (GitHub Actions, pinned to commit SHAs, read-only permissions): a Windows build-and-test job over every test project (the installed-Pro gp suites are skipped there, the synthetic-toolbox subset runs), a packaging job that builds the preview bundle and fails if development-only hosts or test support reach it, and a lint job (PSScriptAnalyzer, ruff). Dependabot updates NuGet packages and Actions weekly.

## Verification and acceptance evidence

Portable tests are not host acceptance. The live acceptance tooling exists: `tools/run-acceptance.ps1` records the commit, ArcGIS Pro version and the hashes of the built and loaded add-in DLLs into `docs/acceptance/<date>-<sha7>/`, and a Core test validates every committed folder against the [evidence contract](docs/acceptance/README.md). The latest committed evidence is [`docs/acceptance/2026-09-29-96f6a5b`](docs/acceptance/2026-09-29-96f6a5b/summary.md): verify, the loaded-DLL match, host probe, the MCP smoke test and the three bundled urban workflows x 3 runs on ArcGIS Pro 3.7.1 (3.7.1.1904; registry 3.7.0) in default mode, with the layouts visually inspected (cosmetic legend/scale-bar overlaps are recorded). It does not cover autonomous mode or the feature/GP/ArcPy section.

The live operation matrix, `tools/run-live-operations.ps1` (the `operations` section of `run-acceptance.ps1`), exercises all 41 operations against a disposable project in default mode, with happy paths, negative cases and eight review cards that the operator approves or denies in the dockpane. Its first recorded run is pending the next live session; until then, the committed evidence above exercises only the operations the smoke test and the bundled workflows use.

<!-- LIVE-RESULTS: after the live session, replace the paragraph above with the operation matrix result from the new evidence folder (docs/acceptance/<date>-<sha7>/operations/summary.json): operations covered, cases passed/failed/skipped, cards approved/denied, and the link. -->

For anything an evidence folder does not cover, run the [manual acceptance](docs/manual-acceptance.md) checklist on your own installation and read the [known limits](docs/deployment.md#known-limits).

## Build from source

```powershell
dotnet build ArcGISPro.MCP.slnx -c Release
dotnet test ArcGISPro.MCP.slnx -c Release --no-build
./tools/pack-addin.ps1 -Configuration Release -Install   # restart ArcGIS Pro afterwards
./tools/package-release.ps1                              # the full CI gate and preview bundle
```

The build uses the `Esri.ArcGISPro.Extensions30` NuGet package, so it and the portable tests do not need ArcGIS Pro. To run an agent without ArcGIS Pro, use [FakeHost](evals/README.md#against-fakehost---host-fakehost).

## Documentation

| Topic | Read |
| --- | --- |
| Start here | [Documentation hub](docs/README.md) |
| Install, status, rollback, registry package | [Deployment](docs/deployment.md) |
| Processes, operation lifecycle, threading | [Architecture](docs/architecture.md) |
| Tools, operations, resources, prompts, errors, environment | [Reference](docs/reference.md) |
| Trust boundary, approvals, risk tiers, autonomous mode | [Security and limits](docs/security.md) · [ArcPy configuration](docs/arcpy.md) |
| Evidence and live checks | [Acceptance evidence](docs/acceptance/README.md) · [Manual acceptance](docs/manual-acceptance.md) |
| Evaluations | [evals/README.md](evals/README.md) |
| Demos, showcase and fixtures | [Demos](docs/demos.md) |
| Plans and history | [Roadmap](https://github.com/nicoazel/ArcGISPro.MCP/blob/main/docs/ROADMAP.md) · [Changelog](https://github.com/nicoazel/ArcGISPro.MCP/blob/main/CHANGELOG.md) |

## Contributing, security and license

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) and the [code of conduct](CODE_OF_CONDUCT.md). Report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). Licensed under [Apache-2.0](LICENSE). ArcGIS Pro and other Esri products require their own licenses.
