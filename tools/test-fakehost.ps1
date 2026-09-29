# FakeHost smoke test: starts tools/ArcGISProMCP.FakeHost on the real named pipe (with its discovery
# record), then the gateway over stdio pointed at it, and runs a short scripted MCP session:
# initialize, system_get_state, registry_search and an approved feature.delete. It also checks that
# the discovery record is marked as a FakeHost and that a gateway with no host selector does NOT
# attach to it. Every process is stopped before the script returns. Runs in Windows PowerShell 5.1
# and PowerShell 7.
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$ServerPath,
    [string]$FakeHostPath,
    [string]$Scenario = 'riverton'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$server = if ($ServerPath) { [IO.Path]::GetFullPath($ServerPath) } else { Join-Path $repoRoot "src\ArcGISProMCP.Server\bin\$Configuration\net10.0\arcgis-pro-mcp.exe" }
$fakeHost = if ($FakeHostPath) { [IO.Path]::GetFullPath($FakeHostPath) } else { Join-Path $repoRoot "tools\ArcGISProMCP.FakeHost\bin\$Configuration\net10.0\ArcGISProMCP.FakeHost.exe" }
foreach ($path in @($server, $fakeHost)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Not built: $path" }
}

function Get-RedirectedStartInfo([string]$Path, [string[]]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    if ([IO.Path]::GetExtension($Path) -eq '.dll') {
        $start.FileName = 'dotnet'
        $start.Arguments = (@('"' + $Path + '"') + $Arguments) -join ' '
    } else {
        $start.FileName = $Path
        $start.Arguments = $Arguments -join ' '
    }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    return $start
}

function Read-LineWithin($Reader, [int]$Seconds) {
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait([TimeSpan]::FromSeconds($Seconds))) { throw "Timed out after $Seconds s waiting for output." }
    return $task.Result
}

function Open-Gateway([hashtable]$Environment) {
    $start = Get-RedirectedStartInfo $server @()
    foreach ($name in @('ARCGIS_PRO_MCP_HOST_PID', 'ARCGIS_PRO_MCP_PIPE', 'ARCGIS_PRO_MCP_ALLOW_FAKEHOST')) {
        $start.EnvironmentVariables.Remove($name)
    }
    foreach ($name in $Environment.Keys) { $start.EnvironmentVariables[$name] = [string]$Environment[$name] }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $null = $process.Start()
    return [pscustomobject]@{ Process = $process; Errors = $process.StandardError.ReadToEndAsync(); NextId = 0 }
}

function Close-Gateway($Gateway) {
    if ($null -eq $Gateway -or $Gateway.Process.HasExited) { return }
    $Gateway.Process.StandardInput.Close()
    if (-not $Gateway.Process.WaitForExit(10000)) { $Gateway.Process.Kill() }
}

function Invoke-Mcp($Gateway, [string]$Method, $Parameters) {
    $Gateway.NextId++
    $id = $Gateway.NextId
    $json = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
    $Gateway.Process.StandardInput.WriteLine($json)
    $Gateway.Process.StandardInput.Flush()
    while ($true) {
        $line = Read-LineWithin $Gateway.Process.StandardOutput 60
        if ($null -eq $line) { throw "Gateway closed stdout: $($Gateway.Errors.Result)" }
        $reply = $line | ConvertFrom-Json
        if ($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
            if ($reply.PSObject.Properties['error'] -and $reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
            return $reply.result
        }
    }
}

function Initialize-Gateway($Gateway) {
    $init = Invoke-Mcp $Gateway 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'fakehost-smoke'; version = '1.0' } }
    $Gateway.Process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $Gateway.Process.StandardInput.Flush()
    return $init
}

# Returns the tool envelope (ok, result, error) without throwing on a failed call.
function Invoke-ToolEnvelope($Gateway, [string]$Name, $Arguments) {
    $reply = Invoke-Mcp $Gateway 'tools/call' @{ name = $Name; arguments = $Arguments }
    $envelope = $reply.structuredContent
    if (-not $envelope) { throw "$Name returned no structuredContent." }
    return $envelope
}

function Invoke-Tool($Gateway, [string]$Name, $Arguments) {
    $envelope = Invoke-ToolEnvelope $Gateway $Name $Arguments
    if (-not $envelope.ok) { throw "$Name failed: $($envelope.error | ConvertTo-Json -Compress)" }
    return $envelope.result
}

# --auto-approve stands in for the person at the panel; it is for scripted and eval runs only.
$hostStart = Get-RedirectedStartInfo $fakeHost @('--scenario', $Scenario, '--auto-approve')
$hostProcess = New-Object Diagnostics.Process
$hostProcess.StartInfo = $hostStart
$gateway = $null
$unselected = $null
$hostRest = $null
$hostLog = New-Object Collections.Generic.List[string]
try {
    $null = $hostProcess.Start()
    $hostErrors = $hostProcess.StandardError.ReadToEndAsync()
    while ($true) {
        $line = Read-LineWithin $hostProcess.StandardOutput 30
        if ($null -eq $line) { throw "FakeHost exited during startup: $($hostErrors.Result)" }
        $hostLog.Add($line)
        if ($line.StartsWith('Commands:')) { break }
    }
    # Keep draining FakeHost's console so it never blocks on a full pipe.
    $hostRest = $hostProcess.StandardOutput.ReadToEndAsync()

    # The discovery record must say what it is, so nothing mistakes it for ArcGIS Pro.
    $recordPath = Join-Path $env:LOCALAPPDATA "ArcGISProMCP\hosts\host-$($hostProcess.Id).json"
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { throw "FakeHost did not publish $recordPath." }
    $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    if ($record.hostKind -ne 'fakehost') { throw "Discovery record hostKind is '$($record.hostKind)', expected 'fakehost'." }
    if (-not ([string]$record.projectName).StartsWith('[FakeHost] ')) { throw "Discovery record project name '$($record.projectName)' lacks the [FakeHost] prefix." }

    $gateway = Open-Gateway @{ ARCGIS_PRO_MCP_HOST_PID = $hostProcess.Id }
    $init = Initialize-Gateway $gateway

    $state = Invoke-Tool $gateway 'system_get_state' @{}
    if ($state.processId -ne $hostProcess.Id) { throw "system_get_state answered from PID $($state.processId), not FakeHost $($hostProcess.Id)." }
    if ($state.workspace.project.name -ne 'Riverton') { throw "Unexpected project '$($state.workspace.project.name)'." }
    $revision = $state.workspace.revision

    $hits = @(Invoke-Tool $gateway 'registry_search' @{ query = 'delete a feature'; limit = 5 })
    if ('feature.delete' -notin @($hits | ForEach-Object { $_.operation.id })) { throw 'registry_search did not find feature.delete.' }

    $arguments = @{ layer = 'Parcels'; target = @{ objectId = 8 } }
    $request = Invoke-Tool $gateway 'approval_request' @{ operationId = 'feature.delete'; arguments = $arguments; expectedRevision = $revision }
    $status = Invoke-Tool $gateway 'approval_status' @{ requestId = $request.requestId; waitSeconds = 10 }
    if ($status.status -ne 'approved') { throw "Approval was '$($status.status)', expected approved." }
    $deleted = Invoke-Tool $gateway 'registry_invoke' @{ operationId = 'feature.delete'; arguments = $arguments; expectedRevision = $revision; confirmationToken = $status.confirmationToken }
    if (-not $deleted.success) { throw 'feature.delete did not succeed.' }

    # A gateway with no selector (an ordinary client configuration) must never attach to FakeHost.
    # Skipped while ArcGIS Pro runs: the gateway would then (correctly) call the real Pro, and this
    # smoke test does not touch a real project. The unit tests cover that case.
    if (@(Get-Process -Name 'ArcGISPro' -ErrorAction SilentlyContinue).Count -gt 0) {
        $unselectedOutcome = 'skipped (ArcGIS Pro is running)'
    }
    else {
        $unselected = Open-Gateway @{}
        $null = Initialize-Gateway $unselected
        $unselectedState = Invoke-ToolEnvelope $unselected 'system_get_state' @{}
        if ($unselectedState.ok) {
            throw "A gateway without ARCGIS_PRO_MCP_HOST_PID, ARCGIS_PRO_MCP_PIPE or ARCGIS_PRO_MCP_ALLOW_FAKEHOST attached to PID $($unselectedState.result.processId)."
        }
        if ($unselectedState.error.code -ne 'arcgis_host_not_found') { throw "Unselected gateway failed with '$($unselectedState.error.code)', expected arcgis_host_not_found." }
        $unselectedOutcome = 'refused (arcgis_host_not_found)'
    }

    [pscustomobject]@{
        Gateway = $init.serverInfo.name + ' ' + $init.serverInfo.version
        FakeHostPid = $hostProcess.Id
        HostKind = $record.hostKind
        Project = $state.workspace.project.name
        RevisionBefore = $revision
        RevisionAfter = $deleted.workspaceRevision
        Deleted = 'Parcels ObjectID 8'
        UnselectedGateway = $unselectedOutcome
    }
}
finally {
    Close-Gateway $unselected
    Close-Gateway $gateway
    if (-not $hostProcess.HasExited) {
        try { $hostProcess.StandardInput.WriteLine('q'); $hostProcess.StandardInput.Close() } catch { Write-Verbose $_ }
        if (-not $hostProcess.WaitForExit(10000)) { $hostProcess.Kill() }
    }
    if ($hostRest) { $hostLog.AddRange([string[]]($hostRest.Result -split "`r?`n")) }
    Write-Verbose ($hostLog -join [Environment]::NewLine)
}
