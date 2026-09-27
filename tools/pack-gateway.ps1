<#
.SYNOPSIS
    Packs the stdio gateway as the ArcGISProMCP.Gateway NuGet McpServer tool and verifies the package.

.DESCRIPTION
    Runs `dotnet pack` on src/ArcGISProMCP.Server into artifacts\packages and checks that the
    .nupkg declares PackageType McpServer, carries .mcp/server.json at the package root, has
    server.json versions equal to the package version, and bundles every source skill manifest
    byte-for-byte. Nothing is pushed or published. Compatible with Windows PowerShell 5.1.
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $repoRoot 'artifacts\packages'
$project = Join-Path $repoRoot 'src\ArcGISProMCP.Server\ArcGISProMCP.Server.csproj'
$serverJsonPath = Join-Path $repoRoot 'src\ArcGISProMCP.Server\.mcp\server.json'
$packageId = 'ArcGISProMCP.Gateway'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-ZipEntryBytes {
    param([System.IO.Compression.ZipArchiveEntry]$Entry)
    $stream = $Entry.Open()
    try {
        $buffer = New-Object System.IO.MemoryStream
        try {
            $stream.CopyTo($buffer)
            return , $buffer.ToArray()
        }
        finally { $buffer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
}

$props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
$versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'Directory.Build.props does not define a version.'
}
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') { throw "Unsafe version '$version'." }

# Fail before packing if the registry metadata drifted from the package version.
$serverJson = Get-Content -LiteralPath $serverJsonPath -Raw | ConvertFrom-Json
if ($serverJson.version -ne $version) {
    throw "server.json version '$($serverJson.version)' does not match Directory.Build.props version '$version'."
}
$nugetEntries = @($serverJson.packages | Where-Object { $_.registryType -eq 'nuget' -and $_.identifier -eq $packageId })
if ($nugetEntries.Count -ne 1) { throw "server.json must contain exactly one nuget package entry for '$packageId'." }
if ($nugetEntries[0].version -ne $version) {
    throw "server.json package version '$($nugetEntries[0].version)' does not match '$version'."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$nupkg = Join-Path $outputRoot "$packageId.$version.nupkg"
if (Test-Path -LiteralPath $nupkg) { Remove-Item -LiteralPath $nupkg -Force }

Push-Location $repoRoot
try {
    & dotnet pack $project -c $Configuration -o $outputRoot
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

if (-not (Test-Path -LiteralPath $nupkg)) { throw "Expected package '$nupkg' was not produced." }

$zip = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    $nuspecEntry = @($zip.Entries | Where-Object { $_.FullName -eq "$packageId.nuspec" })
    if ($nuspecEntry.Count -ne 1) { throw 'Package has no nuspec.' }
    $nuspecText = [Text.Encoding]::UTF8.GetString((Read-ZipEntryBytes $nuspecEntry[0])).TrimStart([char]0xFEFF)
    $nuspec = [xml]$nuspecText
    $ns = New-Object System.Xml.XmlNamespaceManager($nuspec.NameTable)
    $ns.AddNamespace('n', $nuspec.DocumentElement.NamespaceURI)
    $packageTypes = @($nuspec.SelectNodes('/n:package/n:metadata/n:packageTypes/n:packageType', $ns) | ForEach-Object { $_.GetAttribute('name') })
    if ($packageTypes -notcontains 'McpServer') { throw "Package types are '$($packageTypes -join ', ')'; McpServer is missing." }
    if ($packageTypes -notcontains 'DotnetTool') { throw "Package types are '$($packageTypes -join ', ')'; DotnetTool is missing." }
    $packedVersion = $nuspec.SelectSingleNode('/n:package/n:metadata/n:version', $ns).InnerText
    if ($packedVersion -ne $version) { throw "Packed version '$packedVersion' does not match '$version'." }

    $packedServerJson = @($zip.Entries | Where-Object { $_.FullName -eq '.mcp/server.json' })
    if ($packedServerJson.Count -ne 1) { throw 'Package does not contain .mcp/server.json.' }
    if ((Get-Sha256Hex (Read-ZipEntryBytes $packedServerJson[0])) -ne
        (Get-Sha256Hex ([IO.File]::ReadAllBytes($serverJsonPath)))) {
        throw 'Packed .mcp/server.json differs from source.'
    }

    $toolSettings = @($zip.Entries | Where-Object { $_.FullName -like 'tools/*/any/DotnetToolSettings.xml' })
    if ($toolSettings.Count -ne 1) { throw 'Package does not contain DotnetToolSettings.xml.' }
    $toolRoot = $toolSettings[0].FullName.Substring(0, $toolSettings[0].FullName.Length - 'DotnetToolSettings.xml'.Length)
    $settings = [xml]([Text.Encoding]::UTF8.GetString((Read-ZipEntryBytes $toolSettings[0])).TrimStart([char]0xFEFF))
    $command = $settings.SelectSingleNode('//Command')
    if ($null -eq $command -or $command.GetAttribute('Name') -ne 'arcgis-pro-mcp') {
        throw 'Tool command name is not arcgis-pro-mcp.'
    }

    $sourceSkills = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'skills') -File -Filter '*.skill.json' | Sort-Object Name)
    if ($sourceSkills.Count -eq 0) { throw 'No source skills found.' }
    $packedSkills = @($zip.Entries | Where-Object { $_.FullName -like "$($toolRoot)skills/*.skill.json" })
    if ($packedSkills.Count -ne $sourceSkills.Count) {
        throw "Package has $($packedSkills.Count) skill manifests; source has $($sourceSkills.Count)."
    }
    foreach ($skill in $sourceSkills) {
        $entry = @($packedSkills | Where-Object { $_.Name -eq $skill.Name })
        if ($entry.Count -ne 1) { throw "Skill '$($skill.Name)' is missing from the package." }
        if ((Get-Sha256Hex (Read-ZipEntryBytes $entry[0])) -ne (Get-FileHash -LiteralPath $skill.FullName -Algorithm SHA256).Hash) {
            throw "Packed skill '$($skill.Name)' differs from source."
        }
    }
}
finally { $zip.Dispose() }

$hash = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
Write-Output "Packed $nupkg"
Write-Output "SHA256 $hash"
Write-Output 'Verified: PackageType McpServer, .mcp/server.json, tool command arcgis-pro-mcp, bundled skills. Not published.'
