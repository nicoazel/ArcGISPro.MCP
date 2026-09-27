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

The packager always runs `tools/verify-release.ps1` first. That performs the Release solution build, portable Core and Bridge tests, whitespace validation, add-in packaging, and exact add-in-content inspection. It then publishes the framework-dependent Windows x64 stdio server and creates:

```text
artifacts/releases/ArcGISProMCP-0.2.0-win-x64-development-preview.zip
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
      "command": "C:\\ArcGISProMCP\\0.2.0\\server\\arcgis-pro-mcp.exe"
    }
  }
}
```

Keep the dockpane open and review each approval request. Run the procedures in [manual acceptance](manual-acceptance.md) against a disposable project, including local review, reconnect, revision rejection, feature/metadata/geoprocessing mutations, and host shutdown. Portable verification is not host acceptance.

ArcPy is absent from the operation registry unless explicitly enabled before ArcGIS Pro starts. Follow [ArcPy configuration and trust boundaries](arcpy.md); do not enable it on a workstation that accepts untrusted scripts or untrusted MCP clients.

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

A live acceptance pass of the ArcGIS-only build was run on the maintainer's workstation on 2026-09-09. It covered MCP protocol and reconnect, multi-instance discovery, feature editing, layer metadata, SDK geoprocessing, the optional ArcPy runner (in autonomous mode), the urban layout workflows and the Pittsburgh showcase, and idle/shutdown behavior. Two findings are retained:

- Explicit remote cancellation of an accepted SDK geoprocessing call is not implemented. A client disconnect after acceptance is reported as `outcome_unknown`.
- ArcGIS Pro shutdown was not perfectly repeatable across disposable instances; one instance needed a PID-scoped forced close after 75 seconds.

The evidence from that pass (result JSON, hashes and images under `artifacts/`) is **local only**: `artifacts/` is git-ignored and nothing in this repository lets a reader verify those results. Treat them as the maintainer's notes, not as release evidence. Committed evidence goes in [`docs/acceptance/`](acceptance/README.md). Each entry is a `<yyyy-MM-dd>-<sha7>` folder written by `tools/run-acceptance.ps1 -Commit`, with a manifest tied to the commit SHA, ArcGIS Pro version, package hash and loaded DLL hashes, plus a summary and `SHA256SUMS`. A Core test validates every entry. Only the maintainer with a live ArcGIS Pro produces entries. An entry covers only the commit, Pro version and sections it names, and sections run in autonomous mode are marked as such. Until an entry exists for the build you install, and for anything an entry does not cover, run [manual acceptance](manual-acceptance.md) yourself.

## Known limits

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

## Release boundaries

- The packaging script does not install, sign, or upload anything.
- Live ArcGIS-host acceptance of the exact installed build, including the dockpane approval flow, is required before relying on a build.
- Anyone who enables autonomous mode anyway must also verify the visible autonomous warning and the autonomous-mode audit notices.

See [security](security.md) for the transport and approval model.
