[CmdletBinding()]
param([string]$Configuration='Debug', [string]$ImageUri)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
$server=Join-Path $repoRoot "src\ArcGISProMCP.Server\bin\$Configuration\net10.0\arcgis-pro-mcp.dll"
$start=[Diagnostics.ProcessStartInfo]::new('dotnet', ('"'+$server+'"'))
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
        if($reply.id -eq $id) {
            if($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
            if($reply.result.isError) { throw ($reply.result.content | ConvertTo-Json -Compress) }
            return $reply.result
        }
    }
}
try {
    $init=Invoke-Mcp 'initialize' @{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='arcgis-mcp-live-test';version='1.0'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $tools=Invoke-Mcp 'tools/list' @{}
    if(@($tools.tools).Count -lt 13) { throw 'Expected registry, workflow, skill and visual tools.' }
    $state=Invoke-Mcp 'tools/call' @{name='system_get_state';arguments=@{}}
    $skill=Invoke-Mcp 'tools/call' @{name='skill_get';arguments=@{skillId='arcgis.cartography.master-plan'}}
    $search=Invoke-Mcp 'tools/call' @{name='registry_search';arguments=@{query='layout title';limit=5}}
    $imageVerified=$false
    if($ImageUri) {
        $observation=Invoke-Mcp 'tools/call' @{name='resource_read';arguments=@{uri=$ImageUri}}
        $image=@($observation.content | Where-Object type -eq 'image')
        if($image.Count -ne 1 -or -not $image[0].data) { throw 'Expected one native MCP image block.' }
        $imageVerified=$true
    }
    [pscustomobject]@{Server=$init.serverInfo.name;Protocol=$init.protocolVersion;Tools=@($tools.tools).Count;LiveState=$true;SkillRead=$true;RegistrySearch=$true;ImageBlock=$imageVerified}
}
finally {
    $process.StandardInput.Close()
    if(-not $process.WaitForExit(5000)) { $process.Kill() }
    $process.Dispose()
}
