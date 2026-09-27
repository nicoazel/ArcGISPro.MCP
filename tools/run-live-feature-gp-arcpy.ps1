[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PipeName,
    [string]$Configuration = 'Release',
    [string]$EvidenceDirectory,
    [string]$DataDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $repoRoot "tools\ArcGISProMCP.DemoRunner\bin\$Configuration\net10.0\ArcGISProMCP.DemoRunner.dll"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "Build the DemoRunner first: $runner" }

if (-not $EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repoRoot ('artifacts\live-acceptance\evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (-not $DataDirectory) {
    $DataDirectory = Join-Path $repoRoot ('artifacts\live-acceptance\data-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$null = New-Item -ItemType Directory -Path $EvidenceDirectory -Force
$null = New-Item -ItemType Directory -Path $DataDirectory -Force
$env:ARCGIS_PRO_MCP_PIPE = $PipeName
$script:sequence = 0

function Invoke-BridgeCall {
    param(
        [string]$Name,
        [string]$Method,
        $Parameters,
        [switch]$AllowFailure
    )
    $script:sequence++
    $prefix = '{0:D2}-{1}' -f $script:sequence, $Name
    $requestPath = Join-Path $EvidenceDirectory "$prefix.request.json"
    $resultPath = Join-Path $EvidenceDirectory "$prefix.result.json"
    @{ method = $Method; parameters = $Parameters } |
        ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath $requestPath -Encoding utf8
    & dotnet $runner --call $requestPath $resultPath | Out-Null
    $exitCode = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "Bridge call '$Name' did not create $resultPath (exit $exitCode)."
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if (-not $AllowFailure -and ($exitCode -ne 0 -or
            ($result.PSObject.Properties['success'] -and -not $result.success))) {
        throw "Bridge call '$Name' failed; inspect $resultPath."
    }
    return $result
}

function Invoke-Operation {
    param(
        [string]$Name,
        [string]$OperationId,
        $Arguments,
        [string]$ExpectedRevision,
        [switch]$AllowFailure
    )
    $parameters = @{
        operationId = $OperationId
        arguments = $Arguments
        idempotencyKey = "acceptance-$Name-$([Guid]::NewGuid().ToString('N'))"
    }
    if ($ExpectedRevision) { $parameters.expectedRevision = $ExpectedRevision }
    return Invoke-BridgeCall -Name $Name -Method 'registry.invoke' -Parameters $parameters -AllowFailure:$AllowFailure
}

function Assert-Success($Result, [string]$Name) {
    if (-not $Result.success) { throw "$Name was not successful." }
}

function Assert-AutonomousNotice($Result, [string]$Name) {
    if (@($Result.notices | Where-Object code -eq 'autonomous_control').Count -ne 1) {
        throw "$Name did not return the autonomous_control warning."
    }
}

$state = Invoke-BridgeCall 'state' 'system.get_state' @{}
if ($state.operationCount -lt 37) { throw "Expected at least 37 operations, received $($state.operationCount)." }
foreach ($capability in @('arcpy', 'autonomous-control')) {
    if (-not ($state.workspace.capabilities | Where-Object { $_.id -eq $capability -and $_.available })) {
        throw "Required live capability '$capability' is not available."
    }
}

$inspectSetup = Invoke-Operation 'arcpy-inspect-setup' 'arcpy.inspect-script' @{ scriptPath = 'setup_acceptance.py' }
Assert-Success $inspectSetup 'ArcPy setup inspection'
$setupHash = $inspectSetup.data.sha256
$setup = Invoke-Operation 'arcpy-run-setup' 'arcpy.run-script' @{
    scriptPath = 'setup_acceptance.py'
    scriptSha256 = $setupHash
    arguments = @($DataDirectory)
    timeoutSeconds = 120
}
Assert-Success $setup 'ArcPy setup execution'
Assert-AutonomousNotice $setup 'ArcPy setup execution'
$setupOutput = $setup.data.stdout.Trim() | ConvertFrom-Json
$featureClass = [IO.Path]::GetFullPath($setupOutput.featureClass)
$geodatabase = [IO.Path]::GetFullPath($setupOutput.geodatabase)
if (-not (Test-Path -LiteralPath $geodatabase -PathType Container)) { throw 'ArcPy did not create the acceptance geodatabase.' }

$wrongHash = Invoke-Operation 'arcpy-wrong-hash' 'arcpy.run-script' @{
    scriptPath = 'setup_acceptance.py'
    scriptSha256 = ('0' * 64)
    arguments = @($DataDirectory)
    timeoutSeconds = 30
} -AllowFailure
if ($wrongHash.success -or $wrongHash.errorCode -ne 'arcpy_script_changed') { throw 'ArcPy hash mismatch did not fail closed.' }

$inspectFailure = Invoke-Operation 'arcpy-inspect-nonzero' 'arcpy.inspect-script' @{ scriptPath = 'nonzero.py' }
$nonzero = Invoke-Operation 'arcpy-run-nonzero' 'arcpy.run-script' @{
    scriptPath = 'nonzero.py'
    scriptSha256 = $inspectFailure.data.sha256
    arguments = @()
    timeoutSeconds = 10
} -AllowFailure
if ($nonzero.success -or $nonzero.errorCode -ne 'arcpy_exit_nonzero' -or $nonzero.data.exitCode -ne 7) {
    throw 'ArcPy nonzero exit behavior drifted.'
}
Assert-AutonomousNotice $nonzero 'ArcPy nonzero execution'

$inspectTimeout = Invoke-Operation 'arcpy-inspect-timeout' 'arcpy.inspect-script' @{ scriptPath = 'timeout.py' }
$timeout = Invoke-Operation 'arcpy-run-timeout' 'arcpy.run-script' @{
    scriptPath = 'timeout.py'
    scriptSha256 = $inspectTimeout.data.sha256
    arguments = @()
    timeoutSeconds = 1
} -AllowFailure
if ($timeout.success -or $timeout.errorCode -ne 'arcpy_timed_out') { throw 'ArcPy timeout behavior drifted.' }
Assert-AutonomousNotice $timeout 'ArcPy timeout execution'

$map = Invoke-Operation 'ensure-map' 'map.ensure' @{ name = 'MCP Acceptance'; type = 'map'; basemap = 'None' }
Assert-Success $map 'Map ensure'
$layer = Invoke-Operation 'add-layer' 'layer.add' @{ map = 'MCP Acceptance'; source = $featureClass; name = 'Design Sites' }
Assert-Success $layer 'Layer add'

$describe = Invoke-Operation 'feature-describe' 'feature.layer.describe' @{ map = 'MCP Acceptance'; layer = 'Design Sites' }
Assert-Success $describe 'Feature layer describe'
if (-not $describe.data.editable -or $describe.data.geometryType -ne 'Polygon' -or -not $describe.data.globalIdField) {
    throw 'Feature layer schema/editability did not match the acceptance fixture.'
}

$baseline = Invoke-Operation 'feature-query-baseline' 'feature.query' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; where = '1=1'; fields = @('Name', 'Units', 'FAR'); limit = 10
}
Assert-Success $baseline 'Baseline feature query'
if ($baseline.data.returned -ne 1) { throw 'Expected one baseline feature.' }

$created = Invoke-Operation 'feature-create' 'feature.create' @{
    map = 'MCP Acceptance'
    layer = 'Design Sites'
    attributes = @{ Name = 'Agent Test Site'; Units = 120; FAR = 4.75; ReviewDate = '2026-09-08T15:30:00Z' }
    geometry = @{ type = 'polygon'; coordinates = @(
        @(1366340.0, 416760.0), @(1366410.0, 416760.0), @(1366410.0, 416830.0),
        @(1366340.0, 416830.0), @(1366340.0, 416760.0)
    ) }
}
Assert-Success $created 'Feature create'

$createdQuery = Invoke-Operation 'feature-query-created' 'feature.query' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; where = "Name = 'Agent Test Site'"; fields = @('Name', 'Units', 'FAR', 'ReviewDate'); limit = 10
}
Assert-Success $createdQuery 'Created feature query'
$createdRows = @($createdQuery.data.rows)
if ($createdRows.Count -ne 1 -or -not $createdRows[0].globalId) { throw 'Created feature did not read back by stable identity.' }
$createdGlobalId = [string]$createdRows[0].globalId

$updated = Invoke-Operation 'feature-update' 'feature.update' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; target = @{ globalId = $createdGlobalId }; attributes = @{ Units = 140; FAR = 5.25 }
}
Assert-Success $updated 'Feature update'

$selected = Invoke-Operation 'feature-select' 'feature.select' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; where = "Name = 'Agent Test Site'"; mode = 'new'; limit = 10
}
Assert-Success $selected 'Feature select'
if ($selected.data.selectionCount -ne 1) { throw 'Feature selection count drifted.' }

$wrongGeometry = Invoke-Operation 'feature-wrong-geometry' 'feature.create' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; attributes = @{ Name = 'Must Not Exist' }; geometry = @{ type = 'point'; x = 1366300; y = 416800 }
} -AllowFailure
if ($wrongGeometry.success) { throw 'Wrong feature geometry type was accepted.' }

$staleUpdate = Invoke-Operation 'feature-stale-revision' 'feature.update' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; target = @{ globalId = $createdGlobalId }; attributes = @{ Units = 999 }
} -ExpectedRevision 'intentionally-stale' -AllowFailure
if ($staleUpdate.success -or $staleUpdate.errorCode -ne 'workspace_revision_mismatch') { throw 'Stale feature update did not fail closed.' }

$metadataBefore = Invoke-Operation 'metadata-get-before' 'metadata.get' @{ map = 'MCP Acceptance'; layer = 'Design Sites'; includeXml = $true }
Assert-Success $metadataBefore 'Metadata read'
$metadataUpdate = Invoke-Operation 'metadata-update' 'metadata.update' @{
    map = 'MCP Acceptance'
    layer = 'Design Sites'
    title = 'MCP Urban Design Acceptance Sites'
    summary = 'Disposable feature and geoprocessing acceptance layer.'
    description = 'Created and edited through the standalone ArcGIS Pro MCP live acceptance harness.'
    tags = @('mcp', 'urban-design', 'acceptance')
    credits = 'Synthetic acceptance fixture'
    useLimitations = 'Testing only; not authoritative planning data.'
}
Assert-Success $metadataUpdate 'Metadata update'
Assert-AutonomousNotice $metadataUpdate 'Metadata update'
$metadataAfter = Invoke-Operation 'metadata-get-after' 'metadata.get' @{ map = 'MCP Acceptance'; layer = 'Design Sites'; includeXml = $true }
Assert-Success $metadataAfter 'Metadata readback'
if ($metadataAfter.data.metadata.title -ne 'MCP Urban Design Acceptance Sites' -or
    'urban-design' -notin @($metadataAfter.data.metadata.tags)) { throw 'Metadata values did not read back.' }

$bufferCounts = @()
for ($iteration = 1; $iteration -le 10; $iteration++) {
    $bufferPath = Join-Path $geodatabase ('DesignSites_Buffer_{0:D2}' -f $iteration)
    $buffer = Invoke-Operation ("gp-buffer-{0:D2}" -f $iteration) 'gp.run' @{
        tool = 'analysis.Buffer'
        parameters = @($featureClass, $bufferPath, '50 Feet')
        environments = @{ workspace = $geodatabase; scratchWorkspace = $geodatabase }
        overwriteOutput = $false
        addOutputsToMap = $false
        addToHistory = $true
        refreshProjectItems = $false
    }
    Assert-Success $buffer "Buffer iteration $iteration"
    Assert-AutonomousNotice $buffer "Buffer iteration $iteration"
    if ($buffer.data.isFailed -or $buffer.data.returnValue -ne $bufferPath) { throw "Buffer iteration $iteration returned an unexpected result." }

    $count = Invoke-Operation ("gp-count-{0:D2}" -f $iteration) 'gp.run' @{
        tool = 'management.GetCount'
        parameters = @($bufferPath)
        environments = @{}
        overwriteOutput = $false
        addOutputsToMap = $false
        addToHistory = $false
        refreshProjectItems = $false
    }
    Assert-Success $count "GetCount iteration $iteration"
    $bufferCounts += [int]$count.data.returnValue
}
if (@($bufferCounts | Where-Object { $_ -ne 2 }).Count -ne 0) { throw 'Buffered output counts were not deterministic.' }

$existingBuffer = Invoke-Operation 'gp-existing-output-rejection' 'gp.run' @{
    tool = 'analysis.Buffer'
    parameters = @($featureClass, (Join-Path $geodatabase 'DesignSites_Buffer_01'), '50 Feet')
    environments = @{ workspace = $geodatabase }
    overwriteOutput = $false
    addOutputsToMap = $false
    addToHistory = $false
    refreshProjectItems = $false
} -AllowFailure
if ($existingBuffer.success -or $existingBuffer.errorCode -ne 'geoprocessing_failed') {
    throw 'Existing geoprocessing output was not rejected.'
}

$deleted = Invoke-Operation 'feature-delete' 'feature.delete' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; target = @{ globalId = $createdGlobalId }
}
Assert-Success $deleted 'Feature delete'
Assert-AutonomousNotice $deleted 'Feature delete'
$finalQuery = Invoke-Operation 'feature-query-final' 'feature.query' @{
    map = 'MCP Acceptance'; layer = 'Design Sites'; where = '1=1'; fields = @('Name', 'Units', 'FAR'); limit = 10
}
Assert-Success $finalQuery 'Final feature query'
if ($finalQuery.data.returned -ne 1 -or $finalQuery.data.rows[0].Name -ne 'Baseline Site') {
    throw 'Feature deletion did not leave exactly the baseline feature.'
}

$save = Invoke-Operation 'project-save' 'project.save' @{}
Assert-Success $save 'Project save'

$summary = [ordered]@{
    success = $true
    processId = $state.processId
    project = $state.workspace.project.uri
    operationCount = $state.operationCount
    arcPy = [ordered]@{
        version = $setupOutput.arcgisVersion
        product = $setupOutput.product
        setupHash = $setupHash
        nonzeroExit = 7
        timeoutVerified = $true
        changedHashRejected = $true
    }
    feature = [ordered]@{
        layer = $featureClass
        globalId = $createdGlobalId
        createQueryUpdateSelectDelete = $true
        wrongGeometryRejected = $true
        staleRevisionRejected = $true
        remainingFeatures = $finalQuery.data.returned
    }
    metadata = [ordered]@{
        scope = $metadataAfter.data.metadataScope
        title = $metadataAfter.data.metadata.title
        tags = @($metadataAfter.data.metadata.tags)
        xmlLengthBefore = $metadataBefore.data.xmlLength
        xmlLengthAfter = $metadataAfter.data.xmlLength
    }
    geoprocessing = [ordered]@{
        bufferRuns = 10
        counts = $bufferCounts
        existingOutputRejected = $true
    }
    autonomousControl = $true
    evidenceDirectory = $EvidenceDirectory
    dataDirectory = $DataDirectory
}
$summaryPath = Join-Path $EvidenceDirectory 'summary.json'
$summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary
"Evidence: $summaryPath"
