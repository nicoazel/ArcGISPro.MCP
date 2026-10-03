[CmdletBinding()]
param(
    [ValidateRange(1, 100)] [int]$RunsPerCase = 10,
    [string[]]$SelectedCases = @('tod', 'green', 'mixed'),
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $repoRoot "tools\ArcGISProMCP.DemoRunner\bin\$Configuration\net10.0\ArcGISProMCP.DemoRunner.dll"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "Build the DemoRunner first: $runner"
}

if (-not $OutputDirectory) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repoRoot "artifacts\urban-stress\$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force

$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tests\data\SHP'))
$proposalGeodatabase = [IO.Path]::GetFullPath((Join-Path $repoRoot 'tests\data\MasterPlan.gdb'))
$proposal = Join-Path $proposalGeodatabase 'ProposedBuildings'
$massing = Join-Path $proposalGeodatabase 'ProposedMassing'
$fixtureNames = @(
    'Polygon_MixedMultiPart_Parcels',
    'Polyline_MultipartMix_Streets',
    'Polygons_Single_Buildings',
    'Point_Multi_Mixed',
    'Boundary_Multipart_Polygon'
)
foreach ($fixtureName in $fixtureNames) {
    foreach ($extension in @('.shp', '.shx', '.dbf', '.prj')) {
        $fixturePath = Join-Path $fixtureRoot "$fixtureName$extension"
        if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            throw "Standalone urban fixture is incomplete: $fixturePath"
        }
    }
}
if (-not (Test-Path -LiteralPath $proposalGeodatabase -PathType Container) -or
    @((Get-ChildItem -LiteralPath $proposalGeodatabase -Filter '*.gdbtable' -File -ErrorAction SilentlyContinue)).Count -eq 0) {
    throw "Standalone proposal geodatabase is missing or incomplete: $proposalGeodatabase"
}
$commonStats = @{
    'stats-area'       = @{ matched = 1019; sum = 211.80126265300203 }
    'stats-population' = @{ matched = 1019; sum = 2977.0 }
    'stats-dwellings'  = @{ matched = 1019; sum = 1760.0 }
    'stats-employment' = @{ matched = 1019; sum = 4716.0 }
}

$cases = @(
    [pscustomobject]@{
        Name = 'tod'
        Workflow = 'workflows\urban-tod-corridor.workflow.json'
        Id = 'workflow.urban-tod-corridor'
        Version = '1.2.0'
        Layout = 'Urban Test 1 - TOD Corridor'
        Parameters = @{
            zoningSource = Join-Path $fixtureRoot 'Polygon_MixedMultiPart_Parcels.shp'
            transitSource = Join-Path $fixtureRoot 'Polyline_MultipartMix_Streets.shp'
            buildingsSource = Join-Path $fixtureRoot 'Polygons_Single_Buildings.shp'
            proposalSource = $proposal
        }
    },
    [pscustomobject]@{
        Name = 'green'
        Workflow = 'workflows\urban-green-loop.workflow.json'
        Id = 'workflow.urban-green-loop'
        Version = '1.2.0'
        Layout = 'Urban Test 2 - Green Loop'
        Parameters = @{
            zoningSource = Join-Path $fixtureRoot 'Polygon_MixedMultiPart_Parcels.shp'
            corridorSource = Join-Path $fixtureRoot 'Polyline_MultipartMix_Streets.shp'
            pointsSource = Join-Path $fixtureRoot 'Point_Multi_Mixed.shp'
            greenSitesSource = Join-Path $fixtureRoot 'Boundary_Multipart_Polygon.shp'
            proposalSource = $proposal
        }
    },
    [pscustomobject]@{
        Name = 'mixed'
        Workflow = 'workflows\urban-mixed-use-massing.workflow.json'
        Id = 'workflow.urban-mixed-use-massing'
        Version = '1.2.0'
        Layout = 'Urban Test 3 - Mixed Use Massing'
        Parameters = @{
            zoningSource = Join-Path $fixtureRoot 'Polygon_MixedMultiPart_Parcels.shp'
            buildingsSource = Join-Path $fixtureRoot 'Polygons_Single_Buildings.shp'
            boundarySource = Join-Path $fixtureRoot 'Boundary_Multipart_Polygon.shp'
            massingSource = $massing
            proposalSource = $proposal
        }
    }
)
$unknownCases = @($SelectedCases | Where-Object { $_ -notin @('tod', 'green', 'mixed') })
if ($unknownCases.Count -ne 0) {
    throw "Unknown urban stress case: $($unknownCases -join ', ')"
}
$cases = @($cases | Where-Object { $_.Name -in $SelectedCases })

function Invoke-RunnerCall([hashtable]$Request, [string]$ResultPath) {
    $requestPath = [IO.Path]::ChangeExtension($ResultPath, '.request.json')
    $Request | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $requestPath -Encoding utf8
    & dotnet $runner --call $requestPath $ResultPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Runner call failed; inspect $ResultPath and $requestPath" }
    return Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json
}

function Assert-NearlyEqual([double]$Actual, [double]$Expected, [double]$Tolerance, [string]$Message) {
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Message Expected $Expected, received $Actual."
    }
}

function Assert-WorkflowResult($Result, [int]$Iteration) {
    if (-not $Result.success) { throw 'Workflow reported failure.' }
    $failed = @($Result.results | Where-Object { -not $_.success })
    if ($failed.Count -ne 0) { throw "Workflow contained $($failed.Count) failed steps." }

    foreach ($entry in $commonStats.GetEnumerator()) {
        $steps = @($Result.results | Where-Object step -eq $entry.Key)
        if ($steps.Count -ne 1) { throw "$($entry.Key) expected one result, received $($steps.Count)." }
        $step = $steps[0]
        if ($step.data.matched -ne $entry.Value.matched) {
            throw "$($entry.Key) expected $($entry.Value.matched) features, received $($step.data.matched)."
        }
        Assert-NearlyEqual ([double]$step.data.sum) ([double]$entry.Value.sum) 1e-9 "$($entry.Key) sum drifted."
    }

    if ($Iteration -gt 1) {
        $created = @($Result.results | Where-Object {
            $_.data -and $_.data.PSObject.Properties['Created'] -and $_.data.Created
        })
        if ($created.Count -ne 0) {
            throw "Warm iteration $Iteration recreated $($created.Count) ensure/add targets."
        }
    }

    $captures = @($Result.results | Where-Object operation -eq 'view.capture')
    if ($captures.Count -ne 1) { throw "Expected one capture result, received $($captures.Count)." }
    $capture = $captures[0]
    if ($capture.data.width -ne 2400 -or $capture.data.height -ne 1553 -or -not $capture.data.resource) {
        throw 'Capture did not return the required 2400x1553 resource.'
    }
    return $capture.data.resource
}

function Assert-Layout([string]$LayoutName, [string]$CaseDirectory) {
    $inspectPath = Join-Path $CaseDirectory 'layout-inspect.result.json'
    $inspect = Invoke-RunnerCall @{
        method = 'registry.invoke'
        parameters = @{
            operationId = 'layout.inspect'
            arguments = @{ layout = $LayoutName }
            idempotencyKey = "urban-layout-inspect-$([Guid]::NewGuid().ToString('N'))"
        }
    } $inspectPath
    if (-not $inspect.success) { throw "layout.inspect failed for $LayoutName" }
    Assert-NearlyEqual ([double]$inspect.data.page.width) 17.0 1e-6 'Layout width drifted.'
    Assert-NearlyEqual ([double]$inspect.data.page.height) 11.0 1e-6 'Layout height drifted.'
    $frames = @($inspect.data.elements | Where-Object type -eq 'map-frame')
    if ($frames.Count -ne 3) { throw "$LayoutName expected three map frames, received $($frames.Count)." }
    $duplicates = @($inspect.data.elements | Group-Object name | Where-Object Count -gt 1)
    if ($duplicates.Count -ne 0) { throw "$LayoutName contains duplicate named elements: $($duplicates.Name -join ', ')" }
    if (@($inspect.data.elements | Where-Object type -eq 'north-arrow').Count -ne 1) { throw "$LayoutName north arrow count drifted." }
    if (@($inspect.data.elements | Where-Object type -eq 'scale-bar').Count -ne 1) { throw "$LayoutName scale bar count drifted." }
    if (@($inspect.data.elements | Where-Object type -eq 'legend').Count -ne 1) { throw "$LayoutName legend count drifted." }
    if (@($inspect.data.elements | Where-Object name -eq 'dynamic-status').Count -ne 1) { throw "$LayoutName dynamic status text is missing." }
}

function Assert-Png([string]$Path) {
    Add-Type -AssemblyName System.Drawing.Common
    $bitmap = [Drawing.Bitmap]::new($Path)
    try {
        if ($bitmap.Width -ne 2400 -or $bitmap.Height -ne 1553) { throw "Unexpected PNG dimensions for $Path" }
        $colors = [Collections.Generic.HashSet[int]]::new()
        for ($x = 0; $x -lt $bitmap.Width; $x += 120) {
            for ($y = 0; $y -lt $bitmap.Height; $y += 78) {
                $null = $colors.Add($bitmap.GetPixel($x, $y).ToArgb())
            }
        }
        if ($colors.Count -lt 8) { throw "PNG appears blank or visually degenerate: $Path ($($colors.Count) sampled colors)." }
    }
    finally { $bitmap.Dispose() }
}

function Get-JsonLeaf($Value, [string]$Path) {
    if ($null -eq $Value) {
        "$Path=<null>"
        return
    }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in ($Value.Keys | Sort-Object)) {
            $childPath = if ($Path) { "$Path.$key" } else { [string]$key }
            Get-JsonLeaf $Value[$key] $childPath
        }
        return
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Get-JsonLeaf $item "$Path[$index]"
            $index++
        }
        return
    }
    "$Path=$Value"
}

function Assert-SameWorkflowDefinition([string]$WorkflowPath, [string]$InstalledPath, $Case) {
    $local = Get-Content -LiteralPath $WorkflowPath -Raw | ConvertFrom-Json -AsHashtable
    $installed = Get-Content -LiteralPath $InstalledPath -Raw | ConvertFrom-Json -AsHashtable
    $ignored = '^(contentHash=|.*\.(defaultValue|expectedObservation)=<null>$|steps\[\d+\]\.continueOnError=False$|tags\[\d+\]=|requiredCapabilities\[\d+\]=)'
    $localLeaves = @(Get-JsonLeaf $local '' | Where-Object { $_ -notmatch $ignored })
    $installedLeaves = @(Get-JsonLeaf $installed '' | Where-Object { $_ -notmatch $ignored })
    $differences = @(Compare-Object $localLeaves $installedLeaves)
    foreach ($setName in @('tags', 'requiredCapabilities')) {
        $localSet = @($local[$setName] | Sort-Object) -join "`n"
        $installedSet = @($installed[$setName] | Sort-Object) -join "`n"
        if ($localSet -ne $installedSet) {
            $differences += "$setName differs"
        }
    }
    if ($differences.Count -ne 0) {
        throw "Installed immutable workflow $($Case.Id)@$($Case.Version) differs from $WorkflowPath. Bump the workflow version before testing. Differences: $($differences -join '; ')"
    }
}

function Install-WorkflowIfNeeded($Case, [string]$WorkflowPath, [string]$EvidenceDirectory) {
    $listPath = Join-Path $EvidenceDirectory 'workflow-list.result.json'
    $installed = @(Invoke-RunnerCall @{
        method = 'workflow.list'
        parameters = @{}
    } $listPath)
    $match = @($installed | Where-Object { $_.id -eq $Case.Id -and $_.version -eq $Case.Version })
    if ($match.Count -eq 0) {
        & dotnet $runner --save-workflow $WorkflowPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not install $($Case.Id)@$($Case.Version)." }
        return
    }

    $getPath = Join-Path $EvidenceDirectory 'workflow-get.result.json'
    $null = Invoke-RunnerCall @{
        method = 'workflow.get'
        parameters = @{
            workflowId = $Case.Id
            version = $Case.Version
        }
    } $getPath
    Assert-SameWorkflowDefinition $WorkflowPath $getPath $Case
}

$summary = [Collections.Generic.List[object]]::new()
foreach ($case in $cases) {
    $workflowPath = Join-Path $repoRoot $case.Workflow
    $caseDirectory = Join-Path $OutputDirectory $case.Name
    $null = New-Item -ItemType Directory -Path $caseDirectory -Force
    Install-WorkflowIfNeeded $case $workflowPath $caseDirectory
    $resource = $null
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    for ($iteration = 1; $iteration -le $RunsPerCase; $iteration++) {
        $resultPath = Join-Path $caseDirectory ("run-{0:D2}.result.json" -f $iteration)
        $result = Invoke-RunnerCall @{
            method = 'workflow.run'
            parameters = @{
                workflowId = $case.Id
                version = $case.Version
                idempotencyKey = "urban-$($case.Name)-$iteration-$([Guid]::NewGuid().ToString('N'))"
                parameters = $case.Parameters
            }
        } $resultPath
        $resource = Assert-WorkflowResult $result $iteration
    }
    $stopwatch.Stop()
    Assert-Layout $case.Layout $caseDirectory
    $pngPath = Join-Path $caseDirectory 'final-layout.png'
    & dotnet $runner --image $resource $pngPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not download final capture for $($case.Name)." }
    Assert-Png $pngPath
    $summary.Add([pscustomobject]@{
        case = $case.Name
        workflow = "$($case.Id)@$($case.Version)"
        runs = $RunsPerCase
        elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        layout = $case.Layout
        capture = $pngPath
        success = $true
    })
}

$summaryPath = Join-Path $OutputDirectory 'summary.json'
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary
"Evidence: $summaryPath"
