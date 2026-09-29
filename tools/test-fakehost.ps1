# FakeHost smoke test: starts tools/ArcGISProMCP.FakeHost on the real named pipe (with its discovery
# record), then the gateway over stdio pointed at it, and runs a short scripted MCP session:
# initialize, system_get_state, registry_search and an approved feature.delete. Both processes are
# stopped before the script returns. Runs in Windows PowerShell 5.1 and PowerShell 7.
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

function New-RedirectedProcess([string]$Path, [string[]]$Arguments) {
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

# --auto-approve stands in for the person at the panel; it is for scripted and eval runs only.
$hostStart = New-RedirectedProcess $fakeHost @('--scenario', $Scenario, '--auto-approve')
$hostProcess = New-Object Diagnostics.Process
$hostProcess.StartInfo = $hostStart
$gateway = $null
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

    $gatewayStart = New-RedirectedProcess $server @()
    $gatewayStart.EnvironmentVariables['ARCGIS_PRO_MCP_HOST_PID'] = [string]$hostProcess.Id
    $gatewayStart.EnvironmentVariables.Remove('ARCGIS_PRO_MCP_PIPE')
    $gateway = New-Object Diagnostics.Process
    $gateway.StartInfo = $gatewayStart
    $null = $gateway.Start()
    $gatewayErrors = $gateway.StandardError.ReadToEndAsync()

    $script:requestId = 0
    function Invoke-Mcp([string]$Method, $Parameters) {
        $script:requestId++
        $id = $script:requestId
        $json = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 30 -Compress
        $gateway.StandardInput.WriteLine($json)
        $gateway.StandardInput.Flush()
        while ($true) {
            $line = Read-LineWithin $gateway.StandardOutput 60
            if ($null -eq $line) { throw "Gateway closed stdout: $($gatewayErrors.Result)" }
            $reply = $line | ConvertFrom-Json
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
                if ($reply.PSObject.Properties['error'] -and $reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
                return $reply.result
            }
        }
    }
    function Invoke-Tool([string]$Name, $Arguments) {
        $reply = Invoke-Mcp 'tools/call' @{ name = $Name; arguments = $Arguments }
        $envelope = $reply.structuredContent
        if (-not $envelope) { throw "$Name returned no structuredContent." }
        if (-not $envelope.ok) { throw "$Name failed: $($envelope.error | ConvertTo-Json -Compress)" }
        return $envelope.result
    }

    $init = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'fakehost-smoke'; version = '1.0' } }
    $gateway.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $gateway.StandardInput.Flush()

    $state = Invoke-Tool 'system_get_state' @{}
    if ($state.processId -ne $hostProcess.Id) { throw "system_get_state answered from PID $($state.processId), not FakeHost $($hostProcess.Id)." }
    if ($state.workspace.project.name -ne 'Riverton') { throw "Unexpected project '$($state.workspace.project.name)'." }
    $revision = $state.workspace.revision

    $hits = @(Invoke-Tool 'registry_search' @{ query = 'delete a feature'; limit = 5 })
    if ('feature.delete' -notin @($hits | ForEach-Object { $_.operation.id })) { throw 'registry_search did not find feature.delete.' }

    $arguments = @{ layer = 'Parcels'; target = @{ objectId = 8 } }
    $request = Invoke-Tool 'approval_request' @{ operationId = 'feature.delete'; arguments = $arguments; expectedRevision = $revision }
    $status = Invoke-Tool 'approval_status' @{ requestId = $request.requestId; waitSeconds = 10 }
    if ($status.status -ne 'approved') { throw "Approval was '$($status.status)', expected approved." }
    $deleted = Invoke-Tool 'registry_invoke' @{ operationId = 'feature.delete'; arguments = $arguments; expectedRevision = $revision; confirmationToken = $status.confirmationToken }
    if (-not $deleted.success) { throw 'feature.delete did not succeed.' }

    [pscustomobject]@{
        Gateway = $init.serverInfo.name + ' ' + $init.serverInfo.version
        FakeHostPid = $hostProcess.Id
        Project = $state.workspace.project.name
        RevisionBefore = $revision
        RevisionAfter = $deleted.workspaceRevision
        Deleted = 'Parcels ObjectID 8'
    }
}
finally {
    if ($null -ne $gateway -and -not $gateway.HasExited) {
        $gateway.StandardInput.Close()
        if (-not $gateway.WaitForExit(10000)) { $gateway.Kill() }
    }
    if (-not $hostProcess.HasExited) {
        try { $hostProcess.StandardInput.WriteLine('q'); $hostProcess.StandardInput.Close() } catch { Write-Verbose $_ }
        if (-not $hostProcess.WaitForExit(10000)) { $hostProcess.Kill() }
    }
    if ($hostRest) { $hostLog.AddRange([string[]]($hostRest.Result -split "`r?`n")) }
    Write-Verbose ($hostLog -join [Environment]::NewLine)
}
