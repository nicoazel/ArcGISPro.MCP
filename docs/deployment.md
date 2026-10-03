# Deployment, status and rollback

## Status

**Development preview.** Supported: interactive same-user workstation with dockpane approvals. Autonomous mode is an opt-in expert setting, not recommended.

- Target: ArcGIS Pro 3.7.1 on Windows x64, one signed-in user, a model client running as that same user.
- The add-in and bundle are unsigned. This is not a signed public release and must not be deployed as an unattended or organization-wide service.
- By default, confirmation-gated operations (destructive and external-side-effect operations, plus `project.open`, `project.save` and `feature.update`) require a short-lived approval issued by a person in the ArcGIS Pro dockpane, and the gateway cannot approve its own request.
- `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true` bypasses dockpane review for the whole host session and effectively grants the connected client the user's ArcGIS authority. It exists for trusted experimentation and is not a supported deployment mode. See [security](security.md).

## Build the bundle

Prerequisites are Windows x64, .NET SDK 10, a licensed ArcGIS Pro 3.7.1 installation, and access to the referenced ArcGIS Pro 3.7 SDK package.

From the repository root:

```powershell
./tools/package-release.ps1
```

The packager always runs `tools/verify-release.ps1` first. That performs the Release solution build, every portable test project under `tests/`, whitespace validation, add-in packaging, and exact add-in-content inspection. It then publishes the framework-dependent Windows x64 stdio server and creates:

```text
artifacts/releases/ArcGISProMCP-0.3.0-win-x64-development-preview.zip
```

The archive has one versioned root directory containing:

```text
ArcGISProMCP.AddIn.esriAddinX
server/arcgis-pro-mcp.exe
server/skills/*.skill.json
docs/*.md
workflows/*.workflow.json
README.md
LICENSE
release.json
checksums.sha256
```

The server is framework-dependent and requires the .NET 10 x64 runtime on the target workstation. Bundled skills load from `server/skills` relative to the executable, so keep that directory with the executable.

Packaging also launches the actual published executable for an offline MCP initialize, 16-tool discovery, and bundled skill-read smoke test. It does not report live state, registry search, images or local approval as tested when ArcGIS Pro is not connected.

## Verify before installation

Extract the zip into a new version-specific directory. From the extracted version root, verify every listed payload before installing or configuring anything:

```powershell
$failures = foreach ($line in Get-Content -LiteralPath ./checksums.sha256) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "Malformed checksum line: $line" }
    $expected = $Matches[1]
    $path = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { $path }
}
if ($failures) { throw "Checksum failure: $($failures -join ', ')" }
```

Inspect `release.json` too. It records compatibility, framework dependency, unsigned status, and manual acceptance gates. Checksums detect accidental corruption; they do not establish publisher trust.

## Install and configure

1. Keep the previously accepted bundle and add-in package for rollback.
2. Close ArcGIS Pro. Do not replace an add-in while its assemblies are loaded.
3. Install `ArcGISProMCP.AddIn.esriAddinX` with the normal ArcGIS Pro add-in installation flow. Organization policy may prohibit unsigned add-ins.
4. Start ArcGIS Pro, open MCP Studio, and confirm the expected pipe and project before connecting a model client.
5. Configure the MCP client to launch the extracted absolute path to `server\arcgis-pro-mcp.exe`. A single discovered Pro host is selected automatically. With several hosts, set `ARCGIS_PRO_MCP_HOST_PID` for the gateway process. `ARCGIS_PRO_MCP_PIPE` remains an explicit override and must match host and gateway.

New hosts default to a unique `ArcGISProMCP.v1.<pid>` pipe and publish discovery records under LocalAppData. The gateway refuses ambiguous automatic selection and lists PID/project choices. Do not deliberately configure multiple hosts with the same explicit pipe: Windows can distribute successive client connections across different projects, making state and revision checks appear inconsistent.

A generic MCP client entry is:

```json
{
  "mcpServers": {
    "arcgis-pro": {
      "command": "C:\\ArcGISProMCP\\0.3.0\\server\\arcgis-pro-mcp.exe"
    }
  }
}
```

Keep the dockpane open and review each approval request. Run the procedures in [manual acceptance](manual-acceptance.md) against a disposable project, including local review, reconnect, revision rejection, feature/metadata/geoprocessing mutations, and host shutdown. Portable verification is not host acceptance.

ArcPy is absent from the operation registry unless explicitly enabled before ArcGIS Pro starts. Follow [ArcPy configuration and trust boundaries](arcpy.md); do not enable it on a workstation that accepts untrusted scripts or untrusted MCP clients.

### Diagnostics

`ARCGIS_PRO_MCP_REVISION_LOG=1` (or `true`), set for the ArcGIS Pro process before it starts, turns on the workspace revision log. It is off by default. Each ArcGIS Pro process writes its own file, `%LOCALAPPDATA%\ArcGISProMCP\diagnostics\revisions-<pid>.log`, with tab-separated, UTC-timestamped lines:

- every new workspace revision with the state it was computed from: the MCP write sequence, the project URI (its full local path), project name and dirty flag, and each map's and layout's name, handle, layer count and map frame count;
- `advance` and `ignored` lines naming the ArcGIS event (or `operation`) behind each revision advance, or the event deliberately ignored;
- one line per post-write settle, `settle-ok`, `settle-unsettled` (the samples never repeated the revision) or `settle-timeout` (the 3 s budget ran out and a plain snapshot was published), with `drainMs` (host drain plus the 300 ms host-event quiet wait), `sampleMs` (revision sampling after the drain), `samples` and the published `revision`.

`tools/analyze-revision-log.py` (Python 3.11, standard library only) reads a revision log and attributes each settle to the innermost audited operation whose `startedAt`..`completedAt` interval contains it. Per operation id it prints the settle count, budget-cap (timeout) rate, unsettled count and p50/p95 of drain, sample and total milliseconds, then the same over all settles:

```powershell
python tools/analyze-revision-log.py revisions-1234.log --audit "$env:LOCALAPPDATA\ArcGISProMCP\audit\operations.jsonl"
python tools/analyze-revision-log.py revisions-1234.log --audit operations.jsonl --json      # machine-readable report
python tools/analyze-revision-log.py revisions-1234.log --audit operations.jsonl --slack-ms 500
```

`--slack-ms` widens each operation interval when matching (default 250 ms); without `--audit` every settle is reported as unmatched. Settle lines from an older add-in without timing are skipped and counted on stderr.

The log contains project paths and map and layout names, so treat it like the project itself when sharing it. Writing stops once the file reaches 50 MB (a final `capped` line says so); delete the file to start again. Nothing rotates or deletes it automatically.

## Implemented surface

- Searchable registry with curated project, map, scene, layer, cartography, feature, table, metadata, geoprocessing, layout, observation, workflow and optional ArcPy operations. See the [reference](reference.md).
- Geoprocessing discovery (`gp.search`, `gp.describe`) from installed toolbox metadata, allowlisted read-only `gp.query`, per-tool risk tiers with an autonomous-mode refusal for Destructive and UserCode tools, and static `gp.run` dry runs.
- Typed MCP results (`{ ok, result, error }` structured content, output schemas, `isError`), explicit tool annotations, `arcgis://` resources and skill/workflow prompts.
- Typed point, single-part polyline and single-part polygon feature CRUD with GlobalID-first addressing, bounded queries, spatial filters, selection and revision checks.
- Metadata read/update that preserves unrelated ArcGIS XML, plus scene elevation placement metadata.
- Live legends, north arrows, scale bars and dynamic project/date/map-frame-scale text.
- Per-PID host discovery with fail-closed ambiguity for concurrent ArcGIS Pro projects.
- Dockpane local review with single-use approval tokens bound to operation version, arguments and workspace revision, with a "Runs user code" warning on requests that execute Python.
- Workflow operation allowlists: user-code operations (`gp.run`, `arcpy.run-script`) must be listed explicitly in a workflow's `allowedOperations`.
- Audit log of operations, unknown operation ids, autonomous bypasses and approval decisions, rotated at 16 MiB (five rotated files kept).

## Acceptance evidence

**Committed evidence:** [`docs/acceptance/2026-10-03-e7deee2`](acceptance/2026-10-03-e7deee2/summary.md) (the latest; earlier: [`2026-09-29-96f6a5b`](acceptance/2026-09-29-96f6a5b/summary.md) and [`2026-09-28-5f34f16`](acceptance/2026-09-28-5f34f16/summary.md)) records commit `e7deee2` on ArcGIS Pro 3.7.1 (3.7.1.1904; registry 3.7.0): release verify, the loaded add-in DLLs matching the package, host probe, the MCP smoke test, the three bundled urban workflows x 3 runs with layouts visually inspected, and the live operation matrix (41/41 operations, 90/90 cases, 7 approved and 1 denied card), all in default (review-required) mode. Its approval cards were decided by Claude through computer use at the maintainer's direction, not by a person reviewing them ([details](acceptance/README.md)). It does not cover autonomous mode or the separate feature/GP/ArcPy section.

A live acceptance pass of the ArcGIS-only build was run on the maintainer's workstation on 2026-09-09. It covered MCP protocol and reconnect, multi-instance discovery, feature editing, layer metadata, SDK geoprocessing, the optional ArcPy runner (in autonomous mode), the urban layout workflows and the Pittsburgh showcase, and idle/shutdown behavior. Two findings are retained:

- Explicit remote cancellation of an accepted SDK geoprocessing call is not implemented. A client disconnect after acceptance is reported as `outcome_unknown`.
- ArcGIS Pro shutdown was not perfectly repeatable across disposable instances; one instance needed a PID-scoped forced close after 75 seconds.

The evidence from that pass (result JSON, hashes and images under `artifacts/`) is **local only**: `artifacts/` is git-ignored and nothing in this repository lets a reader verify those results. Treat them as the maintainer's notes, not as release evidence. Committed evidence goes in [`docs/acceptance/`](acceptance/README.md). Each entry is a `<yyyy-MM-dd>-<sha7>` folder written by `tools/run-acceptance.ps1 -Commit`, with a manifest tied to the commit SHA, ArcGIS Pro version, package hash and loaded DLL hashes, plus a summary and `SHA256SUMS`. A Core test validates every entry. Only the maintainer with a live ArcGIS Pro produces entries. An entry covers only the commit, Pro version and sections it names, and sections run in autonomous mode are marked as such. Until an entry exists for the build you install, and for anything an entry does not cover, run [manual acceptance](manual-acceptance.md) yourself.

## Known limits

- The post-write settle budget is 3 s. It bounds the host's own waiting: the drain of the main CIM thread and UI dispatcher, the 300 ms host-event quiet wait and the quiet revision samples. It does not bound how long a write takes to return: every snapshot, including the plain one published when the budget runs out, is read on ArcGIS Pro's main CIM thread, so while Pro is busy (for example loading a newly added layer) the write waits for it. One write after `layer.add` took about 30 s in a live run. In the 2026-09-29 evidence run, before the host-event quiet wait existed, the operator's revision log (`ARCGIS_PRO_MCP_REVISION_LOG`, not committed) recorded 26 of 324 writes (8%, heavy layout and 3D steps) reaching the cap because ArcGIS Pro did not go idle within 3 s. No revision drift followed in that run, but a late ArcGIS event echo after a capped settle can still surface as `workspace_changed` on the next workflow step. Rerun the workflow after refreshing state. Measure settles on your own workstation with the revision log and `tools/analyze-revision-log.py` (see [diagnostics](#diagnostics)).
- **Workflows cannot execute confirmation-gated steps in default mode.** `workflow_run` invokes each step without an approval token, and there is no per-step approval yet. A step such as `gp.run`, `metadata.update`, `feature.update`, `feature.delete`, `project.save` or `arcpy.run-script` fails with `confirmation_required` unless the host runs in autonomous mode. Run such operations individually through `approval_request` and `registry_invoke`.
- Workflows are not transactional or resumable after a crash. There is no automatic rollback. A run that detects a mid-run workspace change stops with `workspace_changed` and leaves its completed steps in place.
- Feature editing excludes batch edits, multipoint construction, multipart construction and complete subtype/domain/range validation.
- Metadata update targets a map layer's ArcGIS metadata API; standalone catalog-item metadata editing is not supported.
- Once an SDK geoprocessing write is accepted, the protocol has no durable job id or explicit remote cancel command. A disconnected caller receives `outcome_unknown` and must inspect state or repeat the call with the same idempotency key.
- Process-lifetime idempotency, workflow history and resources are not a durable cross-restart job store.
- Advanced renderers, workspace connection management and PDF export are outside the curated registry. The registry is not a complete ArcGIS SDK wrapper.
- ArcPy and `gp.run` can execute arbitrary user code; neither is a sandbox. See [security](security.md).
- The package is unsigned. Wider distribution requires an organizational signing certificate, publisher policy and another installed-package verification pass over the signed artifact.
- Automation that closes ArcGIS Pro must keep PID-scoped ownership, save first, request a normal close, wait, and only terminate a verified disposable process as a last resort.

## Roll back

1. Close ArcGIS Pro and stop the MCP client.
2. Preserve logs, the failing bundle, and any ArcGIS crash report or dump.
3. Remove the preview add-in with the normal ArcGIS Pro add-in flow.
4. Reinstall the previously accepted add-in and point the client to that version's server executable.
5. Restart ArcGIS Pro and repeat acceptance against a disposable project.

Rollback does not undo map, geodatabase, layout, metadata, geoprocessing, or ArcPy mutations. Restore user data from its own backup/version history, and do not retry an uncertain mutation without checking its recorded outcome.

## MCP registry package

**Draft, not published.** The gateway can also be packed as a NuGet `McpServer` dotnet tool for the [MCP registry](https://github.com/modelcontextprotocol/registry) (`registryType: nuget`, `runtimeHint: dnx`). The zip bundle above stays the primary distribution: the NuGet package contains only the gateway, so the ArcGIS Pro add-in must still be installed from the GitHub release, and the gateway is Windows-only in practice.

| Item | Value |
| --- | --- |
| Registry name | `io.github.nicoazel/arcgis-pro-mcp` |
| NuGet package id | `ArcGISProMCP.Gateway` |
| Tool command | `arcgis-pro-mcp` |
| Registry metadata | `src/ArcGISProMCP.Server/.mcp/server.json`, packed at `/.mcp/server.json` |

Build and verify the package locally:

```powershell
./tools/pack-gateway.ps1
```

This runs `dotnet pack` into `artifacts/packages/ArcGISProMCP.Gateway.<version>.nupkg` and fails unless the package declares the `McpServer` package type, contains `.mcp/server.json` identical to source, exposes the `arcgis-pro-mcp` command, and bundles every `skills/*.skill.json` byte-for-byte under the tool's `skills` directory. It also fails if either version in `server.json` differs from `Directory.Build.props`, so bump them together (see [release process](#release-process)). The package is framework-dependent and RID-agnostic (`tools/net10.0/any`) and needs the .NET 10 runtime; `PublishSingleFile` only affects `dotnet publish`, so `package-release.ps1` still produces the single-file executable.

Try the package without publishing it:

```powershell
dotnet tool install --tool-path "$env:TEMP\arcgis-pro-mcp-tool" --add-source artifacts/packages ArcGISProMCP.Gateway --version 0.3.0
# or, with .NET 10 (dnx asks before it downloads and runs the tool):
# dnx ArcGISProMCP.Gateway --version 0.3.0 --add-source artifacts/packages
```

### Environment variables in `server.json`

Registry clients set environment variables on the gateway process only. `server.json` therefore declares only the gateway-side variables; host-side variables must be set in the environment ArcGIS Pro starts in (see [reference](reference.md#environment-variables)).

| Variable | Side | In `server.json` |
| --- | --- | --- |
| `ARCGIS_PRO_MCP_HOST_PID` | Gateway | Yes, optional |
| `ARCGIS_PRO_MCP_PIPE` | Gateway and Pro (must match) | Yes, optional; set the same value for ArcGIS Pro |
| `ARCGIS_PRO_MCP_AUTONOMOUS_MODE` | Pro | No |
| `ARCGIS_PRO_MCP_ENABLE_ARCPY` and `ARCGIS_PRO_MCP_ARCPY_*` | Pro | No |

### Publishing (maintainer, later)

Not automated and not done by any script in this repository. It requires the maintainer's nuget.org account and GitHub identity:

1. Start from a released version (see [release process](#release-process)), then run `./tools/pack-gateway.ps1`.
2. Push the package: `dotnet nuget push artifacts/packages/ArcGISProMCP.Gateway.<version>.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json`, and wait for nuget.org validation and indexing.
3. Install the registry publisher (`mcp-publisher`, from the modelcontextprotocol/registry releases), run `mcp-publisher login github` as `nicoazel`, then run `mcp-publisher validate` and `mcp-publisher publish` from `src/ArcGISProMCP.Server/.mcp` (both read `server.json` from the current directory).
4. Confirm the entry at `https://registry.modelcontextprotocol.io/v0/servers?search=io.github.nicoazel/arcgis-pro-mcp`.

The registry verifies NuGet ownership by finding `mcp-name: io.github.nicoazel/arcgis-pro-mcp` in the package README. The packed README is the repository `README.md`, which carries `<!-- mcp-name: io.github.nicoazel/arcgis-pro-mcp -->` on its own line (an HTML comment, so it does not render). Keep that line when editing the README.

## Release process

The maintainer pushes a tag `vX.Y.Z`; `.github/workflows/release.yml` builds, tests and packages that commit and creates a **draft** GitHub release; the maintainer reviews the draft and publishes it. Nothing is published, signed or pushed to nuget.org or the MCP registry automatically.

1. On `main`, set the same plain `X.Y.Z` in `Version` in `Directory.Build.props`, both `version` fields in `src/ArcGISProMCP.Server/.mcp/server.json`, and the `AddInInfo` `version` in `src/ArcGISProMCP.AddIn/Config.daml`. In `CHANGELOG.md`, the release section is `## [X.Y.Z] - unreleased` until release day; then set the date (`## [X.Y.Z] - yyyy-MM-dd`), keep an empty `## [Unreleased]` above it, and update the compare links at the bottom.
2. Record live acceptance for the release commit with `tools/run-acceptance.ps1 -Commit` (see [acceptance evidence](#acceptance-evidence)) and merge it. The release notes link the latest committed evidence folder, which covers only the commit it names.
3. Push an annotated tag on that `main` commit: `git tag -a vX.Y.Z -m "vX.Y.Z"` and `git push origin vX.Y.Z`. For a release candidate, tag `vX.Y.Z-rc.N` with the same product version; the changelog heading may still say `unreleased`.
4. The workflow fails unless the tag is on `main`, the tag version equals all four version fields, and, for a final release, the changelog heading is dated. It then runs `tools/package-release.ps1` (release build, every test project, add-in inspection, offline MCP smoke test), writes `SHA256SUMS` for the bundle zip, extracts the notes with `tools/extract-release-notes.ps1`, and creates the draft release "ArcGIS Pro MCP Studio vX.Y.Z" with the zip and `SHA256SUMS` attached (marked as a prerelease for `-rc.N`).
5. Review the draft: the notes, the evidence link, and the assets. Download the zip, check it against `SHA256SUMS` and its inner `checksums.sha256` (see [verify before installation](#verify-before-installation)), then publish the draft by hand. If anything is wrong, delete the draft and the tag, fix `main`, and tag again.

Preview the notes locally with `./tools/extract-release-notes.ps1 -Version X.Y.Z -Ref vX.Y.Z -OutputPath artifacts/release-notes.md`.

## Release boundaries

- The packaging script does not install, sign, or upload anything. The release workflow only creates a draft release; publishing it is the maintainer's decision.
- Live ArcGIS-host acceptance of the exact installed build, including the dockpane approval flow, is required before relying on a build.
- Anyone who enables autonomous mode anyway must also verify the visible autonomous warning and the autonomous-mode audit notices.

See [security](security.md) for the transport and approval model.
