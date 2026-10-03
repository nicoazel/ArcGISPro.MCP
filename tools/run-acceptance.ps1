<#
.SYNOPSIS
    Collects live ArcGIS Pro acceptance evidence for the exact build at HEAD.
.DESCRIPTION
    Orchestrates the existing harnesses (verify-release.ps1, test-mcp.ps1,
    run-live-feature-gp-arcpy.ps1, run-urban-stress.ps1, run-live-operations.ps1) without
    modifying them, records
    git/.NET/ArcGIS Pro facts and add-in DLL hashes, and writes manifest.json, summary.md
    and SHA256SUMS. This script itself is Windows PowerShell 5.1 compatible; the harnesses
    it invokes require PowerShell 7 (pwsh), which is resolved separately.

    Safety model:
      * The default run is read-only against ArcGIS Pro: install facts, discovery records,
        a system.get_state probe and the MCP smoke test (whose approval probe creates one
        pending review card and cancels it; nothing is approved or mutated).
      * 'stress' mutates the open project (maps, layers, layouts, symbology). It requires
        -AllowProjectMutation and a -DisposableRoot that contains the open project.
      * 'feature-gp-arcpy' mutates data and saves the project WITHOUT approval tokens. It
        REQUIRES THE HOST TO RUN IN AUTONOMOUS MODE (ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true at
        Pro startup) and additionally requires -AllowAutonomous, -AllowProjectMutation and a
        -DisposableRoot containing the open project.
      * 'operations' runs every operation (run-live-operations.ps1) in DEFAULT mode: the
        operator approves seven review cards and denies one in the MCP Studio dockpane, so its
        prompts are shown on this console. It mutates the project, saves it and finally opens a
        copy, so it requires -AllowProjectMutation and a -DisposableRoot containing the open
        project, and it is refused while the host reports autonomous-control.
      * The host is a live ArcGIS Pro discovery record (hostKind arcgis-pro), never a FakeHost:
        an explicit -PipeName that matches no such record blocks every live step.
      * -PlanOnly (alias -DryRun) prints the plan and collects only facts that need no running
        ArcGIS Pro, no build and no child harness. It never writes under docs/.
      * -Commit copies the evidence to docs/acceptance/<yyyy-MM-dd>-<sha7>/. It is refused for
        -PlanOnly, -SkipVerify, a dirty working tree, an existing target folder, or any
        selected step that did not pass. It never runs git add/commit. The committed manifest
        lists only files the folder contains: section and top-level evidence paths, and the path
        before ': ' in each -VisuallyInspected note, are rewritten to the committed layout (for
        example stress/tod/final-layout.png -> images/layout-tod.png), and it records
        "evidencePathsRelative": true. A note whose path was not committed is prefixed with
        'not committed: '. The committed stress/summary.json names each case's capture by its
        committed image (images/layout-<case>.png), or 'not committed: <working path>'.
      * -RunNotes records how the run was carried out (who decided the approval cards, prompts
        answered on the host, anything else a reviewer needs) as runNotes[] in manifest.json and
        a "Run notes" section of summary.md, so these disclosures are part of the checksummed
        record instead of being added afterwards.

    Only the maintainer, on a workstation with ArcGIS Pro and the exact installed package,
    should produce committed evidence. See docs/acceptance/README.md.
.EXAMPLE
    ./tools/run-acceptance.ps1 -PlanOnly
.EXAMPLE
    ./tools/run-acceptance.ps1 -PipeName ArcGISProMCP.v1.12345
.EXAMPLE
    ./tools/run-acceptance.ps1 -Sections smoke,feature-gp-arcpy,stress -AllowProjectMutation -AllowAutonomous -DisposableRoot D:\scratch\mcp-acceptance -Commit
.EXAMPLE
    ./tools/run-acceptance.ps1 -Sections smoke,stress,operations -AllowProjectMutation -DisposableRoot D:\scratch\mcp-acceptance -Commit
#>
# An operator console script: coloured progress is for the person at the workstation, and every
# result that matters is written to manifest.json and summary.md, so Write-Host is intended here.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingWriteHost', '', Justification = 'Operator console output; results are recorded in manifest.json and summary.md.')]
# PSReviewUnusedParameter does not follow parameters into functions and step scriptblocks.
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'Configuration', Justification = 'Used by Invoke-DemoRunnerCall and the smoke, feature-gp-arcpy, stress and operations step scriptblocks.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'RunsPerCase', Justification = 'Passed to run-urban-stress.ps1 by the stress step scriptblock.')]
[Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', 'ImageUri', Justification = 'Passed to test-mcp.ps1 by the smoke step scriptblock.')]
[CmdletBinding()]
param(
    [string]$PipeName,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string[]]$Sections = @('smoke'),
    [ValidateRange(1, 100)]
    [int]$RunsPerCase = 3,
    [string]$ImageUri,
    [switch]$AllowProjectMutation,
    [switch]$AllowAutonomous,
    [string]$DisposableRoot,
    [string[]]$Screenshot = @(),
    [string[]]$VisuallyInspected = @(),
    # Where the -VisuallyInspected notes come from (for example 'operator review of the working
    # evidence, 2026-09-28'); recorded in manifest.json and summary.md.
    [string]$VisualNotesSource,
    # Disclosures about how the run was carried out (for example who decided the approval cards,
    # or a host prompt someone answered during the run); recorded as runNotes[] in manifest.json
    # and under "Run notes" in summary.md.
    [string[]]$RunNotes = @(),
    [string]$Operator,
    [string]$OutputDirectory,
    [switch]$SkipVerify,
    [switch]$Commit,
    [Alias('DryRun')]
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolsRoot = Join-Path $repoRoot 'tools'
$addInId = $null
$maxScreenshots = 5
$maxScreenshotBytes = 500KB
$maxCopiedJsonBytes = 1MB
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$startedAtUtc = [DateTime]::UtcNow
# Accept both -Sections a,b (PowerShell array) and "a,b" (a single string, as powershell -File passes it).
$Sections = @($Sections | ForEach-Object { $_ -split "," } | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)

$sectionCatalog = [ordered]@{
    'smoke' = [ordered]@{
        script = 'test-mcp.ps1'
        mutatesProject = $false
        requiresAutonomousMode = $false
        description = 'MCP initialize, 16 tools, live state, registry search/describe/validate, skill read, pending-then-cancelled approval review, optional native image block.'
    }
    'feature-gp-arcpy' = [ordered]@{
        script = 'run-live-feature-gp-arcpy.ps1'
        mutatesProject = $true
        requiresAutonomousMode = $true
        description = 'ArcPy hash/nonzero/timeout, feature create/query/update/select/delete, stale-revision and wrong-geometry rejection, metadata update, 10x Buffer+GetCount, project save. REQUIRES AUTONOMOUS MODE.'
    }
    'stress' = [ordered]@{
        script = 'run-urban-stress.ps1'
        mutatesProject = $true
        requiresAutonomousMode = $false
        description = 'Three urban layout workflows x RunsPerCase on tests/data fixtures, layout inspection and final capture PNG checks.'
    }
    'operations' = [ordered]@{
        script = 'run-live-operations.ps1'
        mutatesProject = $true
        requiresAutonomousMode = $false
        # Without review, its no-token negative cases would delete, save and open instead of failing closed.
        requiresDefaultMode = $true
        description = 'All 41 operations: happy paths, negative cases and 8 review cards (7 approve, 1 deny) decided by the operator in the dockpane; MCP gateway pass; ends by opening a saved copy. DEFAULT MODE ONLY.'
    }
}

# ---------------------------------------------------------------- helpers

function Write-Utf8File([string]$Path, [string]$Text) {
    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { $null = New-Item -ItemType Directory -Path $directory -Force }
    [IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function ConvertTo-JsonText($Value) {
    return (($Value | ConvertTo-Json -Depth 20) -replace "`r`n", "`n") + "`n"
}

function Invoke-NativeQuiet {
    param([string]$FilePath, [string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $FilePath @Arguments 2>$null
        $code = $LASTEXITCODE
    }
    catch {
        $output = @()
        $code = -1
    }
    finally { $ErrorActionPreference = $previous }
    return [pscustomobject]@{ ExitCode = $code; Output = @($output | ForEach-Object { [string]$_ }) }
}

function Get-FirstLine($Result) {
    if ($Result.ExitCode -ne 0 -or @($Result.Output).Count -eq 0) { return $null }
    return ([string]$Result.Output[0]).Trim()
}

function Get-Sha256Hex([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-StreamSha256Hex([IO.Stream]$Stream) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Stream)) -replace '-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "'$pathFull' is not under '$rootFull'." }
    return $pathFull.Substring($rootFull.Length).Replace('\', '/')
}

function Test-PathUnderRoot([string]$Path, [string]$Root) {
    if (-not $Path -or -not $Root) { return $false }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $pathFull = [IO.Path]::GetFullPath($Path)
    return $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)
}

function ConvertTo-LocalPath([string]$Uri) {
    if (-not $Uri) { return $null }
    if ($Uri -match '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
        $parsed = New-Object System.Uri($Uri)
        if ($parsed.IsFile) { return $parsed.LocalPath }
        return $null
    }
    return $Uri
}

function Resolve-ChildPowerShell {
    if ($PSVersionTable.PSVersion.Major -ge 7) {
        return [pscustomobject]@{ Path = (Get-Process -Id $PID).Path; Version = $PSVersionTable.PSVersion.ToString() }
    }
    $command = Get-Command pwsh -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command) { return $null }
    $version = Get-FirstLine (Invoke-NativeQuiet $command.Source @('-NoProfile', '-NonInteractive', '-Command', '$PSVersionTable.PSVersion.ToString()'))
    return [pscustomobject]@{ Path = $command.Source; Version = $version }
}

function Format-PsLiteral([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }

function Invoke-ChildScript {
    <#
        Runs one harness in PowerShell 7 with stdout/stderr captured to log files. -ShowConsole
        leaves stdout on this console (operator prompts) and captures only stderr. -AllowNonZeroExit
        returns the exit code instead of throwing, for harnesses that write their own summary.
    #>
    param(
        [string]$Name,
        [string]$ScriptName,
        [string[]]$ArgumentTokens,
        [string]$LogDirectory,
        [string]$ResultJsonPath,
        [switch]$ShowConsole,
        [switch]$AllowNonZeroExit
    )
    if (-not $script:childShell) { throw 'PowerShell 7 (pwsh) is required to run the acceptance harnesses.' }
    $scriptPath = Join-Path $toolsRoot $ScriptName
    $command = '$ErrorActionPreference = ''Stop''; & ' + (Format-PsLiteral $scriptPath)
    if ($ArgumentTokens) { $command += ' ' + ($ArgumentTokens -join ' ') }
    if ($ResultJsonPath) {
        $command += ' | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath ' + (Format-PsLiteral $ResultJsonPath) + ' -Encoding utf8'
    }
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $stdout = Join-Path $LogDirectory "$Name.stdout.log"
    $stderr = Join-Path $LogDirectory "$Name.stderr.log"
    $startArguments = @{
        FilePath = $script:childShell.Path
        ArgumentList = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
        WorkingDirectory = $repoRoot
        NoNewWindow = $true
        PassThru = $true
        RedirectStandardError = $stderr
    }
    if (-not $ShowConsole) { $startArguments.RedirectStandardOutput = $stdout }
    $process = Start-Process @startArguments
    $null = $process.Handle
    $process.WaitForExit()
    $code = $process.ExitCode
    if ($AllowNonZeroExit) { return $code }
    if ($code -ne 0) {
        $tail = @(Get-Content -LiteralPath $stderr -Tail 5 -ErrorAction SilentlyContinue) -join ' | '
        throw "$ScriptName exited with code $code. See $stderr. $tail"
    }
}

function Invoke-DemoRunnerCall([string]$Method, [string]$RequestPath, [string]$ResultPath) {
    $runner = Join-Path $repoRoot "tools\ArcGISProMCP.DemoRunner\bin\$Configuration\net10.0\ArcGISProMCP.DemoRunner.dll"
    if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) { throw "DemoRunner is not built: $runner" }
    Write-Utf8File $RequestPath (ConvertTo-JsonText ([ordered]@{ method = $Method; parameters = @{} }))
    $call = Invoke-NativeQuiet 'dotnet' @($runner, '--call', $RequestPath, $ResultPath)
    if ($call.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $ResultPath -PathType Leaf)) {
        throw "$Method via DemoRunner failed (exit $($call.ExitCode))."
    }
    return (Get-Content -LiteralPath $ResultPath -Raw | ConvertFrom-Json)
}

$script:steps = New-Object System.Collections.ArrayList

function Add-Step {
    param([string]$Name, [string]$Status, [string]$Detail, [bool]$MutatesProject = $false,
        [bool]$RequiresAutonomousMode = $false, [double]$DurationSeconds = 0, [string[]]$Evidence = @(), [string]$StartedAtUtc)
    if (-not $StartedAtUtc) { $StartedAtUtc = [DateTime]::UtcNow.ToString('o') }
    $step = [ordered]@{
        name = $Name
        status = $Status
        mutatesProject = $MutatesProject
        requiresAutonomousMode = $RequiresAutonomousMode
        startedAtUtc = $StartedAtUtc
        durationSeconds = [Math]::Round($DurationSeconds, 3)
        detail = $Detail
        evidence = @($Evidence)
    }
    $null = $script:steps.Add($step)
    $color = switch ($Status) { 'passed' { 'Green' } 'failed' { 'Red' } 'blocked' { 'Yellow' } default { 'Gray' } }
    Write-Host ('[{0,-7}] {1}: {2}' -f $Status, $Name, $Detail) -ForegroundColor $color
}

function Invoke-Step {
    <# Runs $Action, timing it and recording passed/failed. $Action returns @{detail=...; evidence=@(...)}. #>
    param([string]$Name, [scriptblock]$Action, [bool]$MutatesProject = $false, [bool]$RequiresAutonomousMode = $false)
    $started = [DateTime]::UtcNow
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = @(& $Action)[-1]
        $watch.Stop()
        Add-Step -Name $Name -Status 'passed' -Detail ([string]$result.detail) -Evidence @($result.evidence) `
            -MutatesProject $MutatesProject -RequiresAutonomousMode $RequiresAutonomousMode `
            -DurationSeconds $watch.Elapsed.TotalSeconds -StartedAtUtc $started.ToString('o')
        return $true
    }
    catch {
        $watch.Stop()
        Add-Step -Name $Name -Status 'failed' -Detail $_.Exception.Message `
            -MutatesProject $MutatesProject -RequiresAutonomousMode $RequiresAutonomousMode `
            -DurationSeconds $watch.Elapsed.TotalSeconds -StartedAtUtc $started.ToString('o')
        return $false
    }
}

function Get-ModeRequirement($Info) {
    if ($Info.requiresAutonomousMode) { return 'REQUIRED' }
    if ($Info.Contains('requiresDefaultMode') -and $Info.requiresDefaultMode) { return 'must be OFF' }
    return 'no'
}

# ---------------------------------------------------------------- argument checks

$unknownSections = @($Sections | Where-Object { -not $sectionCatalog.Contains($_) })
if ($unknownSections.Count -gt 0) { throw "Unknown section(s): $($unknownSections -join ', '). Valid: $(@($sectionCatalog.Keys) -join ', ')." }
if ($Sections.Count -eq 0) { throw 'Select at least one section.' }
if ($Commit -and $PlanOnly) { throw '-Commit cannot be combined with -PlanOnly: a plan is not evidence.' }
if ($Commit -and $SkipVerify) { throw '-Commit requires the verify step; remove -SkipVerify.' }
if ($DisposableRoot) { $DisposableRoot = [IO.Path]::GetFullPath($DisposableRoot) }
if (@($Screenshot).Count -gt $maxScreenshots) { throw "At most $maxScreenshots screenshots may be attached." }
foreach ($shot in @($Screenshot)) {
    if (-not (Test-Path -LiteralPath $shot -PathType Leaf)) { throw "Screenshot not found: $shot" }
    if ([IO.Path]::GetExtension($shot) -ne '.png') { throw "Screenshots must be PNG files: $shot" }
}

# Which selected sections are refused up front, and why.
$refusals = [ordered]@{}
foreach ($section in $Sections) {
    $info = $sectionCatalog[$section]
    $reasons = @()
    if ($info.mutatesProject -and -not $AllowProjectMutation) { $reasons += 'mutates the open project: pass -AllowProjectMutation' }
    if ($info.mutatesProject -and -not $DisposableRoot) { $reasons += 'mutates the open project: pass -DisposableRoot <folder containing the disposable .aprx>' }
    if ($info.mutatesProject -and $DisposableRoot -and -not (Test-Path -LiteralPath $DisposableRoot -PathType Container)) { $reasons += "disposable root does not exist: $DisposableRoot" }
    if ($info.requiresAutonomousMode -and -not $AllowAutonomous) { $reasons += 'REQUIRES AUTONOMOUS MODE on the host: pass -AllowAutonomous after starting Pro with ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true' }
    if ($reasons.Count -gt 0) { $refusals[$section] = ($reasons -join '; ') }
}

# ---------------------------------------------------------------- preflight (no Pro, no build)

Write-Host "ArcGIS Pro MCP acceptance - $(if ($PlanOnly) { 'PLAN ONLY (no Pro, no build, no harness)' } else { 'live run' })" -ForegroundColor Cyan

$gitSha = Get-FirstLine (Invoke-NativeQuiet 'git' @('-C', $repoRoot, 'rev-parse', 'HEAD'))
if (-not $gitSha -or $gitSha -notmatch '^[0-9a-f]{40}$') { throw 'Could not resolve the git commit (run from a git checkout).' }
$gitStatus = Invoke-NativeQuiet 'git' @('-C', $repoRoot, 'status', '--porcelain')
$dirty = @($gitStatus.Output | Where-Object { $_ -and $_.Trim() }).Count -gt 0
$describe = Get-FirstLine (Invoke-NativeQuiet 'git' @('-C', $repoRoot, 'describe', '--tags', '--always', '--dirty'))
$tag = Get-FirstLine (Invoke-NativeQuiet 'git' @('-C', $repoRoot, 'describe', '--tags', '--exact-match', 'HEAD'))
$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) { throw 'Directory.Build.props does not define a version.' }
$version = $versionNode.InnerText.Trim()
$dotnetVersion = Get-FirstLine (Invoke-NativeQuiet 'dotnet' @('--version'))
$daml = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\ArcGISProMCP.AddIn\Config.daml') -Raw)
$addInNode = $daml.SelectSingleNode("//*[local-name()='AddInInfo']")
if ($null -eq $addInNode) { throw 'Config.daml has no AddInInfo element.' }
$addInId = $addInNode.GetAttribute('id')
$script:childShell = Resolve-ChildPowerShell
if (-not $Operator) { $Operator = Get-FirstLine (Invoke-NativeQuiet 'git' @('-C', $repoRoot, 'config', 'user.name')) }
if (-not $Operator) { $Operator = 'unknown' }
$sha7 = $gitSha.Substring(0, 7)
$commitFolderName = '{0}-{1}' -f $startedAtUtc.ToLocalTime().ToString('yyyy-MM-dd'), $sha7
$commitFolder = Join-Path $repoRoot (Join-Path 'docs\acceptance' $commitFolderName)

if ($Commit -and $dirty) { throw '-Commit refused: the working tree is dirty. Evidence must describe a committed SHA.' }
if ($Commit -and (Test-Path -LiteralPath $commitFolder)) { throw "-Commit refused: $commitFolder already exists." }

# ---------------------------------------------------------------- ArcGIS Pro install facts (read-only, Pro need not run)

function Get-ProInstallFact {
    $facts = [ordered]@{ installDir = $null; realVersion = $null; registryVersion = $null; exeProductVersion = $null; runningProcessIds = @() }
    $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\ESRI\ArcGISPro' -ErrorAction SilentlyContinue
    if ($key) {
        if ($key.PSObject.Properties['InstallDir']) { $facts.installDir = [string]$key.InstallDir }
        if ($key.PSObject.Properties['RealVersion']) { $facts.realVersion = [string]$key.RealVersion }
        if ($key.PSObject.Properties['Version']) { $facts.registryVersion = [string]$key.Version }
    }
    if ($facts.installDir) {
        $exe = Join-Path $facts.installDir 'bin\ArcGISPro.exe'
        if (Test-Path -LiteralPath $exe -PathType Leaf) { $facts.exeProductVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion }
    }
    $facts.runningProcessIds = @(Get-Process -Name 'ArcGISPro' -ErrorAction SilentlyContinue | Sort-Object Id | ForEach-Object { [int]$_.Id })
    return $facts
}

function Get-DiscoveryRecord {
    $root = Join-Path $env:LOCALAPPDATA 'ArcGISProMCP\hosts'
    $records = @()
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { return $records }
    foreach ($file in Get-ChildItem -LiteralPath $root -Filter 'host-*.json' -File) {
        try { $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json } catch { continue }
        if ($null -eq $record -or -not $record.PSObject.Properties['processId']) { continue }
        # A record without hostKind predates the field and was written by the add-in.
        $hostKind = 'arcgis-pro'
        if ($record.PSObject.Properties['hostKind'] -and $record.hostKind) { $hostKind = [string]$record.hostKind }
        $live = $false
        $process = Get-Process -Id ([int]$record.processId) -ErrorAction SilentlyContinue
        if ($process -and $process.ProcessName -eq 'ArcGISPro') {
            try {
                $started = [DateTimeOffset]::Parse([string]$record.processStartedAtUtc).UtcDateTime
                $live = [Math]::Abs(($process.StartTime.ToUniversalTime() - $started).TotalSeconds) -lt 5
            }
            catch { $live = $false }
        }
        $records += [pscustomobject]@{ processId = [int]$record.processId; pipeName = [string]$record.pipeName; projectUri = [string]$record.projectUri; hostKind = $hostKind; live = $live }
    }
    return $records
}

function Get-PackageDllHash([string]$PackagePath) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $hashes = @()
    $zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($entry in $zip.Entries) {
            $entryName = $entry.FullName.Replace('\', '/')
            if ($entryName -notmatch '^Install/[^/]+\.dll$') { continue }
            $stream = $entry.Open()
            try { $hashes += [ordered]@{ name = $entry.Name; sha256 = (Get-StreamSha256Hex $stream) } }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    return @($hashes | Sort-Object { $_.name })
}

function Get-LoadedDllHash {
    $cache = Join-Path $env:LOCALAPPDATA ('ESRI\ArcGISPro\AssemblyCache\' + $addInId)
    if (-not (Test-Path -LiteralPath $cache -PathType Container)) { return @() }
    return @(Get-ChildItem -LiteralPath $cache -Filter 'ArcGISProMCP.*.dll' -File -Recurse | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; sha256 = (Get-Sha256Hex $_.FullName); path = $_.FullName }
    })
}

function Compare-DllHash($Built, $Loaded) {
    $problems = @()
    if (@($Built).Count -eq 0) { $problems += 'no built DLLs in the package' }
    if (@($Loaded).Count -eq 0) { $problems += "no cached add-in DLLs for $addInId (install the package and restart ArcGIS Pro)" }
    foreach ($dll in @($Built)) {
        $match = @($Loaded | Where-Object { $_.name -eq $dll.name })
        if ($match.Count -eq 0) { $problems += "$($dll.name) not loaded" }
        elseif ($match.Count -gt 1) { $problems += "$($dll.name) is ambiguous: $($match.Count) cached copies ($(@($match | ForEach-Object { $_.path }) -join ', '))" }
        elseif ($match[0].sha256 -ne $dll.sha256) { $problems += "$($dll.name) loaded hash differs from package" }
    }
    return $problems
}

$pro = Get-ProInstallFact
$discovery = @(Get-DiscoveryRecord)
# Only live ArcGIS Pro records count: a FakeHost (hostKind fakehost) is never acceptance evidence.
$liveHosts = @($discovery | Where-Object { $_.live -and $_.hostKind -eq 'arcgis-pro' })
$packagePath = Join-Path $repoRoot 'artifacts\ArcGISProMCP.AddIn.esriAddinX'

$resolvedPipe = $PipeName
$pipeSource = 'parameter'
$pipeProblem = $null
if ($resolvedPipe) {
    # An explicit pipe must belong to a live ArcGIS Pro, not a FakeHost or a stale record.
    $pipeHost = @($liveHosts | Where-Object { $_.pipeName -eq $resolvedPipe })
    if ($pipeHost.Count -eq 1) { $pipeSource = "parameter (live ArcGIS Pro PID $($pipeHost[0].processId))" }
    else {
        $pipeProblem = "-PipeName '$resolvedPipe' does not match a live ArcGIS Pro discovery record (hostKind arcgis-pro)"
        $pipeSource = "parameter, REJECTED: $pipeProblem"
    }
}
else {
    if ($liveHosts.Count -eq 1) { $resolvedPipe = $liveHosts[0].pipeName; $pipeSource = 'single live discovery record' }
    else { $pipeSource = "unresolved ($($liveHosts.Count) live discovery records; pass -PipeName)" }
}

# ---------------------------------------------------------------- plan

$plan = @()
$plan += [pscustomobject]@{ Step = 'preflight'; Runs = 'always'; ReadOnly = 'yes'; NeedsPro = 'no'; Autonomous = 'no'; Invokes = 'git, dotnet --version, Directory.Build.props' }
$plan += [pscustomobject]@{ Step = 'verify'; Runs = $(if ($PlanOnly) { 'no (plan only)' } elseif ($SkipVerify) { 'no (-SkipVerify)' } else { 'yes' }); ReadOnly = 'repo build output only'; NeedsPro = 'no'; Autonomous = 'no'; Invokes = 'pwsh tools/verify-release.ps1' }
$plan += [pscustomobject]@{ Step = 'pro-install'; Runs = 'always'; ReadOnly = 'yes'; NeedsPro = 'installed'; Autonomous = 'no'; Invokes = "HKLM:\SOFTWARE\ESRI\ArcGISPro, ArcGISPro.exe, AssemblyCache\$addInId" }
$plan += [pscustomobject]@{ Step = 'host-probe'; Runs = $(if ($PlanOnly) { 'no (plan only)' } else { 'yes' }); ReadOnly = 'yes'; NeedsPro = 'running'; Autonomous = 'no'; Invokes = 'DemoRunner system.get_state' }
foreach ($section in $Sections) {
    $info = $sectionCatalog[$section]
    $runs = if ($refusals.Contains($section)) { 'REFUSED' } elseif ($PlanOnly) { 'no (plan only)' } else { 'yes' }
    $plan += [pscustomobject]@{
        Step = $section; Runs = $runs
        ReadOnly = $(if ($info.mutatesProject) { 'NO - mutates project' } else { 'yes' })
        NeedsPro = 'running'
        Autonomous = (Get-ModeRequirement $info)
        Invokes = "pwsh tools/$($info.script)"
    }
}
foreach ($section in @($sectionCatalog.Keys)) {
    if ($section -notin $Sections) {
        $plan += [pscustomobject]@{ Step = $section; Runs = 'not selected'; ReadOnly = $(if ($sectionCatalog[$section].mutatesProject) { 'NO - mutates project' } else { 'yes' }); NeedsPro = 'running'; Autonomous = (Get-ModeRequirement $sectionCatalog[$section]); Invokes = "pwsh tools/$($sectionCatalog[$section].script)" }
    }
}
$plan += [pscustomobject]@{ Step = 'commit'; Runs = $(if ($Commit) { "yes -> docs/acceptance/$commitFolderName" } else { 'no (pass -Commit)' }); ReadOnly = 'writes docs/acceptance only'; NeedsPro = 'no'; Autonomous = 'no'; Invokes = 'copy evidence, manifest.json, summary.md, SHA256SUMS' }

Write-Host ''
Write-Host 'Plan' -ForegroundColor Cyan
$plan | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
foreach ($section in $refusals.Keys) { Write-Host "  $section refused: $($refusals[$section])" -ForegroundColor Yellow }
if ('operations' -in $Sections) { Write-Host '  operations: the ordered matrix and the review cards the operator will decide: ./tools/run-live-operations.ps1 -PlanOnly' -ForegroundColor Cyan }

$facts = [ordered]@{
    sha = $gitSha
    dirty = $dirty
    describe = $describe
    tag = $tag
    version = $version
    dotnet = $dotnetVersion
    powershell = $PSVersionTable.PSVersion.ToString()
    childPowerShell = $(if ($script:childShell) { "$($script:childShell.Path) $($script:childShell.Version)" } else { 'not found (install PowerShell 7: the harnesses require pwsh)' })
    addInId = $addInId
    pro = $pro
    discoveryRecords = $discovery
    pipe = [ordered]@{ name = $resolvedPipe; source = $pipeSource }
    package = $(if (Test-Path -LiteralPath $packagePath -PathType Leaf) { [ordered]@{ path = $packagePath; sha256 = (Get-Sha256Hex $packagePath) } } else { 'not built (artifacts/ArcGISProMCP.AddIn.esriAddinX)' })
    loadedDlls = @(Get-LoadedDllHash)
}
if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
    $facts['packageDlls'] = @(Get-PackageDllHash $packagePath)
    $facts['dllMismatches'] = @(Compare-DllHash $facts['packageDlls'] $facts['loadedDlls'])
}

if ($PlanOnly) {
    Write-Host 'Facts (no running ArcGIS Pro, build or harness required)' -ForegroundColor Cyan
    Write-Host (ConvertTo-JsonText $facts)
    Write-Host 'Plan only: nothing was built, no harness ran, ArcGIS Pro was not contacted, nothing was written.' -ForegroundColor Cyan
    return
}

# ---------------------------------------------------------------- live run

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts\acceptance\' + $startedAtUtc.ToLocalTime().ToString('yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$logDirectory = Join-Path $OutputDirectory 'logs'
$null = New-Item -ItemType Directory -Path $logDirectory -Force
Write-Utf8File (Join-Path $OutputDirectory 'preflight.json') (ConvertTo-JsonText $facts)
Add-Step -Name 'preflight' -Status 'passed' -Detail "sha $sha7, dirty=$dirty, version $version, dotnet $dotnetVersion" -Evidence @('preflight.json')

$previousPipe = $env:ARCGIS_PRO_MCP_PIPE
if ($resolvedPipe) { $env:ARCGIS_PRO_MCP_PIPE = $resolvedPipe }
$builtDlls = @()
$loadedDlls = @()
$packageInfo = $null
$state = $null
$autonomousMode = $false
$projectPath = $null
$auditPath = Join-Path $env:LOCALAPPDATA 'ArcGISProMCP\audit\operations.jsonl'
$auditOffset = $null
$pngCandidates = New-Object System.Collections.ArrayList

try {
    # verify: build, portable tests, package
    if ($SkipVerify) {
        Add-Step -Name 'verify' -Status 'skipped' -Detail '-SkipVerify: using the existing package; evidence cannot be committed'
    }
    elseif (-not $script:childShell) {
        Add-Step -Name 'verify' -Status 'blocked' -Detail 'PowerShell 7 (pwsh) not found; verify-release.ps1 requires it'
    }
    else {
        $null = Invoke-Step 'verify' {
            Invoke-ChildScript -Name 'verify' -ScriptName 'verify-release.ps1' -ArgumentTokens @() -LogDirectory $logDirectory
            @{ detail = 'Release build, every tests/*.Tests project, diff check and package contents passed'; evidence = @('logs/verify.stdout.log') }
        }
    }

    # pro-install: package + loaded DLL hashes
    $null = Invoke-Step 'pro-install' {
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Package not found: $packagePath" }
        if (-not $pro.realVersion) { throw 'ArcGIS Pro is not installed (HKLM:\SOFTWARE\ESRI\ArcGISPro).' }
        $script:packageInfo = [ordered]@{ path = 'artifacts/ArcGISProMCP.AddIn.esriAddinX'; sha256 = (Get-Sha256Hex $packagePath) }
        $script:builtDlls = @(Get-PackageDllHash $packagePath)
        $script:loadedDlls = @(Get-LoadedDllHash)
        $problems = @(Compare-DllHash $script:builtDlls $script:loadedDlls)
        if ($problems.Count -gt 0) { throw ('Loaded add-in does not match the package: ' + ($problems -join '; ')) }
        @{ detail = "ArcGIS Pro $($pro.realVersion) ($($pro.exeProductVersion)); $($script:builtDlls.Count) add-in DLLs match the package"; evidence = @() }
    }

    # host-probe: read-only state snapshot, pipe and project resolution
    $hostReady = $false
    if (-not $resolvedPipe) {
        Add-Step -Name 'host-probe' -Status 'blocked' -Detail "no host pipe: $pipeSource"
    }
    elseif ($pipeProblem) {
        Add-Step -Name 'host-probe' -Status 'blocked' -Detail "refused: $pipeProblem"
    }
    else {
        $hostReady = Invoke-Step 'host-probe' {
            $stateDirectory = Join-Path $OutputDirectory 'host-probe'
            $script:state = Invoke-DemoRunnerCall 'system.get_state' (Join-Path $stateDirectory 'state.request.json') (Join-Path $stateDirectory 'state.json')
            $capabilities = @()
            if ($script:state.PSObject.Properties['workspace'] -and $script:state.workspace.PSObject.Properties['capabilities']) { $capabilities = @($script:state.workspace.capabilities) }
            $script:autonomousMode = @($capabilities | Where-Object { $_.id -eq 'autonomous-control' -and $_.available }).Count -gt 0
            $uri = $null
            if ($script:state.workspace.PSObject.Properties['project'] -and $script:state.workspace.project) { $uri = [string]$script:state.workspace.project.uri }
            $script:projectPath = ConvertTo-LocalPath $uri
            if (Test-Path -LiteralPath $auditPath -PathType Leaf) { $script:auditOffset = (Get-Item -LiteralPath $auditPath).Length } else { $script:auditOffset = 0 }
            @{ detail = "pid $($script:state.processId), $($script:state.operationCount) operations, autonomousMode=$($script:autonomousMode), project $($script:projectPath)"; evidence = @('host-probe/state.json') }
        }
    }

    foreach ($section in $Sections) {
        $info = $sectionCatalog[$section]
        $mutates = [bool]$info.mutatesProject
        $autonomous = [bool]$info.requiresAutonomousMode
        if ($refusals.Contains($section)) {
            Add-Step -Name $section -Status 'blocked' -Detail ('refused: ' + $refusals[$section]) -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        if (-not $script:childShell) {
            Add-Step -Name $section -Status 'blocked' -Detail 'PowerShell 7 (pwsh) not found' -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        if (-not $hostReady) {
            Add-Step -Name $section -Status 'blocked' -Detail 'host-probe did not pass' -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        if ($mutates -and -not (Test-PathUnderRoot $projectPath $DisposableRoot)) {
            Add-Step -Name $section -Status 'blocked' -Detail "refused: open project '$projectPath' is not under disposable root '$DisposableRoot'" -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        if ($info.Contains('requiresDefaultMode') -and $info.requiresDefaultMode -and $autonomousMode) {
            Add-Step -Name $section -Status 'blocked' -Detail 'refused: the host reports autonomous-control; this section needs DEFAULT mode (restart Pro without ARCGIS_PRO_MCP_AUTONOMOUS_MODE)' -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        if ($autonomous -and -not $autonomousMode) {
            Add-Step -Name $section -Status 'blocked' -Detail 'refused: the host does not report the autonomous-control capability; restart Pro with ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true' -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            continue
        }
        $sectionDirectory = Join-Path $OutputDirectory $section
        $null = New-Item -ItemType Directory -Path $sectionDirectory -Force
        switch ($section) {
            'smoke' {
                $null = Invoke-Step $section {
                    $tokens = @('-Configuration', (Format-PsLiteral $Configuration), '-ApprovalProbe')
                    if ($ImageUri) { $tokens += @('-ImageUri', (Format-PsLiteral $ImageUri)) }
                    $resultPath = Join-Path $sectionDirectory 'result.json'
                    Invoke-ChildScript -Name $section -ScriptName $info.script -ArgumentTokens $tokens -LogDirectory $logDirectory -ResultJsonPath $resultPath
                    $smoke = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                    @{ detail = "protocol $($smoke.Protocol), $($smoke.Tools) tools, pendingCancelReview=$($smoke.PendingCancelReview), imageBlock=$($smoke.ImageBlock)"; evidence = @("$section/result.json") }
                } -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            }
            'feature-gp-arcpy' {
                $null = Invoke-Step $section {
                    $evidenceDirectory = Join-Path $sectionDirectory 'evidence'
                    $dataDirectory = Join-Path $DisposableRoot ('acceptance-data-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
                    $tokens = @('-PipeName', (Format-PsLiteral $resolvedPipe), '-Configuration', (Format-PsLiteral $Configuration),
                        '-EvidenceDirectory', (Format-PsLiteral $evidenceDirectory), '-DataDirectory', (Format-PsLiteral $dataDirectory))
                    Invoke-ChildScript -Name $section -ScriptName $info.script -ArgumentTokens $tokens -LogDirectory $logDirectory
                    $summaryFile = Join-Path $evidenceDirectory 'summary.json'
                    $featureSummary = Get-Content -LiteralPath $summaryFile -Raw | ConvertFrom-Json
                    if (-not $featureSummary.autonomousControl) { throw 'Harness summary does not record autonomous control.' }
                    @{ detail = "ArcPy $($featureSummary.arcPy.version); feature CRUD, metadata, $($featureSummary.geoprocessing.bufferRuns) buffer runs; autonomous mode"; evidence = @("$section/evidence") }
                } -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            }
            'stress' {
                $null = Invoke-Step $section {
                    $tokens = @('-RunsPerCase', [string]$RunsPerCase, '-Configuration', (Format-PsLiteral $Configuration), '-OutputDirectory', (Format-PsLiteral $sectionDirectory))
                    Invoke-ChildScript -Name $section -ScriptName $info.script -ArgumentTokens $tokens -LogDirectory $logDirectory
                    foreach ($png in @(Get-ChildItem -LiteralPath $sectionDirectory -Filter 'final-layout.png' -File -Recurse)) { $null = $pngCandidates.Add($png.FullName) }
                    $cases = @(Get-Content -LiteralPath (Join-Path $sectionDirectory 'summary.json') -Raw | ConvertFrom-Json)
                    @{ detail = "$($cases.Count) workflows x $RunsPerCase runs; layouts inspected; captures non-blank"; evidence = @("$section/summary.json") }
                } -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            }
            'operations' {
                $null = Invoke-Step $section {
                    $tokens = @('-PipeName', (Format-PsLiteral $resolvedPipe), '-Configuration', (Format-PsLiteral $Configuration),
                        '-DisposableRoot', (Format-PsLiteral $DisposableRoot), '-EvidenceDirectory', (Format-PsLiteral $sectionDirectory))
                    # The operator reads the card prompts on this console, so stdout is not captured.
                    $exit = Invoke-ChildScript -Name $section -ScriptName $info.script -ArgumentTokens $tokens -LogDirectory $logDirectory -ShowConsole -AllowNonZeroExit
                    $summaryFile = Join-Path $sectionDirectory 'summary.json'
                    if (-not (Test-Path -LiteralPath $summaryFile -PathType Leaf)) { throw "$($info.script) exited with code $exit and wrote no summary.json; see logs/$section.stderr.log." }
                    $operations = Get-Content -LiteralPath $summaryFile -Raw | ConvertFrom-Json
                    $detail = "$($operations.coverage.covered)/$($operations.coverage.total) operations covered; cases $($operations.cases.passed) passed, $($operations.cases.failed) failed, $($operations.cases.skipped) skipped; cards $($operations.approvals.approved) approved, $($operations.approvals.denied) denied, $($operations.approvals.skipped) skipped; $(@($operations.messageAudit.flagged).Count) messages flagged"
                    if ($exit -ne 0 -or -not $operations.allPassed) {
                        $uncovered = @($operations.coverage.uncovered) -join ', '
                        throw "$detail. Not covered: $uncovered. See $section/summary.json and $section/errors.md."
                    }
                    @{ detail = $detail; evidence = @("$section/summary.json", "$section/errors.md", "$section/results") }
                } -MutatesProject $mutates -RequiresAutonomousMode $autonomous
            }
        }
    }
}
finally {
    $env:ARCGIS_PRO_MCP_PIPE = $previousPipe
}

# ---------------------------------------------------------------- audit records appended during the run

if ($null -ne $auditOffset -and (Test-Path -LiteralPath $auditPath -PathType Leaf)) {
    $stream = [IO.File]::Open($auditPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $start = [long]$auditOffset
        if ($stream.Length -lt $start) { $start = 0 } # rotated during the run: keep the whole active file
        $null = $stream.Seek($start, [IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ([int]($stream.Length - $start))
        $read = 0
        while ($read -lt $buffer.Length) {
            $n = $stream.Read($buffer, $read, $buffer.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'audit.jsonl'), $buffer)
    }
    finally { $stream.Dispose() }
}

# ---------------------------------------------------------------- manifest, summary, checksums

function Get-StepStatus([string]$Name) {
    $match = @($script:steps | Where-Object { $_.name -eq $Name })
    if ($match.Count -eq 0) { return $null }
    return $match[0].status
}

$selectedStepNames = @('verify', 'pro-install', 'host-probe') + $Sections
$allPassed = @($selectedStepNames | Where-Object { (Get-StepStatus $_) -ne 'passed' }).Count -eq 0

$manifest = [ordered]@{
    schemaVersion = 1
    date = $startedAtUtc.ToLocalTime().ToString('yyyy-MM-dd')
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    sha = $gitSha
    dirty = $dirty
    describe = $describe
    version = $version
    dotnet = $dotnetVersion
    powershell = $PSVersionTable.PSVersion.ToString()
    operator = $Operator
    pro = [ordered]@{
        installDir = [string]$pro.installDir
        realVersion = [string]$pro.realVersion
        exeProductVersion = [string]$pro.exeProductVersion
        runningProcessIds = @($pro.runningProcessIds)
        addInId = $addInId
    }
    dlls = [ordered]@{
        built = @($builtDlls)
        loaded = @($loadedDlls | ForEach-Object { [ordered]@{ name = $_.name; sha256 = $_.sha256 } })
    }
    sections = @($script:steps)
    autonomousMode = [bool]$autonomousMode
    allPassed = [bool]$allPassed
}
if (@($VisuallyInspected).Count -gt 0) { $manifest['visuallyInspected'] = @($VisuallyInspected) }
if ($VisualNotesSource) { $manifest['visualNotesSource'] = $VisualNotesSource }
$runNoteList = @($RunNotes | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ })
if ($runNoteList.Count -gt 0) { $manifest['runNotes'] = $runNoteList }
if ($tag) { $manifest['tag'] = $tag }
# Facts that exist only when the corresponding step produced them are omitted, never faked.
if ($packageInfo) { $manifest['package'] = $packageInfo }
elseif (Test-Path -LiteralPath $packagePath -PathType Leaf) { $manifest['package'] = [ordered]@{ path = 'artifacts/ArcGISProMCP.AddIn.esriAddinX'; sha256 = (Get-Sha256Hex $packagePath) } }
if ($state -and $state.PSObject.Properties['processId']) { $manifest.pro['hostProcessId'] = [int]$state.processId }
if ($state -and $state.PSObject.Properties['operationCount']) { $manifest.pro['operationCount'] = [int]$state.operationCount }

function ConvertTo-SummaryMarkdown {
    $lines = New-Object System.Collections.ArrayList
    $null = $lines.Add("# Acceptance evidence $($manifest.date) ($sha7)")
    $null = $lines.Add('')
    $tick = [string][char]96
    $commitLine = '- Commit: ' + $tick + $gitSha + $tick
    if ($dirty) { $commitLine += ' (DIRTY working tree)' }
    if ($tag) { $commitLine += ', tag ' + $tick + $tag + $tick }
    $null = $lines.Add($commitLine)
    $null = $lines.Add("- Version: $version; .NET SDK $dotnetVersion; operator: $Operator")
    $hostPid = 'not probed'
    if ($manifest.pro.Contains('hostProcessId')) { $hostPid = [string]$manifest.pro['hostProcessId'] }
    $null = $lines.Add("- ArcGIS Pro: $($pro.realVersion) (ArcGISPro.exe $($pro.exeProductVersion)); host PID $hostPid")
    if ($manifest.Contains('package')) { $null = $lines.Add("- Package SHA-256: " + $tick + $manifest['package'].sha256 + $tick) }
    else { $null = $lines.Add('- Package SHA-256: not available (package missing)') }
    $null = $lines.Add("- Autonomous mode on host: $autonomousMode")
    $null = $lines.Add("- All selected steps passed: $allPassed")
    $null = $lines.Add('')
    $null = $lines.Add('## Implemented')
    $null = $lines.Add('')
    if ($manifest.pro.Contains('operationCount')) { $null = $lines.Add("- Add-in $addInId version $version exposing $($manifest.pro['operationCount']) host operations (as reported by system.get_state).") }
    else { $null = $lines.Add("- Add-in $addInId version $version; host operation count not reported (host-probe did not pass).") }
    $null = $lines.Add('')
    $null = $lines.Add('## Portable-tested')
    $null = $lines.Add('')
    $null = $lines.Add("- verify ($(Get-StepStatus 'verify')): Release build, every tests/*.Tests project, package contents.")
    $null = $lines.Add('')
    $null = $lines.Add('## Live-tested')
    $null = $lines.Add('')
    $live = @($script:steps | Where-Object { $_.status -eq 'passed' -and $_.name -in @('pro-install', 'host-probe', 'smoke', 'feature-gp-arcpy', 'stress', 'operations') })
    if ($live.Count -eq 0) { $null = $lines.Add('- Nothing.') }
    foreach ($step in $live) {
        $note = ''
        if ($step.requiresAutonomousMode) { $note = ' **(autonomous mode)**' }
        $null = $lines.Add("- $($step.name)$note - $($step.detail)")
    }
    $null = $lines.Add('')
    $null = $lines.Add('## Visually inspected')
    $null = $lines.Add('')
    $inspected = @()
    if ($manifest.Contains('visuallyInspected')) { $inspected = @($manifest['visuallyInspected']) }
    if ($inspected.Count -eq 0) { $null = $lines.Add('- Nothing recorded by the operator (-VisuallyInspected). Automated PNG checks are not visual inspection.') }
    foreach ($item in $inspected) { $null = $lines.Add("- $item") }
    if ($manifest.Contains('visualNotesSource')) { $null = $lines.Add('') ; $null = $lines.Add("Source of these notes: $($manifest['visualNotesSource'])") }
    $null = $lines.Add('')
    if ($manifest.Contains('runNotes')) {
        $null = $lines.Add('## Run notes')
        $null = $lines.Add('')
        foreach ($item in @($manifest['runNotes'])) { $null = $lines.Add("- $item") }
        $null = $lines.Add('')
    }
    $null = $lines.Add('## Blocked or not run')
    $null = $lines.Add('')
    $blocked = @($script:steps | Where-Object { $_.status -ne 'passed' })
    $notSelected = @($sectionCatalog.Keys) | Where-Object { $_ -notin $Sections }
    if ($blocked.Count -eq 0 -and @($notSelected).Count -eq 0) { $null = $lines.Add('- Nothing.') }
    foreach ($step in $blocked) { $null = $lines.Add("- $($step.name) ($($step.status)): $($step.detail)") }
    foreach ($name in @($notSelected)) { $null = $lines.Add("- $($name): not selected for this run.") }
    $null = $lines.Add('')
    $null = $lines.Add('Standing limits (docs/deployment.md, Known limits) still apply; this record does not lift them.')
    return (($lines -join "`n") + "`n")
}

Write-Utf8File (Join-Path $OutputDirectory 'manifest.json') (ConvertTo-JsonText $manifest)
Write-Utf8File (Join-Path $OutputDirectory 'summary.md') (ConvertTo-SummaryMarkdown)

function Test-EvidencePathLike([string]$Text) {
    <# Same rule as AcceptanceManifestTests: a note's leading token names a file when it has a separator or an extension. #>
    return ($Text -match '[\\/]') -or ($Text -match '\.[A-Za-z0-9]{1,5}$')
}

function ConvertTo-EvidenceKey([string]$Path) {
    <# A lookup key for a working-evidence path: relative to the output directory, '/' separators, lower case. #>
    $candidate = $Path.Trim()
    if ([IO.Path]::IsPathRooted($candidate)) {
        if (-not (Test-PathUnderRoot $candidate $OutputDirectory)) { return $candidate.Replace('\', '/').ToLowerInvariant() }
        $candidate = Get-RelativePath $OutputDirectory $candidate
    }
    $candidate = $candidate.Replace('\', '/')
    while ($candidate.StartsWith('./')) { $candidate = $candidate.Substring(2) }
    return $candidate.TrimEnd('/').ToLowerInvariant()
}

function Resolve-CommittedEvidence([hashtable]$Map, [string]$Path) {
    <# The committed paths for one working-evidence path; a folder maps to every committed file under it. #>
    $key = ConvertTo-EvidenceKey $Path
    if ($Map.ContainsKey($key)) { return @($Map[$key]) }
    $prefix = $key + '/'
    return @($Map.Keys | Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } | Sort-Object | ForEach-Object { $Map[$_] })
}

function Write-Sha256Sum([string]$Directory) {
    $sums = Join-Path $Directory 'SHA256SUMS'
    $entries = @(Get-ChildItem -LiteralPath $Directory -File -Recurse |
        Where-Object { -not [string]::Equals($_.FullName, $sums, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { [pscustomobject]@{ Relative = (Get-RelativePath $Directory $_.FullName); FullName = $_.FullName } } |
        Sort-Object Relative)
    $text = (@($entries | ForEach-Object { '{0}  {1}' -f (Get-Sha256Hex $_.FullName), $_.Relative }) -join "`n") + "`n"
    Write-Utf8File $sums $text
}

function Copy-BoundedPng([string]$Source, [string]$Destination) {
    if ((Get-Item -LiteralPath $Source).Length -le $maxScreenshotBytes) {
        Copy-Item -LiteralPath $Source -Destination $Destination
        return $true
    }
    try {
        Add-Type -AssemblyName System.Drawing
        $image = [System.Drawing.Image]::FromFile($Source)
        try {
            foreach ($width in @(1600, 1200, 900, 600)) {
                $height = [int][Math]::Round($image.Height * $width / [double]$image.Width)
                $bitmap = New-Object System.Drawing.Bitmap($width, $height)
                try {
                    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                    try {
                        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                        $graphics.DrawImage($image, 0, 0, $width, $height)
                    }
                    finally { $graphics.Dispose() }
                    $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
                }
                finally { $bitmap.Dispose() }
                if ((Get-Item -LiteralPath $Destination).Length -le $maxScreenshotBytes) { return $true }
            }
        }
        finally { $image.Dispose() }
    }
    catch { Write-Verbose $_ }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    return $false
}

Write-Sha256Sum $OutputDirectory
Write-Host ''
Write-Host "Working evidence: $OutputDirectory" -ForegroundColor Cyan

if ($Commit) {
    if (-not $allPassed) {
        Write-Host '-Commit refused: not every selected step passed. Working evidence is kept under artifacts/ for diagnosis.' -ForegroundColor Red
        exit 1
    }
    $null = New-Item -ItemType Directory -Path $commitFolder -Force
    $copied = New-Object System.Collections.ArrayList
    $skipped = New-Object System.Collections.ArrayList
    # Working-evidence key -> committed relative path, used to rewrite every evidence path below.
    $committedPaths = @{}
    foreach ($relative in @('host-probe/state.json', 'smoke/result.json', 'stress/summary.json', 'audit.jsonl')) {
        $source = Join-Path $OutputDirectory $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        if ((Get-Item -LiteralPath $source).Length -gt $maxCopiedJsonBytes) { $null = $skipped.Add("$relative (over 1 MB)"); continue }
        $target = Join-Path $commitFolder $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
        Copy-Item -LiteralPath $source -Destination $target
        $null = $copied.Add($relative)
        $committedPaths[(ConvertTo-EvidenceKey $relative)] = $relative
    }
    $featureEvidence = Join-Path $OutputDirectory 'feature-gp-arcpy\evidence'
    if (Test-Path -LiteralPath $featureEvidence -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $featureEvidence -Filter '*.json' -File)) {
            $relative = 'feature-gp-arcpy/' + $file.Name
            if ($file.Length -gt $maxCopiedJsonBytes) { $null = $skipped.Add("$relative (over 1 MB)"); continue }
            $target = Join-Path $commitFolder $relative
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $null = $copied.Add($relative)
            $committedPaths[(ConvertTo-EvidenceKey $file.FullName)] = $relative
        }
    }
    $operationsEvidence = Join-Path $OutputDirectory 'operations'
    if (Test-Path -LiteralPath $operationsEvidence -PathType Container) {
        # summary.json, errors.md and every request/result pair; JSON over 1 MB is left out and listed.
        $operationFiles = @(@('summary.json', 'errors.md') | ForEach-Object { Join-Path $operationsEvidence $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object { Get-Item -LiteralPath $_ })
        $resultsFolder = Join-Path $operationsEvidence 'results'
        if (Test-Path -LiteralPath $resultsFolder -PathType Container) { $operationFiles += @(Get-ChildItem -LiteralPath $resultsFolder -Filter '*.json' -File | Sort-Object Name) }
        foreach ($file in $operationFiles) {
            $relative = Get-RelativePath $OutputDirectory $file.FullName
            if ($file.Length -gt $maxCopiedJsonBytes) { $null = $skipped.Add("$relative (over 1 MB)"); continue }
            $target = Join-Path $commitFolder $relative
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
            $null = $copied.Add($relative)
            $committedPaths[(ConvertTo-EvidenceKey $relative)] = $relative
        }
    }
    $pngs = @(@($Screenshot | ForEach-Object { [IO.Path]::GetFullPath($_) }) + @($pngCandidates))
    $index = 0
    foreach ($png in $pngs) {
        if ($index -ge $maxScreenshots) { $null = $skipped.Add("$png (more than $maxScreenshots images)"); continue }
        $name = if ($png -like '*final-layout.png') { 'layout-' + (Split-Path -Leaf (Split-Path -Parent $png)) + '.png' } else { 'screenshot-{0:D2}.png' -f ($index + 1) }
        $relative = 'images/' + $name
        $target = Join-Path $commitFolder $relative
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
        if (Copy-BoundedPng $png $target) {
            $null = $copied.Add($relative)
            $committedPaths[(ConvertTo-EvidenceKey $png)] = $relative
            $index++
        }
        else { $null = $skipped.Add("$png (could not fit in 500 KB)") }
    }
    # The committed stress summary names each final layout capture by its committed image
    # (images/layout-<case>.png) instead of the working-evidence path under artifacts/.
    $stressSummary = Join-Path $commitFolder 'stress\summary.json'
    if (Test-Path -LiteralPath $stressSummary -PathType Leaf) {
        $stressCases = @(Get-Content -LiteralPath $stressSummary -Raw | ConvertFrom-Json | ForEach-Object { $_ })
        foreach ($case in $stressCases) {
            if (-not $case.PSObject.Properties['capture'] -or -not $case.capture) { continue }
            $capture = [string]$case.capture
            $resolved = @(Resolve-CommittedEvidence $committedPaths $capture)
            if ($resolved.Count -eq 1) { $case.capture = $resolved[0] }
            else {
                $working = $capture
                if ([IO.Path]::IsPathRooted($working) -and (Test-PathUnderRoot $working $OutputDirectory)) { $working = Get-RelativePath $OutputDirectory $working }
                elseif ([IO.Path]::IsPathRooted($working)) { $working = Split-Path -Leaf $working }
                $case.capture = 'not committed: ' + $working.Replace('\', '/')
            }
        }
        # -InputObject keeps a one-case summary an array (piping would unwrap it).
        Write-Utf8File $stressSummary (((ConvertTo-Json -InputObject $stressCases -Depth 20) -replace "`r`n", "`n") + "`n")
    }
    $manifest['evidence'] = @($copied)
    # List only files this folder contains, at their committed paths. Working evidence under
    # artifacts/ keeps the original paths in its own manifest.json.
    $manifest['sections'] = @($script:steps | ForEach-Object {
            $section = [ordered]@{}
            foreach ($entry in $_.GetEnumerator()) { $section[$entry.Key] = $entry.Value }
            $section['evidence'] = @(@($_.evidence) | ForEach-Object { Resolve-CommittedEvidence $committedPaths ([string]$_) } | Select-Object -Unique)
            $section
        })
    if ($manifest.Contains('visuallyInspected')) {
        $manifest['visuallyInspected'] = @(@($manifest['visuallyInspected']) | ForEach-Object {
                $note = [string]$_
                $separator = $note.IndexOf(': ', [StringComparison]::Ordinal)
                if ($separator -le 0) { return $note }
                $token = $note.Substring(0, $separator)
                if (-not (Test-EvidencePathLike $token)) { return $note }
                $resolved = @(Resolve-CommittedEvidence $committedPaths $token)
                if ($resolved.Count -eq 1) { return $resolved[0] + $note.Substring($separator) }
                return 'not committed: ' + $note
            })
    }
    $manifest['evidencePathsRelative'] = $true
    if ($skipped.Count -gt 0) { $manifest['evidenceSkipped'] = @($skipped) }
    Write-Utf8File (Join-Path $commitFolder 'manifest.json') (ConvertTo-JsonText $manifest)
    Write-Utf8File (Join-Path $commitFolder 'summary.md') (ConvertTo-SummaryMarkdown)
    Write-Sha256Sum $commitFolder
    Write-Host "Committed evidence folder written: $commitFolder" -ForegroundColor Green
    Write-Host 'Review it (paths, audit records), then git add it yourself. Nothing was staged or committed.' -ForegroundColor Green
}

if (-not $allPassed) { exit 1 }
