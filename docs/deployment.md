# Development preview deployment

ArcGIS Pro MCP Studio is a development preview for ArcGIS Pro 3.7.1 on Windows x64. The add-in is unsigned, and the newly separated ArcGIS-only build plus its feature, metadata, geoprocessing, and optional ArcPy surfaces require installation and live acceptance before distribution. Do not deploy this build as an unattended or organization-wide service.

By default, risky operations require a short-lived approval issued from the ArcGIS Pro panel, and the server cannot approve its own request. A trusted operator may explicitly start the host with `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true`; this bypasses panel review and materially expands client authority.

## Build the bundle

Prerequisites are Windows x64, .NET SDK 10, a licensed ArcGIS Pro 3.7.1 installation, and access to the referenced ArcGIS Pro 3.7 SDK package. Rhino and Rhino.Inside are not prerequisites.

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

Do not bypass the panel. Run the procedures in [manual acceptance](manual-acceptance.md) against a disposable project, including local review, reconnect, revision rejection, feature/metadata/geoprocessing mutations, and host shutdown. Portable verification is not host acceptance.

ArcPy is absent from the operation registry unless explicitly enabled before ArcGIS Pro starts. Follow [ArcPy configuration and trust boundaries](arcpy.md); do not enable it on a workstation that accepts untrusted scripts or untrusted MCP clients.

## Roll back

1. Close ArcGIS Pro and stop the MCP client.
2. Preserve logs, the failing bundle, and any ArcGIS crash report or dump.
3. Remove the preview add-in with the normal ArcGIS Pro add-in flow.
4. Reinstall the previously accepted add-in and point the client to that version's server executable.
5. Restart ArcGIS Pro and repeat acceptance against a disposable project.

Rollback does not undo map, geodatabase, layout, metadata, geoprocessing, or ArcPy mutations. Restore user data from its own backup/version history, and do not retry an uncertain mutation without checking its recorded outcome.

## Release boundaries

- The script does not install, sign, or upload anything.
- The operation registry is curated, not a complete ArcGIS SDK wrapper.
- Workflows are not transactional or resumable after a crash.
- Default-mode approval UI and live ArcGIS-host acceptance remain required. Autonomous deployments must instead verify the visible autonomous warning and unattended risky-operation audit notices.
- The latest-build installation, reload, mutation and stability tests block wider distribution.

See [production readiness](production-readiness.md) for evidence and remaining gates, and [security](security.md) for the transport and approval model.
