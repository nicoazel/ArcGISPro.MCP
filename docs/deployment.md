# Deployment, status and rollback

The first half of this page is for people installing the release: a GIS analyst who wants Claude Desktop, Claude Code, VS Code or another MCP client to work with ArcGIS Pro. The second half is for maintainers who build and publish releases.

**Contents**

- [Status](#status)
- [Install (users)](#install-users)
  - [Prerequisites](#prerequisites) · [1. Download and verify](#1-download-and-verify) · [2. Unblock and extract](#2-unblock-and-extract) · [3. Install the add-in](#3-install-the-add-in) · [4. Open the ArcGIS MCP pane](#4-open-the-arcgis-mcp-pane) · [5. Connect your MCP client](#5-connect-your-mcp-client) · [6. Verify](#6-verify)
  - [Troubleshooting](#troubleshooting) · [Upgrade](#upgrade) · [Uninstall](#uninstall) · [Roll back](#roll-back) · [More on configuration](#more-on-configuration) · [Diagnostics](#diagnostics) · [Implemented surface](#implemented-surface) · [Known limits](#known-limits)
- [Build and release (maintainers)](#build-and-release-maintainers)
  - [Build the bundle](#build-the-bundle) · [Verify before installation](#verify-before-installation) · [Acceptance evidence](#acceptance-evidence) · [MCP registry package](#mcp-registry-package) · [Release process](#release-process) · [Release boundaries](#release-boundaries)

## Status

**Development preview.** Supported: interactive same-user workstation with dockpane approvals. Autonomous mode is an opt-in expert setting, not recommended.

- Target: ArcGIS Pro 3.7 (tested on 3.7.1) on Windows x64, one signed-in user, a model client running as that same user.
- The add-in and bundle are unsigned. This is not a signed public release and must not be deployed as an unattended or organization-wide service.
- By default, confirmation-gated operations (destructive and external-side-effect operations, plus `project.open`, `project.save` and `feature.update`) require a short-lived approval issued by a person in the ArcGIS MCP pane, and the gateway cannot approve its own request.
- `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true` bypasses dockpane review for the whole host session and effectively grants the connected client the user's ArcGIS authority. It exists for trusted experimentation and is not a supported deployment mode. See [security](security.md).

## Install (users)

The release is one zip, `ArcGISProMCP-<version>-win-x64-development-preview.zip`. These steps use version 0.3.1 as the example; replace `0.3.1` with the version you downloaded. The same steps are in `INSTALL.txt` inside the zip.

### Prerequisites

- Windows x64 and a licensed **ArcGIS Pro 3.7** (tested on 3.7.1).
- The **.NET Runtime 10.x (x64)**. The gateway is framework-dependent, so it needs the runtime; the SDK is not needed. Download ".NET Runtime 10.x", Windows x64 installer, from <https://dotnet.microsoft.com/download/dotnet/10.0>. To check, run this in PowerShell and look for a line that starts with `Microsoft.NETCore.App 10.`:

  ```powershell
  dotnet --list-runtimes
  ```

- An MCP client: Claude Desktop, Claude Code, VS Code, or any other client that can start a stdio server.
- A **disposable copy** of an ArcGIS Pro project to try it on.

### 1. Download and verify

From [the latest release](https://github.com/nicoazel/ArcGISPro.MCP/releases/latest), download both `ArcGISProMCP-0.3.1-win-x64-development-preview.zip` and `SHA256SUMS` into the same folder. Before extracting, check the zip against `SHA256SUMS`. In PowerShell, in that folder:

```powershell
$zip = 'ArcGISProMCP-0.3.1-win-x64-development-preview.zip'
$expected = (Select-String -Path .\SHA256SUMS -SimpleMatch $zip).Line.Split(' ')[0]
if ((Get-FileHash $zip -Algorithm SHA256).Hash -eq $expected) { 'OK' } else { 'MISMATCH - do not install' }
```

Anything other than `OK` means the download is damaged or not the published file: download it again. A checksum detects corruption; it does not prove who published the file.

### 2. Unblock and extract

Windows marks downloaded files as coming from the internet, and files extracted from a marked zip inherit the mark, which can make Windows or ArcGIS Pro block or warn about them. Unblock the zip **before** extracting it, then extract it into `C:\ArcGISProMCP\`. In the same PowerShell window:

```powershell
Unblock-File $zip
Expand-Archive $zip -DestinationPath C:\ArcGISProMCP
```

Or in Explorer: right-click the zip > **Properties** > tick **Unblock** > **OK**, then **Extract All** with the destination set to `C:\ArcGISProMCP` (remove the folder name Explorer suggests, or you get a nested folder).

The zip has one root folder, so you end up with:

```text
C:\ArcGISProMCP\ArcGISProMCP-0.3.1-win-x64-development-preview\
    INSTALL.txt
    ArcGISProMCP.AddIn.esriAddinX
    server\arcgis-pro-mcp.exe        <- the command your MCP client runs
    server\*.pdb                     (debug symbols)
    server\skills\*.skill.json
    docs\  workflows\  README.md  LICENSE  release.json  checksums.sha256
```

Keep `server\skills` next to `arcgis-pro-mcp.exe`. The rest of this page calls `C:\ArcGISProMCP\ArcGISProMCP-0.3.1-win-x64-development-preview\server\arcgis-pro-mcp.exe` the **gateway path**.

### 3. Install the add-in

1. Close ArcGIS Pro.
2. Double-click `ArcGISProMCP.AddIn.esriAddinX` in the extracted folder and click **Install Add-In**.
3. The add-in is unsigned. ArcGIS Pro loads unsigned add-ins only when its add-in security allows it: start ArcGIS Pro, go to **Project** > **Add-In Manager** > **Options**, choose **Load all Add-Ins without restrictions**, and restart ArcGIS Pro. If the options are greyed out, your organization manages this setting: ask your IT department.

### 4. Open the ArcGIS MCP pane

Start ArcGIS Pro and open a **disposable copy** of a project. On the **Add-In** tab, click **MCP Studio**. The **ArcGIS MCP** pane opens. Its status should read **Ready**, with the ArcGIS Pro process id (PID) and the workspace revision underneath.

The connection to MCP clients starts with ArcGIS Pro; the pane does not need to be open for it. Keep the pane open anyway: approval cards appear there, and a risky request waits until you approve or deny its card.

### 5. Connect your MCP client

Every client starts the gateway path as a stdio server. JSON needs every backslash doubled.

> **Do not set `ARCGIS_PRO_MCP_AUTONOMOUS_MODE`** in a client configuration or anywhere else: it skips the approval cards.

#### Claude Desktop

1. Open **Settings** > **Developer** > **Edit Config**. This opens `claude_desktop_config.json` (normally `%APPDATA%\Claude\claude_desktop_config.json`).
2. Add the `arcgis-pro` entry inside `mcpServers`. If the file already has an `mcpServers` block, add the entry next to the servers already there instead of adding a second block:

   ```json
   {
     "mcpServers": {
       "arcgis-pro": {
         "command": "C:\\ArcGISProMCP\\ArcGISProMCP-0.3.1-win-x64-development-preview\\server\\arcgis-pro-mcp.exe"
       }
     }
   }
   ```

3. Save the file, then quit Claude Desktop completely: right-click its icon in the Windows notification area (system tray) and choose **Quit**. Closing the window is not enough. Start Claude Desktop again.

#### Claude Code

```powershell
claude mcp add --scope user arcgis-pro -- "C:\ArcGISProMCP\ArcGISProMCP-0.3.1-win-x64-development-preview\server\arcgis-pro-mcp.exe"
claude mcp list
```

`--scope user` makes the server available in every project. `claude mcp list` should show `arcgis-pro` as connected; inside Claude Code, `/mcp` shows the same.

#### VS Code

Create `.vscode\mcp.json` in your workspace, or run **MCP: Open User Configuration** from the Command Palette to make it available in every workspace, and add:

```json
{
  "servers": {
    "arcgis-pro": {
      "type": "stdio",
      "command": "C:\\ArcGISProMCP\\ArcGISProMCP-0.3.1-win-x64-development-preview\\server\\arcgis-pro-mcp.exe"
    }
  }
}
```

Start the server when VS Code offers to, then use the tools from Chat in agent mode.

#### Any other MCP client

Configure a stdio server whose command is the gateway path, with no arguments. Settings are environment variables on that server (see the [reference](reference.md#environment-variables)); none is needed for a single ArcGIS Pro window.

#### Several ArcGIS Pro windows

With one ArcGIS Pro process running, the gateway finds it automatically. With several, it refuses to guess (`arcgis_host_ambiguous`). Pick one by its process id, which the ArcGIS MCP pane shows under its status, by adding an `env` block to the client entry:

```json
"arcgis-pro": {
  "command": "C:\\ArcGISProMCP\\ArcGISProMCP-0.3.1-win-x64-development-preview\\server\\arcgis-pro-mcp.exe",
  "env": { "ARCGIS_PRO_MCP_HOST_PID": "12345" }
}
```

In Claude Code, add `-e ARCGIS_PRO_MCP_HOST_PID=12345` after `--scope user`. A PID changes every time ArcGIS Pro starts, so update or remove it afterwards.

### 6. Verify

1. The ArcGIS MCP pane says **Ready**.
2. The client lists the `arcgis-pro` server with **16 tools** (Claude Code: `/mcp`; Claude Desktop: the tools menu in the message box; VS Code: the tools picker in Chat).
3. Ask: *"What project is open in ArcGIS Pro?"* The answer names your disposable project.
4. Ask for a small edit, such as updating one attribute. A card appears in the ArcGIS MCP pane; approve it and check the change in ArcGIS Pro.

### Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| The client says the server failed, or shows no ArcGIS tools | Check the command path: it must point to `server\arcgis-pro-mcp.exe` inside the `ArcGISProMCP-<version>-win-x64-development-preview` folder, with doubled backslashes in JSON. Run it once in PowerShell (`& "<gateway path>"`): if it reports that .NET is missing, install the runtime (next row); otherwise it waits silently for a client, which is correct (press Ctrl+C). Claude Desktop writes a log per server to `%APPDATA%\Claude\logs`. Restart the client completely after any change. |
| .NET runtime missing | `dotnet --list-runtimes` shows no `Microsoft.NETCore.App 10.` line, or `dotnet` is not found. Install ".NET Runtime 10.x (x64)" (the SDK is not needed) from <https://dotnet.microsoft.com/download/dotnet/10.0>, then restart the client. |
| No **MCP Studio** button on the **Add-In** tab | The add-in did not load. Check **Project** > **Add-In Manager**: if ArcGIS Pro MCP Studio is listed with a security message, allow unsigned add-ins (**Options** > **Load all Add-Ins without restrictions**, or ask IT if managed) and restart ArcGIS Pro. If it is not listed, install the `.esriAddinX` again with ArcGIS Pro closed. The add-in needs ArcGIS Pro 3.7. If you did not unblock the zip before extracting, unblock the `.esriAddinX` (Properties > Unblock) and install again. |
| `arcgis_unavailable` | No ArcGIS Pro accepted the connection: ArcGIS Pro is not running, it is still starting, or the add-in did not load. Start ArcGIS Pro, open a project, check that the pane says **Ready**, and ask again. Retryable. |
| `bridge_disabled` | Someone clicked **Disconnect** in the ArcGIS MCP pane. Click **Connect**. |
| `arcgis_host_ambiguous` | Several ArcGIS Pro processes are running. Close the extra ones, or set `ARCGIS_PRO_MCP_HOST_PID` in the client entry to the PID shown in the pane ([several ArcGIS Pro windows](#several-arcgis-pro-windows)). |
| `arcgis_host_not_found` | `ARCGIS_PRO_MCP_HOST_PID` names a process that is not a running ArcGIS Pro with the add-in, usually because ArcGIS Pro restarted with a new PID. Update the PID or remove the setting, then restart the client. |
| `arcgis_host_selector_invalid` | `ARCGIS_PRO_MCP_HOST_PID` is not a positive whole number. Fix or remove it. |
| A risky request waits and nothing happens | It is waiting for its card. Open the ArcGIS MCP pane (**Add-In** > **MCP Studio**) and approve or deny it. A card expires after two minutes; the client can then request a new one. |
| `workspace_revision_mismatch` or `workspace_changed` | The project changed since the client last read it (for example you edited it in ArcGIS Pro). Ask the client to read the state again and retry. |

Other error codes are in the [reference](reference.md#error-codes).

### Upgrade

1. Close ArcGIS Pro and quit the MCP client.
2. Download, verify, unblock and extract the new zip into `C:\ArcGISProMCP\` ([steps 1 and 2](#1-download-and-verify)). Each version has its own folder, so the old one stays for [rollback](#roll-back).
3. Double-click the new `ArcGISProMCP.AddIn.esriAddinX`. It has the same add-in id, so it replaces the installed version.
4. Change the client's command to the new gateway path (Claude Code: `claude mcp remove --scope user arcgis-pro`, then add it again).
5. Start ArcGIS Pro, then restart the client completely, and [verify](#6-verify).

### Uninstall

1. In ArcGIS Pro: **Project** > **Add-In Manager**, select **ArcGIS Pro MCP Studio**, click **Delete this Add-In**, and restart ArcGIS Pro.
2. Remove the client entry: delete `arcgis-pro` from `claude_desktop_config.json` or `mcp.json`, or run `claude mcp remove --scope user arcgis-pro`. Restart the client.
3. Delete `C:\ArcGISProMCP\`.
4. Optional: `%LOCALAPPDATA%\ArcGISProMCP` holds the audit log, saved workflows, captured images and discovery records. Delete it if you do not need them.

### Roll back

1. Close ArcGIS Pro and stop the MCP client.
2. Preserve logs, the failing bundle, and any ArcGIS crash report or dump.
3. Remove the preview add-in with the normal ArcGIS Pro add-in flow.
4. Reinstall the previously accepted add-in and point the client to that version's server executable.
5. Restart ArcGIS Pro and repeat acceptance against a disposable project.

Rollback does not undo map, geodatabase, layout, metadata, geoprocessing, or ArcPy mutations. Restore user data from its own backup/version history, and do not retry an uncertain mutation without checking its recorded outcome.

### More on configuration

New hosts default to a unique `ArcGISProMCP.v1.<pid>` pipe and publish discovery records under `%LOCALAPPDATA%`. `ARCGIS_PRO_MCP_PIPE` is an explicit override and must match host and gateway. Do not deliberately configure multiple hosts with the same explicit pipe: Windows can distribute successive client connections across different projects, making state and revision checks appear inconsistent.

Run the procedures in [manual acceptance](manual-acceptance.md) against a disposable project, including local review, reconnect, revision rejection, feature/metadata/geoprocessing mutations, and host shutdown, before relying on a build.

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

### Implemented surface

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

### Known limits

- **Post-write settle budget (3 s).** After each write the host waits for ArcGIS Pro to go quiet: the drain of the main CIM thread and UI dispatcher, the 300 ms host-event quiet wait and the quiet revision samples, at most 3 s in total.
  - The budget does not bound how long a write takes to return. Every snapshot, including the plain one published when the budget runs out, is read on ArcGIS Pro's main CIM thread, so while Pro is busy (for example loading a newly added layer) the write waits for it. One write after `layer.add` took about 30 s in a live run.
  - In the 2026-09-29 evidence run, before the host-event quiet wait existed, the operator's revision log recorded 26 of 324 writes (8%, heavy layout and 3D steps) reaching the cap because ArcGIS Pro did not go idle within 3 s. No revision drift followed in that run.
  - A late ArcGIS event echo after a capped settle can still surface as `workspace_changed` on the next workflow step. Rerun the workflow after refreshing state. Measure settles on your own workstation with the revision log and `tools/analyze-revision-log.py` (see [diagnostics](#diagnostics)).
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

## Build and release (maintainers)

### Build the bundle

Prerequisites are Windows x64, .NET SDK 10, a licensed ArcGIS Pro 3.7 installation (tested on 3.7.1), and access to the referenced ArcGIS Pro 3.7 SDK package.

From the repository root:

```powershell
./tools/package-release.ps1
```

The packager always runs `tools/verify-release.ps1` first. That performs the Release solution build, every portable test project under `tests/`, whitespace validation, add-in packaging, and exact add-in-content inspection. It then publishes the framework-dependent Windows x64 stdio server and creates:

```text
artifacts/releases/ArcGISProMCP-<version>-win-x64-development-preview.zip
```

The archive has one versioned root directory, `ArcGISProMCP-<version>-win-x64-development-preview/`, containing:

```text
INSTALL.txt
ArcGISProMCP.AddIn.esriAddinX
server/arcgis-pro-mcp.exe
server/*.pdb
server/skills/*.skill.json
docs/*.md
workflows/*.workflow.json
README.md
LICENSE
release.json
checksums.sha256
```

`INSTALL.txt` is generated from `tools/bundle/INSTALL.txt` with the version filled in and CRLF line endings; keep its steps in line with [Install (users)](#install-users). The server is framework-dependent and requires the .NET 10 x64 runtime on the target workstation. Bundled skills load from `server/skills` relative to the executable, so keep that directory with the executable.

Packaging also launches the actual published executable for an offline MCP initialize, 16-tool discovery, and bundled skill-read smoke test. It does not report live state, registry search, images or local approval as tested when ArcGIS Pro is not connected.

### Verify before installation

Besides the release `SHA256SUMS` for the zip ([step 1](#1-download-and-verify)), every bundle carries `checksums.sha256` for its contents. From the extracted version root (for example `C:\ArcGISProMCP\ArcGISProMCP-0.3.1-win-x64-development-preview`), verify every listed payload:

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

### Acceptance evidence

**Committed evidence:** [`docs/acceptance/2026-10-03-e7deee2`](acceptance/2026-10-03-e7deee2/summary.md) is the latest. It records commit `e7deee2` (private-history SHA; public commit `c976c51`, see [Public history](acceptance/README.md#public-history)) on ArcGIS Pro 3.7.1 (3.7.1.1904; registry 3.7.0), all in default (review-required) mode:

- release verify, the loaded add-in DLLs matching the package, host probe and the MCP smoke test;
- the three bundled urban workflows x 3 runs, with layouts visually inspected;
- the live operation matrix: 41/41 operations, 90/90 cases, 7 approved and 1 denied card.

Its approval cards were decided by Claude through computer use at the maintainer's direction, not by a person reviewing them ([details](acceptance/README.md#run-notes)). It does not cover autonomous mode or the separate feature/GP/ArcPy section. The two earlier runs used the previous third-party test data and are kept in the private development archive only.

Each entry is a `<yyyy-MM-dd>-<sha7>` folder written by `tools/run-acceptance.ps1 -Commit`, with a manifest tied to the commit SHA, ArcGIS Pro version, package hash and loaded DLL hashes, plus a summary and `SHA256SUMS`. A Core test validates every entry.

Only the maintainer with a live ArcGIS Pro produces entries. An entry covers only the commit, Pro version and sections it names, and sections run in autonomous mode are marked as such. Until an entry exists for the build you install, and for anything an entry does not cover, run [manual acceptance](manual-acceptance.md) yourself. Earlier live passes that are not committed evidence are listed in [acceptance/README.md](acceptance/README.md#earlier-live-passes-not-evidence).

### MCP registry package

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
dotnet tool install --tool-path "$env:TEMP\arcgis-pro-mcp-tool" --add-source artifacts/packages ArcGISProMCP.Gateway --version 0.3.1
# or, with .NET 10 (dnx asks before it downloads and runs the tool):
# dnx ArcGISProMCP.Gateway --version 0.3.1 --add-source artifacts/packages
```

#### Environment variables in `server.json`

Registry clients set environment variables on the gateway process only. `server.json` therefore declares only the gateway-side variables; host-side variables must be set in the environment ArcGIS Pro starts in (see [reference](reference.md#environment-variables)).

| Variable | Side | In `server.json` |
| --- | --- | --- |
| `ARCGIS_PRO_MCP_HOST_PID` | Gateway | Yes, optional |
| `ARCGIS_PRO_MCP_PIPE` | Gateway and Pro (must match) | Yes, optional; set the same value for ArcGIS Pro |
| `ARCGIS_PRO_MCP_AUTONOMOUS_MODE` | Pro | No |
| `ARCGIS_PRO_MCP_ENABLE_ARCPY` and `ARCGIS_PRO_MCP_ARCPY_*` | Pro | No |

#### Publishing (maintainer, later)

Not automated and not done by any script in this repository. It requires the maintainer's nuget.org account and GitHub identity:

1. Start from a released version (see [release process](#release-process)), then run `./tools/pack-gateway.ps1`.
2. Push the package: `dotnet nuget push artifacts/packages/ArcGISProMCP.Gateway.<version>.nupkg --api-key <key> --source https://api.nuget.org/v3/index.json`, and wait for nuget.org validation and indexing.
3. Install the registry publisher (`mcp-publisher`, from the modelcontextprotocol/registry releases), run `mcp-publisher login github` as `nicoazel`, then run `mcp-publisher validate` and `mcp-publisher publish` from `src/ArcGISProMCP.Server/.mcp` (both read `server.json` from the current directory).
4. Confirm the entry at `https://registry.modelcontextprotocol.io/v0/servers?search=io.github.nicoazel/arcgis-pro-mcp`.

The registry verifies NuGet ownership by finding `mcp-name: io.github.nicoazel/arcgis-pro-mcp` in the package README. The packed README is the repository `README.md`, which carries `<!-- mcp-name: io.github.nicoazel/arcgis-pro-mcp -->` on its own line (an HTML comment, so it does not render). Keep that line when editing the README.

### Release process

The maintainer pushes a tag `vX.Y.Z`; `.github/workflows/release.yml` builds, tests and packages that commit and creates a **draft** GitHub release; the maintainer reviews the draft and publishes it. Nothing is published, signed or pushed to nuget.org or the MCP registry automatically.

1. On `main`, set the same plain `X.Y.Z` in `Version` in `Directory.Build.props`, both `version` fields in `src/ArcGISProMCP.Server/.mcp/server.json`, and the `AddInInfo` `version` in `src/ArcGISProMCP.AddIn/Config.daml`. In `CHANGELOG.md`, the release section is `## [X.Y.Z] - unreleased` until release day; then set the date (`## [X.Y.Z] - yyyy-MM-dd`), keep an empty `## [Unreleased]` above it, and update the compare links at the bottom.
2. Record live acceptance for the release commit with `tools/run-acceptance.ps1 -Commit` (see [acceptance evidence](#acceptance-evidence)) and merge it. The release notes link the latest committed evidence folder, which covers only the commit it names.
3. Push an annotated tag on that `main` commit: `git tag -a vX.Y.Z -m "vX.Y.Z"` and `git push origin vX.Y.Z`. For a release candidate, tag `vX.Y.Z-rc.N` with the same product version; the changelog heading may still say `unreleased`.
4. The workflow fails unless the tag is on `main`, the tag version equals all four version fields, and, for a final release, the changelog heading is dated. It then runs `tools/package-release.ps1` (release build, every test project, add-in inspection, offline MCP smoke test), writes `SHA256SUMS` for the bundle zip, extracts the notes with `tools/extract-release-notes.ps1`, and creates the draft release "ArcGIS Pro MCP Studio vX.Y.Z" with the zip and `SHA256SUMS` attached (marked as a prerelease for `-rc.N`).
5. Review the draft: the notes, the evidence link, and the assets. Download the zip, check it against `SHA256SUMS` and its inner `checksums.sha256` (see [verify before installation](#verify-before-installation)), then publish the draft by hand. If anything is wrong, delete the draft and the tag, fix `main`, and tag again.

Preview the notes locally with `./tools/extract-release-notes.ps1 -Version X.Y.Z -Ref vX.Y.Z -OutputPath artifacts/release-notes.md`.

### Release boundaries

- The packaging script does not install, sign, or upload anything. The release workflow only creates a draft release; publishing it is the maintainer's decision.
- Live ArcGIS-host acceptance of the exact installed build, including the dockpane approval flow, is required before relying on a build.
- Anyone who enables autonomous mode anyway must also verify the visible autonomous warning and the autonomous-mode audit notices.

See [security](security.md) for the transport and approval model.
