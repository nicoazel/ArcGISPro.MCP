[CmdletBinding()]
param([string]$Configuration='Debug', [string]$ImageUri, [switch]$ApprovalProbe, [string]$ServerPath, [switch]$Offline)
$ErrorActionPreference='Stop'
if ($Offline -and ($ApprovalProbe -or $ImageUri)) { throw 'Offline verification cannot test host approvals or images.' }
$repoRoot=Split-Path -Parent $PSScriptRoot
$server = if ($ServerPath) { [IO.Path]::GetFullPath($ServerPath) } else { Join-Path $repoRoot "src\ArcGISProMCP.Server\bin\$Configuration\net10.0\arcgis-pro-mcp.dll" }
if (-not (Test-Path -LiteralPath $server -PathType Leaf)) { throw "Server was not found: $server" }
$start = if ([IO.Path]::GetExtension($server) -eq '.exe') {
    [Diagnostics.ProcessStartInfo]::new($server)
} else {
    [Diagnostics.ProcessStartInfo]::new('dotnet', ('"'+$server+'"'))
}
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardInput=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$process=[Diagnostics.Process]::new()
$process.StartInfo=$start
$null=$process.Start()
$errorRead=$process.StandardError.ReadToEndAsync()
$script:requestId=0
function Invoke-Mcp([string]$Method, $Parameters) {
    $script:requestId++
    $id=$script:requestId
    $json=@{jsonrpc='2.0';id=$id;method=$Method;params=$Parameters} | ConvertTo-Json -Depth 30 -Compress
    $process.StandardInput.WriteLine($json)
    $process.StandardInput.Flush()
    while($true) {
        $line=$process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
        if($null -eq $line) { throw 'MCP server closed stdout.' }
        $reply=$line | ConvertFrom-Json
        if($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
            if($reply.PSObject.Properties['error'] -and $reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
            if($reply.result.PSObject.Properties['isError'] -and $reply.result.isError) { throw ($reply.result.content | ConvertTo-Json -Compress) }
            return $reply.result
        }
    }
}
# Tools return { ok, result, error } as structuredContent (and the same JSON as text).
function Get-ToolResult($Reply) {
    $envelope=$Reply.structuredContent
    if(-not $envelope) { throw 'Tool result has no structuredContent.' }
    if(-not $envelope.ok) { throw ($envelope.error | ConvertTo-Json -Compress) }
    return $envelope.result
}
try {
    $init=Invoke-Mcp 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='arcgis-mcp-live-test';version='1.0'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $tools=Invoke-Mcp 'tools/list' @{}
    if(@($tools.tools).Count -ne 16) { throw 'Expected exactly 16 registry, workflow, skill, review and visual entry tools.' }
    foreach($required in @('approval_request','approval_status','approval_cancel')) {
        if($required -notin $tools.tools.name) { throw "Missing review tool: $required" }
    }
    foreach($tool in $tools.tools) {
        foreach($hint in @('readOnlyHint','destructiveHint','idempotentHint','openWorldHint')) {
            if(-not $tool.annotations -or $tool.annotations.$hint -isnot [bool]) { throw "Tool $($tool.name) does not declare $hint." }
        }
        if(-not $tool.outputSchema -or $tool.outputSchema.type -ne 'object' -or
            -not $tool.outputSchema.properties.ok -or -not $tool.outputSchema.properties.error) { throw "Tool $($tool.name) has no envelope outputSchema." }
    }
    $skill=Get-ToolResult (Invoke-Mcp 'tools/call' @{name='skill_get';arguments=@{skillId='arcgis.cartography.master-plan'}})
    if($skill.id -ne 'arcgis.cartography.master-plan') { throw 'skill_get returned the wrong skill.' }
    if (-not $Offline) {
        $state=Invoke-Mcp 'tools/call' @{name='system_get_state';arguments=@{}}
        $search=Invoke-Mcp 'tools/call' @{name='registry_search';arguments=@{query='layout title';limit=5}}
        $describe=Invoke-Mcp 'tools/call' @{name='registry_describe';arguments=@{operationId='feature.create'}}
        $descriptor=Get-ToolResult $describe
        if($descriptor.id -ne 'feature.create' -or -not $descriptor.inputSchema -or
            -not $descriptor.inputSchema.properties.geometry) { throw 'Feature-create registry schema was missing or incomplete.' }
        if(-not $descriptor.resultSchema -or -not $descriptor.resultSchema.properties.workspaceRevision) { throw 'registry_describe did not return a resultSchema.' }
        $validate=Invoke-Mcp 'tools/call' @{name='registry_validate';arguments=@{operationId='project.get';arguments=@{}}}
        $validation=Get-ToolResult $validate
        if(-not $validation.valid) { throw 'Registry validation rejected valid project.get arguments.' }
    }
    $approvalVerified=$false
    if($ApprovalProbe) {
        $snapshot=Get-ToolResult $state
        $pending=Invoke-Mcp 'tools/call' @{name='approval_request';arguments=@{operationId='gp.run';arguments=@{tool='management.GetCount';parameters=@('nonexistent-test-layer')};expectedRevision=$snapshot.workspace.revision}}
        $review=Get-ToolResult $pending
        if($review.status -ne 'pending' -or $review.confirmationToken) { throw 'Expected pending review without token.' }
        try {
            # Give the actual WPF approval template time to materialize before cancelling.
            Start-Sleep -Milliseconds 1500
            $status=Invoke-Mcp 'tools/call' @{name='approval_status';arguments=@{requestId=$review.requestId}}
            if((Get-ToolResult $status).status -ne 'pending') { throw 'Review state changed unexpectedly.' }
            # A short wait on an undecided request returns pending when it elapses.
            $waited=Invoke-Mcp 'tools/call' @{name='approval_status';arguments=@{requestId=$review.requestId;waitSeconds=1}}
            if((Get-ToolResult $waited).status -ne 'pending') { throw 'Waiting changed the review state unexpectedly.' }
        }
        finally {
            $cancel=Invoke-Mcp 'tools/call' @{name='approval_cancel';arguments=@{requestId=$review.requestId}}
        }
        $status=Invoke-Mcp 'tools/call' @{name='approval_status';arguments=@{requestId=$review.requestId}}
        if((Get-ToolResult $status).status -ne 'cancelled') { throw 'Review was not cancelled.' }
        $approvalVerified=$true
    }
    $imageVerified=$false
    if($ImageUri) {
        $observation=Invoke-Mcp 'tools/call' @{name='resource_read';arguments=@{uri=$ImageUri}}
        $image=@($observation.content | Where-Object type -eq 'image')
        if($image.Count -ne 1 -or -not $image[0].data) { throw 'Expected one native MCP image block.' }
        $imageVerified=$true
    }
    [pscustomobject]@{Server=$init.serverInfo.name;Protocol=$init.protocolVersion;Tools=@($tools.tools).Count;LiveState=(-not $Offline);SkillRead=$true;RegistrySearch=(-not $Offline);RegistrySchema=(-not $Offline);ImageBlock=$imageVerified;PendingCancelReview=$approvalVerified}
}
finally {
    $process.StandardInput.Close()
    if(-not $process.WaitForExit(5000)) { $process.Kill() }
    $process.Dispose()
}
