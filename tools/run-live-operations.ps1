<#
.SYNOPSIS
    Live operation matrix: exercises every registered operation against a disposable ArcGIS Pro
    project in DEFAULT mode, with a person approving or denying review cards in the dockpane.
.DESCRIPTION
    The ordered matrix lives in tools/live-operations-plan.json: phases of cases, each naming one
    operation, whether it is a happy path or a negative case, the error code or message fragment a
    negative case must produce, and which cases need an approval card. Every case key is implemented
    below by one Register-Case block. AcceptanceManifestTests checks that the plan covers every
    operation descriptor and that every key is implemented here.

    Phases: read-only discovery, map build, scene, layout, geoprocessing without review, ArcPy
    inspection, feature create, an MCP gateway pass, gated operations without a token, and finally
    the approval phase. In the approval phase the console asks the operator, one card at a time, to
    click Approve once (seven cards) or Deny (one card) in the MCP Studio dockpane. A card lives two
    minutes; an expired card is queued again until -OperatorTimeoutSeconds elapses, after which the
    case is recorded as skipped by the operator and the run continues. The final card opens a copy
    of the saved project.

    Safety:
      * Refuses a host that reports autonomous-control: without review, the "no token" negatives
        would execute (and delete, save or open) instead of failing closed.
      * Refuses unless the open project is under -DisposableRoot (FakeHost runs excepted). Test data
        is copied to <DisposableRoot>\operations-data-<stamp>; tests/data is never touched.
      * ArcPy cases run only when the host reports the arcpy capability (see docs/arcpy.md, "Live
        acceptance"); basemap.set Topographic only when a portal is reachable (-Offline skips it).
      * -PlanOnly (alias -DryRun) prints the ordered matrix and card list, checks the plan against
        the operation descriptors and this script, and contacts nothing.
      * -SkipCards is a pre-run without an operator: every case that needs an approval card is
        recorded as skipped (no card is queued), so a -SkipCards run can never pass and is never
        acceptance evidence. Use it to shake out the other cases before the operator session.

    Launch ArcGIS Pro on a fresh copy of the disposable .aprx for each session. After a forced
    close, reopening the same project brings up ArcGIS Pro's Project Recovery prompt, which blocks
    the start until someone answers it.

    Evidence (under -EvidenceDirectory): results/NNN-<operation>-<case>.request.json and
    .result.json for every bridge call (approval calls use approval.request-<case> and the like),
    results/NNN-gateway-<tool>-<case>.* for every MCP gateway call, summary.json (per-operation
    cases, coverage, approvals, message audit, skipCards, aborted), errors.md (a Markdown table of
    every negative case: operation, error code, message and message-audit findings) and console.log
    (what the operator was shown; working evidence only, never committed). The script exits 1
    unless summary.json reports allPassed, so a -SkipCards pre-run always exits 1.

    -AllowFakeHost is for shaking out this script against tools/ArcGISProMCP.FakeHost (run it with
    --auto-approve on its own pipe). Operations the FakeHost does not implement are recorded as
    skipped, the DENY card is skipped (an auto-approving host cannot deny), and the summary records
    hostKind fakehost. Such a run is never acceptance evidence.
.EXAMPLE
    ./tools/run-live-operations.ps1 -PlanOnly
.EXAMPLE
    ./tools/run-live-operations.ps1 -PipeName ArcGISProMCP.v1.12345 -DisposableRoot D:\scratch\mcp-acceptance
.EXAMPLE
    ./tools/run-live-operations.ps1 -PipeName ArcGISProMCP.v1.12345 -DisposableRoot D:\scratch\mcp-acceptance -SkipCards
#>
# An operator console script: the coloured prompts are for the person at the dockpane, and every
# result that matters is written to summary.json, errors.md and console.log, so Write-Host is intended.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator console prompts; results are recorded in summary.json, errors.md and console.log.')]
[CmdletBinding()]
param(
    [string]$PipeName,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DisposableRoot,
    [string]$EvidenceDirectory,
    [ValidateRange(60, 3600)]
    [int]$OperatorTimeoutSeconds = 600,
    [switch]$Offline,
    [switch]$AllowFakeHost,
    # Pre-run without an operator: approval-card cases are recorded as skipped (the section then cannot pass).
    [switch]$SkipCards,
    [Alias('DryRun')]
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$planPath = Join-Path $PSScriptRoot 'live-operations-plan.json'
$descriptorPath = Join-Path $repoRoot 'tests\ArcGISProMCP.Operations.Tests\Fixtures\operation-descriptors.json'
$expectedStatisticsPath = Join-Path $repoRoot 'tests\data\expected-statistics.json'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$fakeHostRun = [bool]$AllowFakeHost
$offlineRun = [bool]$Offline
$operatorTimeout = $OperatorTimeoutSeconds
$runnerPath = Join-Path $repoRoot "tools\ArcGISProMCP.DemoRunner\bin\$Configuration\net10.0\ArcGISProMCP.DemoRunner.dll"
$gatewayPath = Join-Path $repoRoot "src\ArcGISProMCP.Server\bin\$Configuration\net10.0\arcgis-pro-mcp.dll"

# ---------------------------------------------------------------- shared helpers

$script:consoleLines = New-Object System.Collections.Generic.List[string]

function Write-Operator {
    param([string]$Text, [string]$Color = 'Gray')
    $script:consoleLines.Add(('{0:HH:mm:ss} {1}' -f (Get-Date), $Text))
    Write-Host $Text -ForegroundColor $Color
}

function Write-Utf8File([string]$Path, [string]$Text) {
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    [IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function ConvertTo-JsonText($Value) {
    return (($Value | ConvertTo-Json -Depth 40) -replace "`r`n", "`n") + "`n"
}

function Get-Property($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) { if ($Object.Contains($Name)) { return $Object[$Name] } return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Read-Plan {
    $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
    $rows = New-Object System.Collections.ArrayList
    foreach ($phase in @($plan.phases)) {
        foreach ($case in @($phase.cases)) {
            $null = $rows.Add([pscustomobject]@{
                    Phase = [string]$phase.id
                    Key = [string]$case.key
                    Operation = [string]$case.operation
                    Kind = [string]$case.kind
                    Card = [string](Get-Property $case 'card')
                    Via = $(if (Get-Property $case 'via') { [string]$case.via } else { 'bridge' })
                    Requires = @(Get-Property $case 'requires' | Where-Object { $_ })
                    ExpectErrorCode = [string](Get-Property $case 'expectErrorCode')
                    ExpectMessageContains = @(Get-Property $case 'expectMessageContains' | Where-Object { $_ })
                    ExpectDryRunValid = Get-Property $case 'expectDryRunValid'
                    Target = [string](Get-Property $case 'target')
                    Description = [string](Get-Property $case 'description')
                })
        }
    }
    return [pscustomobject]@{ Phases = @($plan.phases); Cases = @($rows) }
}

function Get-DescriptorInfo {
    $descriptors = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
    return @($descriptors.PSObject.Properties | ForEach-Object {
            [pscustomobject]@{ Id = $_.Name; RequiresConfirmation = [bool]$_.Value.requiresConfirmation }
        } | Sort-Object Id)
}

# ---------------------------------------------------------------- case implementations
# Each case key in live-operations-plan.json has exactly one Register-Case block. -Invoke returns the
# primary call result (or, for card cases, a request spec @{ OperationId; Arguments }); the runner
# applies the plan's expectation to it and then runs -Verify, which throws on any mismatch.

$script:implementations = @{}

function Register-Case {
    param([string]$Key, [scriptblock]$Invoke, [scriptblock]$Verify)
    if ($script:implementations.ContainsKey($Key)) { throw "Case '$Key' is registered twice." }
    $script:implementations[$Key] = @{ Invoke = $Invoke; Verify = $Verify }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Context([string]$Name) {
    $value = $script:ctx[$Name]
    if ($null -eq $value -or ($value -is [string] -and [string]::IsNullOrWhiteSpace($value))) {
        throw "Prerequisite '$Name' is missing (an earlier case failed or was skipped)."
    }
    return $value
}

function Assert-Close([double]$Actual, [double]$Expected, [string]$What) {
    $tolerance = 1e-9 * [Math]::Max(1.0, [Math]::Abs($Expected))
    if ([Math]::Abs($Actual - $Expected) -gt $tolerance) { throw "$What is $Actual, expected $Expected." }
}

function Test-BoxOverlap($A, $B) {
    return ($A.x -lt $B.xMax) -and ($B.x -lt $A.xMax) -and ($A.y -lt $B.yMax) -and ($B.y -lt $A.yMax)
}

function Get-PngSize([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24 -or $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47) { throw "$Path is not a PNG." }
    $width = ([int]$bytes[16] -shl 24) -bor ([int]$bytes[17] -shl 16) -bor ([int]$bytes[18] -shl 8) -bor [int]$bytes[19]
    $height = ([int]$bytes[20] -shl 24) -bor ([int]$bytes[21] -shl 16) -bor ([int]$bytes[22] -shl 8) -bor [int]$bytes[23]
    return [pscustomobject]@{ Width = $width; Height = $height; Bytes = $bytes.Length }
}

function Assert-Capture($Result, [int]$Width, [int]$Height, [string]$Name) {
    $resource = [string](Get-Property $Result.Data 'resource')
    Assert-True ([bool]$resource) 'view.capture returned no resource handle.'
    $png = Join-Path $script:captureDirectory "$Name.png"
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $null = & dotnet $runnerPath --image $resource $png 2>&1 }
    finally { $ErrorActionPreference = $previous }
    Assert-True (Test-Path -LiteralPath $png -PathType Leaf) "Could not read capture resource $resource."
    $size = Get-PngSize $png
    Assert-True ($size.Width -eq $Width -and $size.Height -eq $Height) "Capture is $($size.Width)x$($size.Height), expected ${Width}x${Height}."
    Assert-True ($size.Bytes -gt 2048) "Capture is only $($size.Bytes) bytes; it is probably blank."
}

$map = 'Ops Map'
$scene = 'Ops Scene'
$layout = 'Ops Layout'
$frame = 'Main Frame'
$siteName = 'Ops Matrix Site'
$siteRing = @(@(1366340.0, 416760.0), @(1366410.0, 416760.0), @(1366410.0, 416830.0), @(1366340.0, 416830.0), @(1366340.0, 416760.0))
$landUseClasses = @(
    @{ value = 'multifamily'; label = 'Multifamily'; color = '#D98C3A' }
    @{ value = 'retail_commercial'; label = 'Retail / commercial'; color = '#C0392B' }
    @{ value = 'office'; label = 'Office'; color = '#7D5BA6' }
    @{ value = 'parks_recreation'; label = 'Parks'; color = '#4C9A2A' }
    @{ value = 'civic_facilities'; label = 'Civic'; color = '#2E86C1' }
)

function Get-BufferArgument {
    return [ordered]@{
        tool = 'analysis.Buffer'
        parameters = @((Assert-Context 'designSitesPath'), (Assert-Context 'bufferOutputPath'), '50 Feet')
        environments = [ordered]@{ workspace = (Assert-Context 'geodatabasePath') }
        overwriteOutput = $false
        addOutputsToMap = $false
        addToHistory = $true
        refreshProjectItems = $false
    }
}

function Get-MetadataArgument {
    return [ordered]@{
        map = $map
        layer = 'Design Sites'
        title = 'Ops Matrix Design Sites'
        summary = 'Disposable layer edited by the live operation matrix.'
        description = 'Written by tools/run-live-operations.ps1 after local review in the MCP Studio dockpane.'
        tags = @('mcp', 'acceptance', 'ops-matrix')
        credits = 'Synthetic acceptance fixture'
        useLimitations = 'Testing only; not authoritative planning data.'
    }
}

# --- read-only
Register-Case 'project-get' { Invoke-Op 'project.get' @{} } {
    param($Result)
    Assert-True ([bool](Get-Property (Get-Property $Result.Data 'project') 'name')) 'project.get returned no project name.'
}
Register-Case 'map-list' { Invoke-Op 'map.list' @{} }
Register-Case 'layer-list' { Invoke-Op 'layer.list' @{} }
Register-Case 'layout-list' { Invoke-Op 'layout.list' @{} }
Register-Case 'gp-search-buffer' { Invoke-Op 'gp.search' @{ query = 'buffer'; limit = 20 } } {
    param($Result)
    $names = @(@(Get-Property $Result.Data 'tools') | ForEach-Object { [string](Get-Property (Get-Property $_ 'tool') 'executionName') })
    Assert-True ('analysis.Buffer' -in $names) "gp.search 'buffer' did not return analysis.Buffer (got $($names -join ', '))."
}
Register-Case 'gp-describe-buffer' { Invoke-Op 'gp.describe' @{ tool = 'analysis.Buffer' } } {
    param($Result)
    Assert-True (@(Get-Property $Result.Data 'signature').Count -ge 3) 'gp.describe analysis.Buffer returned no positional signature.'
}
Register-Case 'style-search-circle' { Invoke-Op 'style.search' @{ query = 'circle'; type = 'point'; limit = 5 } }
Register-Case 'gp-describe-unknown-tool' { Invoke-Op 'gp.describe' @{ tool = 'nope.Tool' } }
Register-Case 'gp-describe-bare-name' { Invoke-Op 'gp.describe' @{ tool = 'Buffer' } } {
    param($Result)
    Assert-True ('analysis.Buffer' -in @(Get-Property $Result.Data 'suggestions')) 'gp.describe Buffer did not suggest analysis.Buffer.'
}
Register-Case 'layer-list-missing-map' { Invoke-Op 'layer.list' @{ map = 'No Such Map' } }

# --- map build
Register-Case 'map-ensure-ops-map' { Invoke-Op 'map.ensure' @{ name = $map; type = 'map'; basemap = 'None' } }
Register-Case 'basemap-set-topographic' { Invoke-Op 'basemap.set' @{ map = $map; basemap = 'Topographic' } }
Register-Case 'basemap-set-unknown' { Invoke-Op 'basemap.set' @{ map = $map; basemap = 'NotABasemap' } }
Register-Case 'layer-add-parcels' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'parcelsPath'); name = 'Parcels' } }
Register-Case 'layer-add-streets' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'streetsPath'); name = 'Streets' } }
Register-Case 'layer-add-boundary' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'boundaryPath'); name = 'Boundary' } }
Register-Case 'layer-add-design-sites' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'designSitesPath'); name = 'Design Sites' } }
Register-Case 'layer-set-appearance-boundary' { Invoke-Op 'layer.set-appearance' @{ map = $map; layer = 'Boundary'; transparency = 40; visible = $true } }
Register-Case 'symbology-set-simple-streets' { Invoke-Op 'symbology.set-simple' @{ map = $map; layer = 'Streets'; color = '#4A4A4A'; size = 1.5 } }
Register-Case 'symbology-set-unique-values-parcels' {
    Invoke-Op 'symbology.set-unique-values' @{ map = $map; layer = 'Parcels'; field = 'land_use_1'; classes = $landUseClasses; defaultColor = '#E6E6E6' }
}
Register-Case 'label-configure-streets' {
    Invoke-Op 'label.configure' @{ map = $map; layer = 'Streets'; enabled = $true; expression = '$feature.NAME'; fontFamily = 'Tahoma'; size = 7; color = '#333333' }
}
Register-Case 'map-activate-ops-map' { Invoke-Op 'map.activate' @{ map = $map } }
Register-Case 'view-capture-active-map' { Invoke-Op 'view.capture' @{ view = 'active-map'; width = 1280; height = 800 } } {
    param($Result)
    Assert-Capture $Result 1280 800 'active-map'
}
Register-Case 'feature-select-multifamily' {
    Invoke-Op 'feature.select' @{ map = $map; layer = 'Parcels'; where = "land_use_1 = 'multifamily'"; mode = 'new'; limit = 500 }
} {
    param($Result)
    Assert-True ([int](Get-Property $Result.Data 'selectionCount') -gt 0) 'feature.select selected no multifamily parcels.'
}
Register-Case 'map-clear-selection' { Invoke-Op 'map.clear-selection' @{ map = $map } }
Register-Case 'table-query-parcels' { Invoke-Op 'table.query' @{ map = $map; layer = 'Parcels'; fields = @('land_use_1', 'pop'); limit = 5 } } {
    param($Result)
    Assert-True ([int](Get-Property $Result.Data 'returned') -eq 5) "table.query returned $(Get-Property $Result.Data 'returned') rows, expected 5."
}

function Invoke-ParcelStatistic([string]$Field) {
    return Invoke-Op 'table.statistics' @{ map = $map; layer = 'Parcels'; field = $Field; sampleLimit = 1000000 }
}

function Assert-ParcelStatistic($Result, [string]$Field) {
    $expected = Assert-Context 'expected'
    Assert-True ([long](Get-Property $Result.Data 'matched') -eq [long]$expected.matched) "table.statistics matched $(Get-Property $Result.Data 'matched') parcels, expected $($expected.matched)."
    Assert-Close ([double](Get-Property $Result.Data 'sum')) ([double](Get-Property $expected $Field)) "Sum of $Field"
}

Register-Case 'table-statistics-pop' { Invoke-ParcelStatistic 'pop' } { param($Result) Assert-ParcelStatistic $Result 'pop' }
Register-Case 'table-statistics-area-gross' { Invoke-ParcelStatistic 'area_gross' } { param($Result) Assert-ParcelStatistic $Result 'area_gross' }
Register-Case 'table-statistics-du' { Invoke-ParcelStatistic 'du' } { param($Result) Assert-ParcelStatistic $Result 'du' }
Register-Case 'table-statistics-emp' { Invoke-ParcelStatistic 'emp' } { param($Result) Assert-ParcelStatistic $Result 'emp' }
Register-Case 'feature-query-baseline' {
    Invoke-Op 'feature.query' @{ map = $map; layer = 'Design Sites'; where = '1=1'; fields = @('Name', 'Units', 'FAR'); limit = 10 }
} {
    param($Result)
    $rows = @(Get-Property $Result.Data 'rows')
    Assert-True ($rows.Count -eq 1 -and (Get-Property $rows[0] 'Name') -eq 'Baseline Site') "Design Sites should hold exactly 'Baseline Site' (got $($rows.Count) rows)."
    # A restricted field list must still return the row identity (regression: objectId -1, globalId null).
    Assert-True ([long](Get-Property $rows[0] 'objectId') -gt 0) "feature.query returned objectId $(Get-Property $rows[0] 'objectId') for 'Baseline Site'."
    Assert-True (-not [string]::IsNullOrEmpty([string](Get-Property $rows[0] 'globalId'))) "feature.query returned no globalId for 'Baseline Site'."
    $script:ctx.baselineGlobalId = [string](Get-Property $rows[0] 'globalId')
}
Register-Case 'feature-layer-describe-design-sites' { Invoke-Op 'feature.layer.describe' @{ map = $map; layer = 'Design Sites' } } {
    param($Result)
    Assert-True ([bool](Get-Property $Result.Data 'editable')) 'Design Sites is not editable.'
    Assert-True ([bool](Get-Property $Result.Data 'globalIdField')) 'Design Sites reports no GlobalID field.'
    Assert-True ([string](Get-Property $Result.Data 'geometryType') -eq 'Polygon') "Design Sites geometry is $(Get-Property $Result.Data 'geometryType'), expected Polygon."
}
Register-Case 'metadata-get-design-sites' { Invoke-Op 'metadata.get' @{ map = $map; layer = 'Design Sites'; includeXml = $true } }
Register-Case 'layer-set-appearance-missing-layer' { Invoke-Op 'layer.set-appearance' @{ map = $map; layer = 'No Such Layer'; transparency = 10 } }
Register-Case 'symbology-set-unique-values-polyline' {
    Invoke-Op 'symbology.set-unique-values' @{ map = $map; layer = 'Streets'; field = 'NAME'; classes = @(@{ value = 'Main Street'; color = '#FF0000' }) }
}
Register-Case 'layer-set-appearance-stale-revision' { Invoke-Op 'layer.set-appearance' @{ map = $map; layer = 'Boundary'; transparency = 45 } -StaleRevision }
Register-Case 'layer-set-appearance-no-revision' { Invoke-Op 'layer.set-appearance' @{ map = $map; layer = 'Boundary'; transparency = 45 } -NoRevision }
Register-Case 'layer-add-broken-source' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'brokenPath'); name = 'Broken' } } {
    # Break the source: rename every file of the shapefile copy while the layer still points at it.
    # ArcGIS Pro can hold a file open (seen as a sharing violation); rename what can be renamed and,
    # if the .shp itself stays locked, put the renamed files back and skip the broken-layer query
    # with the reason instead of running it against a half-broken layer.
    $brokenPath = Assert-Context 'brokenPath'
    $folder = Split-Path -Parent $brokenPath
    $base = [IO.Path]::GetFileNameWithoutExtension($brokenPath)
    $moved = New-Object System.Collections.ArrayList
    $locked = New-Object System.Collections.ArrayList
    foreach ($file in @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.BaseName -eq $base })) {
        try {
            Rename-Item -LiteralPath $file.FullName -NewName ('Moved_' + $file.Name)
            $null = $moved.Add($file.Name)
        }
        catch { $null = $locked.Add("$($file.Name) ($($_.Exception.Message))") }
    }
    if (Test-Path -LiteralPath $brokenPath) {
        foreach ($name in $moved) {
            try { Rename-Item -LiteralPath (Join-Path $folder ('Moved_' + $name)) -NewName $name }
            catch { $null = $locked.Add("restoring $name failed ($($_.Exception.Message))") }
        }
        $script:ctx.brokenSkipReason = "the source files could not be renamed while ArcGIS Pro held them: $($locked -join '; ')"
        Write-Operator "  [warning] $($script:ctx.brokenSkipReason). table-query-broken-layer will be skipped." 'Yellow'
        return
    }
    if ($locked.Count -gt 0) { Write-Operator "  [note] Renamed the .shp but not: $($locked -join '; ')" 'Yellow' }
    $script:ctx.brokenRenamed = $true
}
Register-Case 'table-query-broken-layer' {
    if ($script:ctx['brokenSkipReason']) { return @{ Skipped = $script:ctx.brokenSkipReason } }
    $null = Assert-Context 'brokenRenamed'
    Invoke-Op 'table.query' @{ map = $map; layer = 'Broken'; limit = 1 }
}
Register-Case 'layer-add-repair-broken' { Invoke-Op 'layer.add' @{ map = $map; source = (Assert-Context 'boundaryPath'); name = 'Broken' } } {
    param($Result)
    Assert-True (@($Result.Notices | Where-Object { (Get-Property $_ 'code') -eq 'layer_repaired' }).Count -eq 1) 'Re-adding the broken layer did not report layer_repaired.'
}

# --- scene
Register-Case 'map-ensure-ops-scene' { Invoke-Op 'map.ensure' @{ name = $scene; type = 'scene'; basemap = 'None' } }
Register-Case 'layer-add-massing' { Invoke-Op 'layer.add' @{ map = $scene; source = (Assert-Context 'massingPath'); name = 'Proposed Massing' } }
Register-Case 'layer-set-elevation-relative' { Invoke-Op 'layer.set-elevation' @{ map = $scene; layer = 'Proposed Massing'; mode = 'relative-to-ground' } }
Register-Case 'layer-set-elevation-invalid-mode' { Invoke-Op 'layer.set-elevation' @{ map = $scene; layer = 'Proposed Massing'; mode = 'floating' } }

# --- layout
Register-Case 'layout-ensure-ops-layout' { Invoke-Op 'layout.ensure' @{ name = $layout; width = 17; height = 11 } }
Register-Case 'layout-add-map-frame' { Invoke-Op 'layout.add-map-frame' @{ layout = $layout; map = $map; name = $frame; x = 0.5; y = 0.5; width = 12; height = 10 } }
Register-Case 'layout-set-frame-extent-parcels' { Invoke-Op 'layout.set-frame-extent' @{ layout = $layout; frame = $frame; layer = 'Parcels'; padding = 2 } }
Register-Case 'layout-set-text-title' {
    Invoke-Op 'layout.set-text' @{ layout = $layout; name = 'Title'; text = 'OPS MATRIX'; x = 12.9; y = 9.9; fontFamily = 'Tahoma'; fontStyle = 'Bold'; size = 22; color = '#1F2D3D' }
}
Register-Case 'layout-set-text-dynamic' {
    Invoke-Op 'layout.set-text' @{ layout = $layout; name = 'Dynamic Date'; text = 'Printed <dyn type="date" format="yyyy-MM-dd"/>'; x = 12.9; y = 9.4; size = 9 }
}
Register-Case 'layout-ensure-surround-legend' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'Legend'; kind = 'legend'; x = 12.9; y = 3.0; width = 3.6; height = 5.6 }
}
Register-Case 'layout-ensure-surround-north-arrow' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'North Arrow'; kind = 'north-arrow'; x = 15.7; y = 0.9; width = 0.7; height = 1.1 }
}
Register-Case 'layout-ensure-surround-scale-bar' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'Scale Bar'; kind = 'scale-bar'; x = 12.9; y = 0.9; width = 2.5; height = 0.5 }
} {
    # Anchored where requested; the style decides the height, and a size that differs is reported.
    param($Result)
    $bounds = Get-Property $Result.Data 'bounds'
    Assert-True ([Math]::Abs([double](Get-Property $bounds 'x') - 12.9) -le 0.02 -and [Math]::Abs([double](Get-Property $bounds 'y') - 0.9) -le 0.02) "Scale bar is anchored at $(Get-Property $bounds 'x'), $(Get-Property $bounds 'y'), expected 12.9, 0.9."
    $resized = [Math]::Abs([double](Get-Property $bounds 'width') - 2.5) -gt 0.02 -or [Math]::Abs([double](Get-Property $bounds 'height') - 0.5) -gt 0.02
    $notices = @($Result.Notices | Where-Object { (Get-Property $_ 'code') -eq 'surround_resized' }).Count
    Assert-True ($notices -eq [int]$resized) "Scale bar bounds $(Get-Property $bounds 'width') x $(Get-Property $bounds 'height') with $notices surround_resized notice(s)."
}
Register-Case 'layout-inspect-overlaps' { Invoke-Op 'layout.inspect' @{ layout = $layout } } {
    param($Result)
    $elements = @(Get-Property $Result.Data 'elements')
    $find = {
        param([string]$Name)
        $match = @($elements | Where-Object { (Get-Property $_ 'name') -eq $Name })
        if ($match.Count -ne 1) { throw "layout.inspect lists $($match.Count) elements named '$Name', expected 1." }
        return $match[0].bounds
    }
    $mapFrame = & $find $frame
    $legend = & $find 'Legend'
    $scaleBar = & $find 'Scale Bar'
    $northArrow = & $find 'North Arrow'
    Assert-True (-not (Test-BoxOverlap $legend $scaleBar)) 'Legend overlaps the scale bar.'
    Assert-True (-not (Test-BoxOverlap $legend $northArrow)) 'Legend overlaps the north arrow.'
    $floor = [double]$mapFrame.y + 0.35
    Assert-True ([double]$legend.y -ge $floor) "Legend bottom $($legend.y) is below frame bottom + 0.35 ($floor)."
    Assert-True ([double]$scaleBar.y -ge $floor) "Scale bar bottom $($scaleBar.y) is below frame bottom + 0.35 ($floor)."
}
Register-Case 'layout-activate' { Invoke-Op 'layout.activate' @{ layout = $layout } }
Register-Case 'view-capture-layout' { Invoke-Op 'view.capture' @{ view = 'layout'; layout = $layout; width = 1700; height = 1100 } } {
    param($Result)
    Assert-Capture $Result 1700 1100 'layout'
}
Register-Case 'layout-add-map-frame-off-page' {
    Invoke-Op 'layout.add-map-frame' @{ layout = $layout; map = $map; name = 'Off Page Frame'; x = 40; y = 40; width = 5; height = 5 }
}
Register-Case 'layout-ensure-surround-off-page' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'Off Page Bar'; kind = 'scale-bar'; x = 16; y = 1; width = 2.5; height = 0.5 }
}
Register-Case 'layout-ensure-surround-missing-frame' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = 'No Such Frame'; name = 'Orphan Arrow'; kind = 'north-arrow'; x = 14; y = 1; width = 0.7; height = 1.1 }
}
Register-Case 'layout-inspect-missing-layout' { Invoke-Op 'layout.inspect' @{ layout = 'No Such Layout' } }
Register-Case 'layout-ensure-surround-unknown-kind' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'Compass'; kind = 'compass'; x = 14; y = 1; width = 1; height = 1 }
}
Register-Case 'layout-ensure-surround-zero-width' {
    Invoke-Op 'layout.ensure-surround' @{ layout = $layout; frame = $frame; name = 'Flat Bar'; kind = 'scale-bar'; x = 13; y = 1; width = 0; height = 0.5 }
}

# --- geoprocessing without review
Register-Case 'gp-query-parcel-count' { Invoke-Op 'gp.query' @{ tool = 'management.GetCount'; parameters = @((Assert-Context 'parcelsPath')) } } {
    param($Result)
    $expected = Assert-Context 'expectedParcelCount'
    Assert-True ([long](Get-Property $Result.Data 'returnValue') -eq [long]$expected) "GetCount returned $(Get-Property $Result.Data 'returnValue'), expected $expected."
}
Register-Case 'gp-query-not-allowed' {
    Invoke-Op 'gp.query' @{ tool = 'analysis.Buffer'; parameters = @((Assert-Context 'designSitesPath'), (Assert-Context 'bufferOutputPath'), '50 Feet') }
}
Register-Case 'gp-query-invalid-name' { Invoke-Op 'gp.query' @{ tool = 'Buffer'; parameters = @() } }
Register-Case 'gp-run-dry-run-valid' { Invoke-Op 'gp.run' (Get-BufferArgument) -DryRun } {
    param($Result)
    Assert-True ([bool](Get-Property $Result.Data 'valid')) "Dry run of a valid Buffer reported valid: false ($((Get-Property $Result.Data 'issues') | ConvertTo-Json -Compress -Depth 5))."
    Assert-True ([bool](Get-Property $Result.Data 'requiresConfirmation')) 'Dry run of gp.run did not report requiresConfirmation.'
}
Register-Case 'gp-run-dry-run-invalid-line-side' {
    $arguments = Get-BufferArgument
    $arguments.parameters = @((Assert-Context 'designSitesPath'), (Assert-Context 'bufferOutputPath'), '50 Feet', 'SIDEWAYS')
    Invoke-Op 'gp.run' $arguments -DryRun
} {
    param($Result)
    $codes = @(@(Get-Property $Result.Data 'issues') | ForEach-Object { [string](Get-Property $_ 'code') })
    Assert-True ('invalid_coded_value' -in $codes) "Dry run issues are '$($codes -join ', ')', expected invalid_coded_value."
}
Register-Case 'gp-run-dry-run-idempotency-conflict' { Invoke-Op 'gp.run' (Get-BufferArgument) -DryRun -IdempotencyKey "ops-matrix-dry-run-$stamp" }

# --- ArcPy inspection
Register-Case 'arcpy-inspect-hello' { Invoke-Op 'arcpy.inspect-script' @{ scriptPath = 'hello.py' } } {
    param($Result)
    $hash = [string](Get-Property $Result.Data 'sha256')
    Assert-True ($hash -match '^[0-9A-Fa-f]{64}$') 'arcpy.inspect-script returned no SHA-256.'
    $script:ctx.helloSha256 = $hash
}
Register-Case 'arcpy-inspect-escape' { Invoke-Op 'arcpy.inspect-script' @{ scriptPath = '..\outside.py' } }

# --- feature create
Register-Case 'feature-create-site' {
    Invoke-Op 'feature.create' @{
        map = $map; layer = 'Design Sites'
        attributes = @{ Name = $siteName; Units = 120; FAR = 4.75; ReviewDate = '2026-09-08T15:30:00Z' }
        geometry = @{ type = 'polygon'; coordinates = $siteRing }
    }
} {
    param($Result)
    $globalId = [string](Get-Property $Result.Data 'globalId')
    Assert-True ([bool]$globalId) 'feature.create returned no GlobalID.'
    $readBack = Invoke-Op 'feature.query' @{ map = $map; layer = 'Design Sites'; where = "Name = '$siteName'"; fields = @('Name', 'Units'); limit = 10 } -Label 'feature.query-feature-create-site-readback'
    $rows = @(Get-Property $readBack.Data 'rows')
    Assert-True ($readBack.Success -and $rows.Count -eq 1) "Created feature did not read back exactly once ($($rows.Count) rows)."
    Assert-True ([string]::Equals([string](Get-Property $rows[0] 'globalId'), $globalId, [StringComparison]::OrdinalIgnoreCase)) 'Created feature reads back with a different GlobalID.'
    $script:ctx.createdGlobalId = $globalId
}
Register-Case 'feature-create-point-geometry' {
    Invoke-Op 'feature.create' @{ map = $map; layer = 'Design Sites'; attributes = @{ Name = 'Must Not Exist' }; geometry = @{ type = 'point'; x = 1366300; y = 416800 } }
}
Register-Case 'feature-create-two-vertices' {
    Invoke-Op 'feature.create' @{ map = $map; layer = 'Design Sites'; attributes = @{ Name = 'Must Not Exist' }; geometry = @{ type = 'polygon'; coordinates = @(@(1366300.0, 416800.0), @(1366310.0, 416810.0)) } }
}
Register-Case 'feature-create-unknown-field' {
    Invoke-Op 'feature.create' @{ map = $map; layer = 'Design Sites'; attributes = @{ Name = 'Must Not Exist'; NoSuchField = 1 }; geometry = @{ type = 'polygon'; coordinates = $siteRing } }
}

# --- MCP gateway pass
Register-Case 'gateway-project-get' {
    $state = Invoke-GatewayTool 'system_get_state' @{} -Label 'system_get_state'
    Assert-True $state.Success "Gateway system_get_state failed: $($state.ErrorCode) $($state.Message)"
    Assert-True ([int](Get-Property $state.Data 'processId') -eq [int]$script:hostProcessId) "Gateway answered from PID $(Get-Property $state.Data 'processId'), not the host PID $($script:hostProcessId)."
    Invoke-GatewayTool 'registry_invoke' @{ operationId = 'project.get'; arguments = @{} }
}
Register-Case 'gateway-gp-run-dry-run' { Invoke-GatewayTool 'registry_invoke' @{ operationId = 'gp.run'; arguments = (Get-BufferArgument); dryRun = $true } } {
    param($Result)
    Assert-True ([bool](Get-Property $Result.Data 'valid')) 'Gateway dry run of a valid Buffer reported valid: false.'
}

# --- gated operations without a token (current revision, valid arguments: only the token is missing)
Register-Case 'gp-run-without-token' { Invoke-Op 'gp.run' (Get-BufferArgument) }
Register-Case 'feature-update-without-token' {
    Invoke-Op 'feature.update' @{ map = $map; layer = 'Design Sites'; target = @{ globalId = (Assert-Context 'createdGlobalId') }; attributes = @{ Units = 999 } }
}
Register-Case 'metadata-update-without-token' { Invoke-Op 'metadata.update' (Get-MetadataArgument) }
Register-Case 'arcpy-run-without-token' {
    Invoke-Op 'arcpy.run-script' @{ scriptPath = 'hello.py'; scriptSha256 = (Assert-Context 'helloSha256'); arguments = @('without-token'); timeoutSeconds = 60 }
}
Register-Case 'feature-delete-without-token' {
    Invoke-Op 'feature.delete' @{ map = $map; layer = 'Design Sites'; target = @{ globalId = (Assert-Context 'createdGlobalId') } }
}
Register-Case 'project-save-without-token' { Invoke-Op 'project.save' @{} }
Register-Case 'project-open-without-token' { Invoke-Op 'project.open' @{ path = (Assert-Context 'reopenPath') } }

# --- approval cards
Register-Case 'gp-run-buffer-approved' { @{ OperationId = 'gp.run'; Arguments = (Get-BufferArgument) } } {
    param($Result)
    Assert-True (-not [bool](Get-Property $Result.Data 'isFailed')) 'Approved Buffer reported isFailed.'
    $count = Invoke-Op 'gp.query' @{ tool = 'management.GetCount'; parameters = @((Assert-Context 'bufferOutputPath')) } -Label 'gp.query-buffer-output-count'
    Assert-True ($count.Success -and [long](Get-Property $count.Data 'returnValue') -eq 2) "Buffer output count is $(Get-Property $count.Data 'returnValue') ($($count.ErrorCode)), expected 2."
}
Register-Case 'feature-update-approved' {
    @{ OperationId = 'feature.update'; Arguments = @{ map = $map; layer = 'Design Sites'; target = @{ globalId = (Assert-Context 'createdGlobalId') }; attributes = @{ Units = 140 } } }
} {
    $readBack = Invoke-Op 'feature.query' @{ map = $map; layer = 'Design Sites'; where = "Name = '$siteName'"; fields = @('Units'); limit = 10 } -Label 'feature.query-feature-update-readback'
    $rows = @(Get-Property $readBack.Data 'rows')
    Assert-True ($rows.Count -eq 1 -and [int](Get-Property $rows[0] 'Units') -eq 140) 'Updated Units did not read back as 140.'
}
Register-Case 'metadata-update-approved' { @{ OperationId = 'metadata.update'; Arguments = (Get-MetadataArgument) } } {
    $readBack = Invoke-Op 'metadata.get' @{ map = $map; layer = 'Design Sites'; includeXml = $true } -Label 'metadata.get-metadata-update-readback'
    $metadata = Get-Property $readBack.Data 'metadata'
    Assert-True ((Get-Property $metadata 'title') -eq 'Ops Matrix Design Sites') "Metadata title reads back as '$(Get-Property $metadata 'title')'."
    Assert-True ('ops-matrix' -in @(Get-Property $metadata 'tags')) 'Metadata tags did not read back.'
}
Register-Case 'arcpy-run-hello-approved' {
    @{ OperationId = 'arcpy.run-script'; Arguments = @{ scriptPath = 'hello.py'; scriptSha256 = (Assert-Context 'helloSha256'); arguments = @('ops-matrix'); timeoutSeconds = 60 } }
} {
    param($Result)
    Assert-True ([int](Get-Property $Result.Data 'exitCode') -eq 0) "hello.py exited with $(Get-Property $Result.Data 'exitCode')."
    $probe = ([string](Get-Property $Result.Data 'stdout')).Trim() | ConvertFrom-Json
    Assert-True ([bool](Get-Property $probe 'arcgisVersion')) 'hello.py printed no ArcGIS version.'
    Assert-True ('ops-matrix' -in @(Get-Property $probe 'arguments')) 'hello.py did not receive its argument.'
}
Register-Case 'feature-delete-approved' {
    @{ OperationId = 'feature.delete'; Arguments = @{ map = $map; layer = 'Design Sites'; target = @{ globalId = (Assert-Context 'createdGlobalId') } } }
} {
    $readBack = Invoke-Op 'feature.query' @{ map = $map; layer = 'Design Sites'; where = '1=1'; fields = @('Name'); limit = 10 } -Label 'feature.query-feature-delete-readback'
    $rows = @(Get-Property $readBack.Data 'rows')
    Assert-True ($rows.Count -eq 1 -and (Get-Property $rows[0] 'Name') -eq 'Baseline Site') "After the delete Design Sites holds $($rows.Count) rows, expected only 'Baseline Site'."
}
Register-Case 'feature-delete-denied' {
    @{ OperationId = 'feature.delete'; Arguments = @{ map = $map; layer = 'Design Sites'; target = @{ globalId = (Assert-Context 'baselineGlobalId') } } }
} {
    $readBack = Invoke-Op 'feature.query' @{ map = $map; layer = 'Design Sites'; where = "Name = 'Baseline Site'"; fields = @('Name'); limit = 10 } -Label 'feature.query-feature-delete-denied-readback'
    Assert-True (@(Get-Property $readBack.Data 'rows').Count -eq 1) 'The baseline row is gone after a denied delete.'
}
Register-Case 'project-save-approved' { @{ OperationId = 'project.save'; Arguments = @{} } } {
    $projectPath = Assert-Context 'projectPath'
    $reopenPath = Assert-Context 'reopenPath'
    Copy-Item -LiteralPath $projectPath -Destination $reopenPath
    Assert-True (Test-Path -LiteralPath $reopenPath -PathType Leaf) "Could not copy the saved project to $reopenPath."
}
Register-Case 'project-open-approved' { @{ OperationId = 'project.open'; Arguments = @{ path = (Assert-Context 'reopenPath') } } } {
    # ArcGIS reports the project name with its .aprx extension; compare file names without it.
    $expected = [IO.Path]::GetFileNameWithoutExtension((Assert-Context 'reopenPath'))
    $after = Invoke-Op 'project.get' @{} -Label 'project.get-after-project-open'
    $name = [IO.Path]::GetFileNameWithoutExtension([string](Get-Property (Get-Property $after.Data 'project') 'name'))
    Assert-True ($after.Success -and $name -eq $expected) "project.get reports '$name' after project.open, expected '$expected'."
}

# ---------------------------------------------------------------- plan checks (no host needed)

function Get-PlanProblem($Plan, $Descriptors) {
    $problems = New-Object System.Collections.ArrayList
    $ids = @($Descriptors | ForEach-Object { $_.Id })
    $keys = @($Plan.Cases | ForEach-Object { $_.Key })
    foreach ($duplicate in @($keys | Group-Object | Where-Object { $_.Count -gt 1 })) { $null = $problems.Add("case key '$($duplicate.Name)' appears $($duplicate.Count) times") }
    foreach ($case in $Plan.Cases) {
        if ($case.Operation -notin $ids) { $null = $problems.Add("case '$($case.Key)' names unknown operation '$($case.Operation)'") }
        if ($case.Kind -notin @('happy', 'negative')) { $null = $problems.Add("case '$($case.Key)' has kind '$($case.Kind)'") }
        if (-not $script:implementations.ContainsKey($case.Key)) { $null = $problems.Add("case '$($case.Key)' has no Register-Case block") }
    }
    foreach ($key in @($script:implementations.Keys)) { if ($key -notin $keys) { $null = $problems.Add("Register-Case '$key' is not in the plan") } }
    foreach ($descriptor in $Descriptors) {
        $happy = @($Plan.Cases | Where-Object { $_.Operation -eq $descriptor.Id -and $_.Kind -eq 'happy' })
        if ($happy.Count -eq 0) { $null = $problems.Add("operation '$($descriptor.Id)' has no happy-path case") }
        if ($descriptor.RequiresConfirmation) {
            if (@($happy | Where-Object { $_.Card -eq 'approve' }).Count -eq 0) { $null = $problems.Add("gated operation '$($descriptor.Id)' has no approve card") }
            if (@($Plan.Cases | Where-Object { $_.Operation -eq $descriptor.Id -and -not $_.Card -and $_.ExpectErrorCode -eq 'confirmation_required' }).Count -eq 0) {
                $null = $problems.Add("gated operation '$($descriptor.Id)' has no no-token confirmation_required case")
            }
        }
    }
    return @($problems)
}

function Show-Plan($Plan, $Descriptors) {
    $index = 0
    $rows = foreach ($case in $Plan.Cases) {
        $index++
        $expect = if ($case.Kind -eq 'happy') { 'success' }
        elseif ($case.ExpectErrorCode) { $case.ExpectErrorCode }
        elseif ($null -ne $case.ExpectDryRunValid) { "dry run valid=$($case.ExpectDryRunValid)" }
        else { 'failure' }
        if ($case.ExpectMessageContains.Count -gt 0) { $expect += " (names '$($case.ExpectMessageContains -join "', '")')" }
        [pscustomobject]@{
            '#' = $index; Phase = $case.Phase; Case = $case.Key; Operation = $case.Operation; Kind = $case.Kind
            Expect = $expect; Card = $case.Card; Via = $case.Via; Requires = ($case.Requires -join ',')
        }
    }
    Write-Host ''
    Write-Host 'Ordered operation matrix (tools/live-operations-plan.json)' -ForegroundColor Cyan
    $rows | Format-Table -AutoSize | Out-String -Width 260 | Write-Host
    $cards = @($Plan.Cases | Where-Object { $_.Card })
    Write-Host "Approval cards ($($cards.Count); the operator clicks in the MCP Studio dockpane):" -ForegroundColor Cyan
    $number = 0
    foreach ($card in $cards) {
        $number++
        $action = if ($card.Card -eq 'deny') { 'DENY' } else { 'APPROVE ONCE' }
        $extra = @()
        if ($card.Requires.Count -gt 0) { $extra += "only with $($card.Requires -join ', ')" }
        if ($card.Via -eq 'gateway') { $extra += 'through the MCP gateway' }
        $suffix = if ($extra.Count -gt 0) { " [$($extra -join '; ')]" } else { '' }
        Write-Host ("  Card {0}/{1}: {2} on {3} -> click {4}{5}" -f $number, $cards.Count, $card.Operation, $card.Target, $action, $suffix)
    }
    $covered = @($Descriptors | Where-Object { $id = $_.Id; @($Plan.Cases | Where-Object { $_.Operation -eq $id -and $_.Kind -eq 'happy' }).Count -gt 0 })
    Write-Host ''
    Write-Host ("Cases: {0} ({1} happy, {2} negative); operations with a happy case: {3}/{4}; cards: {5} approve + {6} deny." -f `
            $Plan.Cases.Count, @($Plan.Cases | Where-Object Kind -eq 'happy').Count, @($Plan.Cases | Where-Object Kind -eq 'negative').Count,
            $covered.Count, $Descriptors.Count, @($cards | Where-Object Card -eq 'approve').Count, @($cards | Where-Object Card -eq 'deny').Count) -ForegroundColor Cyan
}

$plan = Read-Plan
$descriptors = Get-DescriptorInfo
$planProblems = @(Get-PlanProblem $plan $descriptors)

if ($PlanOnly) {
    Show-Plan $plan $descriptors
    if ($planProblems.Count -gt 0) {
        foreach ($problem in $planProblems) { Write-Host "  PLAN PROBLEM: $problem" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'Plan only: the plan matches every operation descriptor and every case is implemented. Nothing was contacted or written.' -ForegroundColor Cyan
    return
}
if ($planProblems.Count -gt 0) { throw "The operation plan is inconsistent: $($planProblems -join '; ')" }

# ---------------------------------------------------------------- live run: calls

foreach ($path in @($runnerPath, $gatewayPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Not built: $path (dotnet build -c $Configuration)." }
}
if (-not $DisposableRoot) { throw 'Pass -DisposableRoot: the folder that contains the disposable .aprx; test data is copied there.' }
$DisposableRoot = [IO.Path]::GetFullPath($DisposableRoot)
if (-not (Test-Path -LiteralPath $DisposableRoot -PathType Container)) { throw "Disposable root does not exist: $DisposableRoot" }
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $repoRoot ('artifacts\live-operations\' + $stamp) }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
$resultsDirectory = Join-Path $EvidenceDirectory 'results'
$script:captureDirectory = Join-Path $EvidenceDirectory 'captures'
$null = New-Item -ItemType Directory -Path $resultsDirectory -Force
$null = New-Item -ItemType Directory -Path $script:captureDirectory -Force
$previousPipe = $env:ARCGIS_PRO_MCP_PIPE
if ($PipeName) { $env:ARCGIS_PRO_MCP_PIPE = $PipeName }
$script:sequence = 0
$script:gateway = $null
$script:ctx = @{}
$script:hostProcessId = $null

function Get-SafeFileLabel([string]$Label) { return ($Label -replace '[^A-Za-z0-9._-]', '_') }

function ConvertTo-CallResult($Raw) {
    <# Normalises an OperationResult, a bridge error ({ success: false, error }) or any other bridge reply. #>
    $errorObject = Get-Property $Raw 'error'
    $hasSuccess = $null -ne $Raw -and $Raw -isnot [array] -and $null -ne $Raw.PSObject.Properties['success']
    $success = if ($errorObject) { $false } elseif ($hasSuccess) { [bool]$Raw.success } else { $true }
    $code = [string](Get-Property $Raw 'errorCode')
    $message = [string](Get-Property $Raw 'message')
    if ($errorObject) { $code = [string](Get-Property $errorObject 'code'); $message = [string](Get-Property $errorObject 'message') }
    $data = if ($hasSuccess) { Get-Property $Raw 'data' } else { $Raw }
    return [pscustomobject]@{
        Success = $success
        ErrorCode = $(if ($code) { $code } else { $null })
        Message = $(if ($message) { $message } else { $null })
        Data = $data
        Notices = @(Get-Property $Raw 'notices' | Where-Object { $null -ne $_ })
        Revision = [string](Get-Property $Raw 'workspaceRevision')
    }
}

function Invoke-Bridge {
    param([string]$Label, [string]$Method, $Parameters, [int]$TimeoutMilliseconds = 300000)
    $script:sequence++
    $prefix = '{0:D3}-{1}' -f $script:sequence, (Get-SafeFileLabel $Label)
    $requestPath = Join-Path $resultsDirectory "$prefix.request.json"
    $resultPath = Join-Path $resultsDirectory "$prefix.result.json"
    Write-Utf8File $requestPath (ConvertTo-JsonText ([ordered]@{ method = $Method; parameters = $Parameters }))
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& dotnet $runnerPath --call-timeout $TimeoutMilliseconds $requestPath $resultPath 2>&1 | ForEach-Object { [string]$_ }) }
    finally { $ErrorActionPreference = $previous }
    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        # The runner failed before it had a reply (no host, bad pipe): keep its own error JSON as the result.
        $errorLine = @($output | Where-Object { $_ -match '^\{.*"error"' } | Select-Object -Last 1)
        $text = if ($errorLine.Count -gt 0) { $errorLine[0] } else { ConvertTo-JsonText ([ordered]@{ success = $false; error = [ordered]@{ code = 'runner_failed'; message = ($output -join ' ') } }) }
        Write-Utf8File $resultPath $text
    }
    return ConvertTo-CallResult (Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json)
}

function Invoke-Op {
    param(
        [string]$OperationId,
        $Arguments,
        [switch]$StaleRevision,
        [switch]$NoRevision,
        [switch]$DryRun,
        [string]$IdempotencyKey,
        [string]$ConfirmationToken,
        [string]$Label
    )
    $parameters = [ordered]@{ operationId = $OperationId; arguments = $Arguments }
    # Without expectedRevision the DemoRunner fills in the current revision, as a client would.
    if ($StaleRevision) { $parameters.expectedRevision = 'stale-ops-matrix-revision' }
    elseif ($NoRevision) { $parameters.expectedRevision = $null }
    if ($DryRun) { $parameters.dryRun = $true }
    if ($IdempotencyKey) { $parameters.idempotencyKey = $IdempotencyKey }
    if ($ConfirmationToken) { $parameters.confirmationToken = $ConfirmationToken }
    if (-not $Label) { $Label = "$OperationId-$($script:currentCase)" }
    return Invoke-Bridge -Label $Label -Method 'registry.invoke' -Parameters $parameters
}

# ---------------------------------------------------------------- live run: MCP gateway

function Read-LineWithin($Reader, [int]$Seconds) {
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait([TimeSpan]::FromSeconds($Seconds))) {
        # The pending read cannot be cancelled and a late reply would be read by the next call, so the
        # gateway is unusable: kill it. The next gateway call starts a fresh one.
        Stop-Gateway
        throw "Timed out after $Seconds s waiting for the gateway; the gateway process was stopped."
    }
    return $task.Result
}

function Stop-Gateway {
    $gateway = $script:gateway
    $script:gateway = $null
    if ($null -eq $gateway) { return }
    try { if (-not $gateway.Process.HasExited) { $gateway.Process.Kill() } }
    catch { Write-Verbose "Could not stop the gateway process: $($_.Exception.Message)" }
}

function Open-Gateway {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'dotnet'
    $start.Arguments = '"' + $gatewayPath + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    # Select exactly the host this run talks to: the pipe, never automatic discovery.
    foreach ($name in @('ARCGIS_PRO_MCP_HOST_PID', 'ARCGIS_PRO_MCP_ALLOW_FAKEHOST')) { $start.EnvironmentVariables.Remove($name) }
    $start.EnvironmentVariables['ARCGIS_PRO_MCP_PIPE'] = $script:pipe
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $null = $process.Start()
    $gateway = [pscustomobject]@{ Process = $process; Errors = $process.StandardError.ReadToEndAsync(); NextId = 0 }
    $script:gateway = $gateway
    $init = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'ops-matrix'; version = '1.0' } } 60
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    Write-Operator "Gateway: $($init.serverInfo.name) $($init.serverInfo.version), protocol $($init.protocolVersion)"
}

function Close-Gateway {
    $gateway = $script:gateway
    if ($null -eq $gateway) { return }
    $script:gateway = $null
    if ($gateway.Process.HasExited) { return }
    try { $gateway.Process.StandardInput.Close() } catch { Write-Verbose $_ }
    if (-not $gateway.Process.WaitForExit(10000)) { $gateway.Process.Kill() }
}

function Invoke-Mcp([string]$Method, $Parameters, [int]$TimeoutSeconds) {
    $gateway = $script:gateway
    $gateway.NextId++
    $id = $gateway.NextId
    $json = @{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 40 -Compress
    $gateway.Process.StandardInput.WriteLine($json)
    $gateway.Process.StandardInput.Flush()
    while ($true) {
        $line = Read-LineWithin $gateway.Process.StandardOutput $TimeoutSeconds
        if ($null -eq $line) { throw "The gateway closed stdout: $($gateway.Errors.Result)" }
        $reply = $line | ConvertFrom-Json
        if ($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
            if ($reply.PSObject.Properties['error'] -and $reply.error) { throw ('Gateway JSON-RPC error: ' + ($reply.error | ConvertTo-Json -Compress)) }
            return $reply.result
        }
    }
}

function Invoke-GatewayTool {
    param([string]$Name, $Arguments, [string]$Label, [int]$TimeoutSeconds = 150)
    if ($null -eq $script:gateway) { Open-Gateway }
    if (-not $Label) { $Label = "$Name-$($script:currentCase)" }
    $script:sequence++
    $prefix = '{0:D3}-gateway-{1}' -f $script:sequence, (Get-SafeFileLabel $Label)
    Write-Utf8File (Join-Path $resultsDirectory "$prefix.request.json") (ConvertTo-JsonText ([ordered]@{ tool = $Name; arguments = $Arguments }))
    $reply = Invoke-Mcp 'tools/call' @{ name = $Name; arguments = $Arguments } $TimeoutSeconds
    $envelope = $reply.structuredContent
    Write-Utf8File (Join-Path $resultsDirectory "$prefix.result.json") (ConvertTo-JsonText $envelope)
    if (-not $envelope) { throw "Gateway tool $Name returned no structuredContent." }
    $inner = Get-Property $envelope 'result'
    if ($envelope.ok) { return ConvertTo-CallResult $inner }
    $errorObject = Get-Property $envelope 'error'
    $failure = ConvertTo-CallResult $inner
    $failure.Success = $false
    $failure.ErrorCode = [string](Get-Property $errorObject 'code')
    $failure.Message = [string](Get-Property $errorObject 'message')
    return $failure
}

# ---------------------------------------------------------------- live run: approval cards

# Per case: requested (a card case that asked for a card) and exactly one of approved, denied,
# approvedDenyCard (the operator approved the DENY card; its token was cancelled and the case
# failed), requestFailed (the approval request itself was refused, so no card was shown) or
# skipped (no decision). Per request: queued (every approval request sent, including re-queues
# after expiry or a refused token) and expired.
$script:approvalCounts = [ordered]@{ requested = 0; queued = 0; approved = 0; denied = 0; approvedDenyCard = 0; requestFailed = 0; expired = 0; skipped = 0 }
$script:cardNumber = 0
$script:cardTotal = 0

function Request-Approval($Spec, [string]$Via) {
    if ($Via -eq 'gateway') {
        $state = Invoke-GatewayTool 'system_get_state' @{} -Label "system_get_state-$($script:currentCase)"
        if (-not $state.Success) { return $state }
        $revision = [string](Get-Property (Get-Property $state.Data 'workspace') 'revision')
        return Invoke-GatewayTool 'approval_request' @{ operationId = $Spec.OperationId; arguments = $Spec.Arguments; expectedRevision = $revision }
    }
    return Invoke-Bridge -Label "approval.request-$($script:currentCase)" -Method 'approval.request' -Parameters ([ordered]@{ operationId = $Spec.OperationId; arguments = $Spec.Arguments })
}

function Get-ApprovalStatus([string]$RequestId, [int]$WaitSeconds, [string]$Via) {
    if ($Via -eq 'gateway') { return Invoke-GatewayTool 'approval_status' @{ requestId = $RequestId; waitSeconds = $WaitSeconds } -TimeoutSeconds ($WaitSeconds + 30) }
    return Invoke-Bridge -Label "approval.status-$($script:currentCase)" -Method 'approval.status' -Parameters ([ordered]@{ requestId = $RequestId; waitSeconds = $WaitSeconds }) -TimeoutMilliseconds (($WaitSeconds + 30) * 1000)
}

function Undo-Approval([string]$RequestId, [string]$Via) {
    if ($Via -eq 'gateway') { $null = Invoke-GatewayTool 'approval_cancel' @{ requestId = $RequestId } }
    else { $null = Invoke-Bridge -Label "approval.cancel-$($script:currentCase)" -Method 'approval.cancel' -Parameters ([ordered]@{ requestId = $RequestId }) }
}

function Write-CardPrompt($Case, [string]$Operation, [int]$Attempt) {
    $line = '=' * 78
    $action = if ($Case.Card -eq 'deny') { 'click DENY (do NOT approve this one)' } else { 'click APPROVE ONCE' }
    $again = if ($Attempt -gt 1) { " (queued again, attempt $Attempt)" } else { '' }
    $color = if ($Case.Card -eq 'deny') { 'Magenta' } else { 'Yellow' }
    try { [Console]::Beep(880, 250) } catch { Write-Verbose 'No console beep available.' }
    Write-Operator ''
    Write-Operator $line $color
    Write-Operator ("Card {0}/{1}: {2} on {3}{4}" -f $script:cardNumber, $script:cardTotal, $Operation, $Case.Target, $again) $color
    Write-Operator ("  -> {0} in the MCP Studio dockpane (ArcGIS Pro: Add-In tab > MCP Studio, pane 'ArcGIS MCP')." -f $action) $color
    Write-Operator ("  Check that the card shows '{0}' and the details above. It expires after 2 minutes;" -f $Operation) $color
    Write-Operator ("  this run re-queues an expired card and gives up after {0} minutes (recorded as skipped by the operator)." -f [int]($operatorTimeout / 60)) $color
    Write-Operator $line $color
}

function Wait-OperatorDecision($Case, $Spec) {
    <# Queues the card (again after expiry) until the operator decides or the overall wait elapses. #>
    $deadline = (Get-Date).AddSeconds($operatorTimeout)
    $attempt = 0
    while ($true) {
        $attempt++
        $request = Request-Approval $Spec $Case.Via
        $script:approvalCounts.queued++
        if (-not $request.Success) {
            Write-Operator "  approval request for Card $($script:cardNumber)/$($script:cardTotal) was refused ($($request.ErrorCode)); no card is waiting." 'Red'
            return [pscustomobject]@{ Status = 'request-failed'; Token = $null; RequestId = $null; Result = $request }
        }
        $requestId = [string](Get-Property $request.Data 'requestId')
        $status = [string](Get-Property $request.Data 'status')
        Write-CardPrompt $Case $Spec.OperationId $attempt
        $reply = $request
        while ($status -eq 'pending') {
            $remaining = [int][Math]::Floor(($deadline - (Get-Date)).TotalSeconds)
            if ($remaining -le 0) {
                Undo-Approval $requestId $Case.Via
                return [pscustomobject]@{ Status = 'timeout'; Token = $null; RequestId = $requestId }
            }
            $reply = Get-ApprovalStatus $requestId ([Math]::Min(120, $remaining)) $Case.Via
            if (-not $reply.Success) { throw "approval status failed: $($reply.ErrorCode): $($reply.Message)" }
            $status = [string](Get-Property $reply.Data 'status')
            if ($status -eq 'pending') {
                if (Get-Property $reply.Data 'waitNotice') { Start-Sleep -Seconds 2 }
                Write-Operator ("  ... still waiting for Card {0}/{1} ({2} s left before it is skipped)" -f $script:cardNumber, $script:cardTotal, [int][Math]::Max(0, ($deadline - (Get-Date)).TotalSeconds))
            }
        }
        if ($status -eq 'expired' -and (Get-Date) -lt $deadline) {
            $script:approvalCounts.expired++
            Write-Operator "  Card $($script:cardNumber)/$($script:cardTotal) expired before a decision; queuing it again." 'Yellow'
            continue
        }
        return [pscustomobject]@{ Status = $status; Token = [string](Get-Property $reply.Data 'confirmationToken'); RequestId = $requestId }
    }
}

function Invoke-WithToken($Spec, [string]$Token, [string]$Via) {
    if ($Via -eq 'gateway') {
        $state = Invoke-GatewayTool 'system_get_state' @{} -Label "system_get_state-$($script:currentCase)-invoke"
        $revision = [string](Get-Property (Get-Property $state.Data 'workspace') 'revision')
        $arguments = @{ operationId = $Spec.OperationId; arguments = $Spec.Arguments; expectedRevision = $revision }
        if ($Token) { $arguments.confirmationToken = $Token }
        return Invoke-GatewayTool 'registry_invoke' $arguments
    }
    if ($Token) { return Invoke-Op $Spec.OperationId $Spec.Arguments -ConfirmationToken $Token }
    return Invoke-Op $Spec.OperationId $Spec.Arguments
}

function Invoke-CardCase($Case, $Implementation, $Record) {
    # Card numbers follow the plan order of the cards this run can show, so they match -PlanOnly.
    $script:cardNumber = $script:cardIndex[$Case.Key]
    $spec = & $Implementation.Invoke
    $script:approvalCounts.requested++
    $decision = $null
    $approvedDenyCard = $false
    try {
        for ($round = 1; $round -le 2; $round++) {
            $decision = Wait-OperatorDecision $Case $spec
            if ($decision.Status -eq 'request-failed') { return $decision.Result }
            $Record.approval = $decision.Status
            if ($Case.Card -eq 'deny') {
                if ($decision.Status -eq 'approved') {
                    if ($fakeHostRun) { return @{ Skipped = 'the FakeHost auto-approves, so the DENY card cannot be exercised' } }
                    # Withdraw the issued token so nothing can use it, then fail the case.
                    $approvedDenyCard = $true
                    $cancelNote = 'it was cancelled (approval.cancel)'
                    try { Undo-Approval $decision.RequestId $Case.Via }
                    catch { $cancelNote = "cancelling it failed ($($_.Exception.Message)); it expires on its own" }
                    throw "The operator APPROVED the DENY card. Its token was NOT used and $cancelNote; the baseline feature was not deleted."
                }
                if ($decision.Status -ne 'denied') { return @{ Skipped = "operator did not deny the card ($($decision.Status))" } }
                Write-Operator "  Card $($script:cardNumber)/$($script:cardTotal) denied, as asked. Checking that the operation still refuses to run without a token." 'Green'
                return Invoke-WithToken $spec $null $Case.Via
            }
            if ($decision.Status -ne 'approved') { return @{ Skipped = "skipped by the operator ($($decision.Status))" } }
            Write-Operator "  Card $($script:cardNumber)/$($script:cardTotal) approved. Running $($spec.OperationId) with the token." 'Green'
            $result = Invoke-WithToken $spec $decision.Token $Case.Via
            if (-not $result.Success -and $result.ErrorCode -in @('confirmation_required', 'workspace_revision_mismatch') -and $round -eq 1) {
                # The workspace moved between the approval and the call (late settle): ask once more.
                Write-Operator "  The token was refused ($($result.ErrorCode): the workspace changed after approval). The same card is queued once more." 'Yellow'
                $Record.retriedAfter = $result.ErrorCode
                continue
            }
            return $result
        }
    }
    finally {
        # One decision per case: a re-queued card (expiry, refused token) is not counted twice.
        $final = if ($null -ne $decision) { [string]$decision.Status } else { $null }
        if ($approvedDenyCard) { $final = 'approved-deny-card' }
        switch ($final) {
            'approved-deny-card' { $script:approvalCounts.approvedDenyCard++ }
            'request-failed' { $script:approvalCounts.requestFailed++ }
            'approved' { $script:approvalCounts.approved++ }
            'denied' { $script:approvalCounts.denied++ }
            default { $script:approvalCounts.skipped++ }
        }
    }
}

# ---------------------------------------------------------------- live run: case runner

$script:records = New-Object System.Collections.ArrayList
$script:audits = New-Object System.Collections.ArrayList
$script:capabilities = @{}

function Get-MessageAuditFinding([string]$Message) {
    $findings = @()
    if (-not $Message) { return @('no message') }
    if ($Message.Length -ge 500) { $findings += "message is $($Message.Length) characters (limit 500)" }
    if ($Message -match '\b(System\.[A-Z]\w+|[A-Z]\w*Exception)\b') { $findings += "names an exception type ($($Matches[0]))" }
    if ($Message -match '\s+at [A-Za-z_][\w.]+\(') { $findings += 'contains a stack trace' }
    if ($Message -match '[A-Za-z]:\\|\\\\[A-Za-z0-9]|/Users/|%LOCALAPPDATA%|AppData\\') { $findings += 'contains a local path' }
    return $findings
}

function Assert-CaseExpectation($Case, $Result) {
    if ($Case.Kind -eq 'happy') {
        if (-not $Result.Success) { throw "expected success, got $($Result.ErrorCode): $($Result.Message)" }
        return
    }
    if ($null -ne $Case.ExpectDryRunValid) {
        if (-not $Result.Success) { throw "expected a completed dry run, got $($Result.ErrorCode): $($Result.Message)" }
        $valid = [bool](Get-Property $Result.Data 'valid')
        if ($valid -ne [bool]$Case.ExpectDryRunValid) { throw "dry run reported valid=$valid, expected $($Case.ExpectDryRunValid)" }
        return
    }
    if ($Result.Success) { throw 'expected a failure, but the call succeeded' }
    if ($Case.ExpectErrorCode -and $Result.ErrorCode -ne $Case.ExpectErrorCode) { throw "expected $($Case.ExpectErrorCode), got $($Result.ErrorCode): $($Result.Message)" }
    foreach ($fragment in $Case.ExpectMessageContains) {
        if ([string]$Result.Message -notlike "*$fragment*") { throw "the message does not name '$fragment': $($Result.Message)" }
    }
}

function Invoke-PlanCase($Case) {
    $record = [ordered]@{
        name = $Case.Key; operation = $Case.Operation; phase = $Case.Phase; kind = $Case.Kind; via = $Case.Via
        status = 'skipped'; errorCode = $null; message = $null; durationMs = 0; approval = $null; detail = $null
    }
    $null = $script:records.Add($record)
    $missing = @($Case.Requires | Where-Object { -not $script:capabilities[$_] })
    if ($missing.Count -gt 0) {
        $record.detail = "requires $($missing -join ', '), which this run does not have"
        Write-Operator ('[skipped] {0}: {1}' -f $Case.Key, $record.detail) 'DarkGray'
        return
    }
    if ($SkipCards -and $Case.Card) {
        $record.detail = 'approval card skipped (-SkipCards pre-run)'
        Write-Operator ('[skipped] {0}: {1}' -f $Case.Key, $record.detail) 'DarkGray'
        return
    }
    $implementation = $script:implementations[$Case.Key]
    $script:currentCase = $Case.Key
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = if ($Case.Card) { Invoke-CardCase $Case $implementation $record } else { & $implementation.Invoke }
        $result = @($result)[-1]
        if ($result -is [hashtable] -and $result.ContainsKey('Skipped')) {
            $record.detail = $result.Skipped
        }
        elseif ($fakeHostRun -and -not $result.Success -and $result.ErrorCode -eq 'operation_not_found') {
            $record.errorCode = $result.ErrorCode
            $record.detail = 'not implemented by this host (FakeHost)'
        }
        else {
            $record.errorCode = $result.ErrorCode
            $record.message = $result.Message
            if ($Case.Kind -eq 'negative' -and -not $result.Success) {
                $findings = @(Get-MessageAuditFinding $result.Message)
                $null = $script:audits.Add([ordered]@{ case = $Case.Key; operation = $Case.Operation; errorCode = $result.ErrorCode; message = $result.Message; findings = $findings })
            }
            Assert-CaseExpectation $Case $result
            if ($implementation.Verify) { $null = & $implementation.Verify $result }
            $record.status = 'passed'
        }
    }
    catch {
        $record.status = 'failed'
        $record.detail = $_.Exception.Message
    }
    $watch.Stop()
    $record.durationMs = [int]$watch.ElapsedMilliseconds
    $color = switch ($record.status) { 'passed' { 'Green' } 'failed' { 'Red' } default { 'DarkGray' } }
    $note = if ($record.detail) { ': ' + $record.detail } elseif ($record.errorCode) { " ($($record.errorCode))" } else { '' }
    Write-Operator ('[{0,-7}] {1} {2} {3}{4}' -f $record.status, $Case.Kind, $Case.Operation, $Case.Key, $note) $color
}

# ---------------------------------------------------------------- live run: setup

function Test-PortalReachable {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $connect = $client.BeginConnect('www.arcgis.com', 443, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(3000)) { return $false }
        $client.EndConnect($connect)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Copy-TestData([string]$Destination) {
    $null = New-Item -ItemType Directory -Path $Destination -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tests\data\SHP') -Destination (Join-Path $Destination 'SHP') -Recurse
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tests\data\MasterPlan.gdb') -Destination (Join-Path $Destination 'MasterPlan.gdb') -Recurse
    Get-ChildItem -LiteralPath $Destination -Recurse -File -Filter '*.lock' | Remove-Item -Force
    $broken = Join-Path $Destination 'broken'
    $null = New-Item -ItemType Directory -Path $broken -Force
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $Destination 'SHP') -File | Where-Object { $_.BaseName -eq 'Boundary_Multipart_Polygon' })) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $broken ('Broken_Boundary' + $file.Extension))
    }
}

$startedAtUtc = [DateTime]::UtcNow
# Set by the catch below; read by the summary (allPassed is false and summary.aborted is written).
$script:aborted = $null
$exitCode = 1
try {
    Write-Operator "ArcGIS Pro MCP live operation matrix - evidence: $EvidenceDirectory" 'Cyan'
    $stateResult = Invoke-Bridge -Label 'system.get_state' -Method 'system.get_state' -Parameters @{}
    if (-not $stateResult.Success) { throw "system.get_state failed: $($stateResult.ErrorCode): $($stateResult.Message)" }
    $state = $stateResult.Data
    $script:hostProcessId = [int]$state.processId
    $script:pipe = if ($env:ARCGIS_PRO_MCP_PIPE) { $env:ARCGIS_PRO_MCP_PIPE } else { 'ArcGISProMCP.v1.' + $script:hostProcessId }
    $capabilityList = @(Get-Property $state.workspace 'capabilities')
    $available = @{}
    foreach ($capability in $capabilityList) { $available[[string]$capability.id] = [bool]$capability.available }
    if ($available['autonomous-control']) {
        throw 'The host reports autonomous-control. The operation matrix needs DEFAULT mode: without review the no-token negatives would run feature.delete, project.save and project.open. Restart ArcGIS Pro without ARCGIS_PRO_MCP_AUTONOMOUS_MODE.'
    }
    $projectUri = [string](Get-Property $state.workspace.project 'uri')
    $projectPath = $projectUri
    if ($projectUri -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') { $parsed = New-Object System.Uri($projectUri); $projectPath = if ($parsed.IsFile) { $parsed.LocalPath } else { $null } }
    if (-not $fakeHostRun) {
        $rootPrefix = $DisposableRoot.TrimEnd('\') + '\'
        if (-not $projectPath -or -not ([IO.Path]::GetFullPath($projectPath)).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refused: the open project '$projectPath' is not under the disposable root '$DisposableRoot'."
        }
    }
    $script:capabilities['arcpy'] = [bool]$available['arcpy']
    $script:capabilities['online'] = (-not $offlineRun) -and (Test-PortalReachable)
    Write-Operator ("Host PID {0}, {1} operations, project {2}; arcpy={3}, online={4}" -f $script:hostProcessId, $state.operationCount, $projectPath, $script:capabilities['arcpy'], $script:capabilities['online'])

    $dataRoot = Join-Path $DisposableRoot "operations-data-$stamp"
    Copy-TestData $dataRoot
    $geodatabase = Join-Path $dataRoot 'MasterPlan.gdb'
    $script:ctx.parcelsPath = Join-Path $dataRoot 'SHP\Polygon_MixedMultiPart_Parcels.shp'
    $script:ctx.streetsPath = Join-Path $dataRoot 'SHP\Polyline_MultipartMix_Streets.shp'
    $script:ctx.boundaryPath = Join-Path $dataRoot 'SHP\Boundary_Multipart_Polygon.shp'
    $script:ctx.brokenPath = Join-Path $dataRoot 'broken\Broken_Boundary.shp'
    $script:ctx.geodatabasePath = $geodatabase
    $script:ctx.designSitesPath = Join-Path $geodatabase 'DesignSites'
    $script:ctx.massingPath = Join-Path $geodatabase 'ProposedMassing'
    $script:ctx.bufferOutputPath = Join-Path $geodatabase ('DesignSites_Buffer_' + ($stamp -replace '-', '_'))
    if ($projectPath) {
        $script:ctx.projectPath = $projectPath
        $script:ctx.reopenPath = Join-Path $DisposableRoot (([IO.Path]::GetFileNameWithoutExtension($projectPath) -replace '-reopen-\d{8}-\d{6}$', '') + "-reopen-$stamp.aprx")
    }
    if (Test-Path -LiteralPath $expectedStatisticsPath -PathType Leaf) {
        $expected = Get-Content -LiteralPath $expectedStatisticsPath -Raw | ConvertFrom-Json
        $script:ctx.expected = $expected
        $counts = Get-Property $expected 'counts'
        $parcelCount = Get-Property $counts 'Polygon_MixedMultiPart_Parcels'
        $script:ctx.expectedParcelCount = $(if ($null -ne $parcelCount) { $parcelCount } else { $expected.matched })
    }
    elseif (-not $fakeHostRun) { throw "Missing $expectedStatisticsPath (generated with the synthetic test data)." }

    $runnable = @($plan.Cases | Where-Object { $case = $_; $case.Card -and @($case.Requires | Where-Object { -not $script:capabilities[$_] }).Count -eq 0 })
    $script:cardTotal = $runnable.Count
    $script:cardIndex = @{}
    for ($index = 0; $index -lt $runnable.Count; $index++) { $script:cardIndex[$runnable[$index].Key] = $index + 1 }
    $currentPhase = $null
    foreach ($case in $plan.Cases) {
        if ($case.Phase -ne $currentPhase) {
            $currentPhase = $case.Phase
            $title = @($plan.Phases | Where-Object { $_.id -eq $currentPhase })[0].title
            Write-Operator ''
            Write-Operator "== $title" 'Cyan'
            if ($currentPhase -eq 'approvals') {
                Write-Operator ("The next {0} steps need you at ArcGIS Pro: open the MCP Studio dockpane now (Add-In tab > MCP Studio)." -f $script:cardTotal) 'Yellow'
                Write-Operator 'Each card says whether to click APPROVE ONCE or DENY. Read the card before clicking.' 'Yellow'
            }
        }
        Invoke-PlanCase $case
    }
    $exitCode = 0
}
catch {
    Write-Operator "Run aborted: $($_.Exception.Message)" 'Red'
    $script:aborted = $_.Exception.Message
}
finally {
    Close-Gateway
    $env:ARCGIS_PRO_MCP_PIPE = $previousPipe
}

# ---------------------------------------------------------------- summary, errors.md, console.log

$operations = foreach ($descriptor in $descriptors) {
    $cases = @($script:records | Where-Object { $_.operation -eq $descriptor.Id })
    [ordered]@{
        id = $descriptor.Id
        cases = @($cases | ForEach-Object {
                $entry = [ordered]@{ name = $_.name; kind = $_.kind; status = $_.status; errorCode = $_.errorCode; durationMs = $_.durationMs; approval = $_.approval }
                if ($_.detail) { $entry.detail = $_.detail }
                if ($_.via -ne 'bridge') { $entry.via = $_.via }
                $entry
            })
        covered = @($cases | Where-Object { $_.kind -eq 'happy' -and $_.status -eq 'passed' }).Count -gt 0
    }
}
$failed = @($script:records | Where-Object { $_.status -eq 'failed' })
$coveredCount = @($operations | Where-Object { $_.covered }).Count
$flagged = @($script:audits | Where-Object { @($_.findings).Count -gt 0 })
$aborted = $script:aborted
$summary = [ordered]@{
    schemaVersion = 1
    startedAtUtc = $startedAtUtc.ToString('o')
    finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    host = [ordered]@{
        kind = $(if ($fakeHostRun) { 'fakehost' } else { 'arcgis-pro' })
        processId = $script:hostProcessId
        operationCount = $(if (Get-Variable -Name state -ErrorAction SilentlyContinue) { [int](Get-Property $state 'operationCount') } else { 0 })
    }
    capabilities = [ordered]@{ arcpy = [bool]$script:capabilities['arcpy']; online = [bool]$script:capabilities['online'] }
    operations = @($operations)
    coverage = [ordered]@{
        total = @($descriptors).Count
        covered = $coveredCount
        uncovered = @($operations | Where-Object { -not $_.covered } | ForEach-Object { $_.id })
    }
    cases = [ordered]@{
        total = $script:records.Count
        happy = @($script:records | Where-Object kind -eq 'happy').Count
        negative = @($script:records | Where-Object kind -eq 'negative').Count
        passed = @($script:records | Where-Object status -eq 'passed').Count
        failed = $failed.Count
        skipped = @($script:records | Where-Object status -eq 'skipped').Count
    }
    approvals = [ordered]@{
        cards = $script:cardTotal
        requested = $script:approvalCounts.requested
        queued = $script:approvalCounts.queued
        approved = $script:approvalCounts.approved
        denied = $script:approvalCounts.denied
        approvedDenyCard = $script:approvalCounts.approvedDenyCard
        requestFailed = $script:approvalCounts.requestFailed
        expired = $script:approvalCounts.expired
        skipped = $script:approvalCounts.skipped
    }
    messageAudit = [ordered]@{
        checked = $script:audits.Count
        flagged = @($flagged | ForEach-Object { [ordered]@{ case = $_.case; findings = @($_.findings) } })
    }
    # A -SkipCards pre-run is never evidence: it is recorded and can never pass.
    skipCards = [bool]$SkipCards
    allPassed = ($null -eq $aborted) -and (-not $SkipCards) -and $failed.Count -eq 0 -and $coveredCount -eq @($descriptors).Count
}
if ($aborted) { $summary.aborted = $aborted }
Write-Utf8File (Join-Path $EvidenceDirectory 'summary.json') (ConvertTo-JsonText $summary)

$errorLines = New-Object System.Collections.Generic.List[string]
$errorLines.Add('# Negative-case error messages')
$errorLines.Add('')
$errorLines.Add('Every negative case that failed as a call, with the message a client would see. Audit: no exception type names, no stack traces, no local paths, under 500 characters; the plan''s expected fragments (argument or layer names) are checked as part of each case.')
$errorLines.Add('')
$errorLines.Add('| Case | Operation | Error code | Message | Audit |')
$errorLines.Add('| --- | --- | --- | --- | --- |')
foreach ($audit in $script:audits) {
    $cell = { param([string]$Text) ($Text -replace '\|', '\|' -replace "`r?`n", ' ') }
    $verdict = if (@($audit.findings).Count -eq 0) { 'ok' } else { 'FLAGGED: ' + (@($audit.findings) -join '; ') }
    $errorLines.Add(('| {0} | {1} | {2} | {3} | {4} |' -f (& $cell $audit.case), (& $cell $audit.operation), (& $cell ([string]$audit.errorCode)), (& $cell ([string]$audit.message)), (& $cell $verdict)))
}
Write-Utf8File (Join-Path $EvidenceDirectory 'errors.md') ((($errorLines -join "`n")) + "`n")
Write-Utf8File (Join-Path $EvidenceDirectory 'console.log') ((($script:consoleLines -join "`n")) + "`n")

Write-Operator ''
Write-Operator ("Cases: {0} passed, {1} failed, {2} skipped of {3}. Operations covered by a passing happy path: {4}/{5}. Cards: {6} approved, {7} denied, {8} skipped. Flagged messages: {9}." -f `
        $summary.cases.passed, $summary.cases.failed, $summary.cases.skipped, $summary.cases.total, $coveredCount, @($descriptors).Count,
        $summary.approvals.approved, $summary.approvals.denied, $summary.approvals.skipped, $flagged.Count) $(if ($summary.allPassed) { 'Green' } else { 'Yellow' })
foreach ($record in $failed) { Write-Operator ("  FAILED {0} ({1}): {2}" -f $record.name, $record.operation, $record.detail) 'Red' }
if (@($summary.coverage.uncovered).Count -gt 0) { Write-Operator ("  Not covered: {0}" -f (@($summary.coverage.uncovered) -join ', ')) 'Yellow' }
Write-Operator "Summary: $(Join-Path $EvidenceDirectory 'summary.json')" 'Cyan'
if ($exitCode -ne 0 -or -not $summary.allPassed) { exit 1 }
