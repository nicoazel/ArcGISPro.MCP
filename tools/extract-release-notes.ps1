#Requires -Version 7.0
<#
.SYNOPSIS
    Writes GitHub release notes for one version from CHANGELOG.md.

.DESCRIPTION
    Copies the body of the `## [<Version>]` section of CHANGELOG.md (up to the next `## [` heading),
    rewrites repository-relative Markdown links to absolute links at -Ref, and appends a pointer to
    the latest committed acceptance evidence folder under docs/acceptance. Fails when the section is
    missing or empty, or, with -RequireReleaseDate, when its heading has no yyyy-MM-dd date (a final
    release must not ship notes from a section still marked "unreleased").

    Used by .github/workflows/release.yml; safe to run locally. It only reads the repository and
    writes -OutputPath.

.EXAMPLE
    ./tools/extract-release-notes.ps1 -Version 0.3.1 -Ref v0.3.1 -OutputPath artifacts/release-notes.md
#>
[CmdletBinding()]
param(
    # Version without the leading v and without a prerelease suffix, for example 0.3.1.
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version,

    # Git ref (tag or commit) that relative links resolve against.
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._/-]*$')]
    [string]$Ref,

    [Parameter(Mandatory)]
    [string]$OutputPath,

    [string]$RepositoryUrl = 'https://github.com/nicoazel/ArcGISPro.MCP',

    # Require a dated heading (`## [x.y.z] - yyyy-MM-dd`). Final releases set it; release candidates do not.
    [switch]$RequireReleaseDate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$repositoryRoot = $RepositoryUrl.TrimEnd('/')

$lines = [IO.File]::ReadAllLines($changelogPath)
$headingPattern = '^## \[' + [regex]::Escape($Version) + '\](?:\s+-\s+(?<date>.+?))?\s*$'
$start = -1
$date = $null
for ($i = 0; $i -lt $lines.Length; $i++) {
    $match = [regex]::Match($lines[$i], $headingPattern)
    if ($match.Success) {
        if ($start -ge 0) { throw "CHANGELOG.md has more than one '## [$Version]' section." }
        $start = $i
        if ($match.Groups['date'].Success) { $date = $match.Groups['date'].Value }
    }
}
if ($start -lt 0) { throw "CHANGELOG.md has no '## [$Version]' section." }
if ($RequireReleaseDate -and ($null -eq $date -or $date -notmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}$')) {
    throw "CHANGELOG.md section '## [$Version]' must carry a release date (yyyy-MM-dd) for a final release; found '$date'."
}

$end = $lines.Length
for ($i = $start + 1; $i -lt $lines.Length; $i++) {
    # The next version section, or the link reference block at the end of the file.
    if ($lines[$i] -match '^## \[' -or $lines[$i] -match '^\[[^\]]+\]:\s') { $end = $i; break }
}
$body = ''
if ($end -gt $start + 1) { $body = ($lines[($start + 1)..($end - 1)] -join "`n").Trim() }
if ([string]::IsNullOrWhiteSpace($body)) { throw "CHANGELOG.md section '## [$Version]' is empty." }

# Relative links resolve against the repository at -Ref; absolute URLs and in-page anchors are kept.
$body = [regex]::Replace($body, '\]\((?!https?://|mailto:|#)(?:\./)?(?<path>[^)\s]+)\)', {
        param($linkMatch)
        $path = $linkMatch.Groups['path'].Value
        $parts = $path.Split('#', 2)
        $kind = if ($parts[0].EndsWith('/')) { 'tree' } else { 'blob' }
        "]($repositoryRoot/$kind/$Ref/$path)"
    })

$acceptanceRoot = Join-Path $repoRoot 'docs/acceptance'
$evidence = @()
if (Test-Path -LiteralPath $acceptanceRoot -PathType Container) {
    $evidence = @(Get-ChildItem -LiteralPath $acceptanceRoot -Directory |
            Where-Object { $_.Name -match '^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9a-f]{7,40}$' } |
            Sort-Object Name)
}
$footer = [Collections.Generic.List[string]]::new()
$footer.Add('---')
$footer.Add('')
if ($evidence.Count -gt 0) {
    $latest = $evidence[-1].Name
    $commit = $latest.Substring(11)
    $evidenceLine = ('Latest committed live acceptance evidence: [`docs/acceptance/{0}`]({1}/blob/{2}/docs/acceptance/{0}/summary.md) ' +
        '(commit `{3}`). It covers only the commit, ArcGIS Pro version and sections it names.') -f $latest, $repositoryRoot, $Ref, $commit
    $footer.Add($evidenceLine)
}
else {
    $footer.Add('No live acceptance evidence is committed for this release.')
}
$footer.Add('')
$verifyLine = ('Unsigned development preview. Verify the bundle against `SHA256SUMS` and its inner `checksums.sha256`, ' +
    'then follow [deployment]({0}/blob/{1}/docs/deployment.md).') -f $repositoryRoot, $Ref
$footer.Add($verifyLine)

$notes = $body + "`n`n" + ($footer -join "`n") + "`n"
$outputFullPath = [IO.Path]::GetFullPath($OutputPath, (Get-Location).Path)
$outputDirectory = Split-Path -Parent $outputFullPath
if (-not (Test-Path -LiteralPath $outputDirectory)) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
[IO.File]::WriteAllText($outputFullPath, $notes, [Text.UTF8Encoding]::new($false))
Write-Output $outputFullPath
