#Requires -Version 7.0
# Uses .NET APIs that Windows PowerShell 5.1 lacks (for example IO.Path.GetRelativePath); run with pwsh.
[CmdletBinding()]
param(
    # Passed to verify-release.ps1: build and package without re-running the tests. CI uses it
    # after the build-test job has run them; local runs keep the default (tests run).
    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repoRoot 'artifacts\releases'
$rid = 'win-x64'
$status = 'development-preview'

function Remove-ReleaseTemporaryPath {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '',
        Justification = 'Private cleanup helper; it removes only validated staging paths under artifacts/releases.')]
    param([string]$Path, [string]$Prefix)
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = [IO.Directory]::GetParent($resolved)
    $expectedParent = [IO.Path]::GetFullPath($releaseRoot).TrimEnd('\')
    $leaf = [IO.Path]::GetFileName($resolved)
    if ($null -eq $parent -or
        -not [string]::Equals($parent.FullName.TrimEnd('\'), $expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw "Refusing to clean unexpected release path '$resolved'."
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

Push-Location $repoRoot
try {
    & ./tools/verify-release.ps1 -SkipTests:$SkipTests

    $props = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw)
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw 'Directory.Build.props does not define a release version.'
    }
    $version = $versionNode.InnerText.Trim()
    if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') { throw "Unsafe release version '$version'." }

    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    $name = "ArcGISProMCP-$version-$rid-$status"
    $stage = Join-Path $releaseRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
    $tempZip = Join-Path $releaseRoot ('.bundle-tmp-' + [Guid]::NewGuid().ToString('N') + '.zip')
    $bundleRoot = Join-Path $stage $name
    $bundlePath = Join-Path $releaseRoot ($name + '.zip')

    try {
        New-Item -ItemType Directory -Path $bundleRoot -Force | Out-Null
        $serverRoot = Join-Path $bundleRoot 'server'
        dotnet publish ./src/ArcGISProMCP.Server/ArcGISProMCP.Server.csproj -c Release -r $rid --self-contained false -p:PublishSingleFile=true -o $serverRoot
        if ($LASTEXITCODE -ne 0) { throw "Server publish failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath (Join-Path $serverRoot 'arcgis-pro-mcp.exe') -PathType Leaf)) {
            throw 'Published server executable is missing.'
        }

        $sourceSkills = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'skills') -File -Filter '*.skill.json' | Sort-Object Name)
        $publishedSkillRoot = Join-Path $serverRoot 'skills'
        $publishedSkills = if (Test-Path -LiteralPath $publishedSkillRoot) {
            @(Get-ChildItem -LiteralPath $publishedSkillRoot -File -Filter '*.skill.json' | Sort-Object Name)
        } else { @() }
        if ($sourceSkills.Count -eq 0 -or @(Compare-Object $sourceSkills.Name $publishedSkills.Name).Count -ne 0) {
            throw 'Published skills do not exactly match the source skill catalog.'
        }
        foreach ($skill in $sourceSkills) {
            $published = Join-Path $publishedSkillRoot $skill.Name
            if ((Get-FileHash -LiteralPath $skill.FullName -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash) {
                throw "Published skill '$($skill.Name)' differs from source."
            }
        }

        # Exercise the actual published binary and bundled skill lookup, without needing Pro.
        & ./tools/test-mcp.ps1 -ServerPath (Join-Path $serverRoot 'arcgis-pro-mcp.exe') -Offline

        $addIn = Join-Path $repoRoot 'artifacts\ArcGISProMCP.AddIn.esriAddinX'
        if (-not (Test-Path -LiteralPath $addIn -PathType Leaf)) { throw 'Verified add-in package is missing.' }
        Copy-Item -LiteralPath $addIn -Destination (Join-Path $bundleRoot 'ArcGISProMCP.AddIn.esriAddinX')
        Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $bundleRoot
        Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $bundleRoot

        $bundleDocs = Join-Path $bundleRoot 'docs'
        New-Item -ItemType Directory -Path $bundleDocs | Out-Null
        $docs = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'docs') -File -Filter '*.md' | Where-Object Name -ne 'ROADMAP.md' | Sort-Object Name)
        if ($docs.Count -eq 0) { throw 'No documentation was found to package.' }
        foreach ($doc in $docs) { Copy-Item -LiteralPath $doc.FullName -Destination $bundleDocs }

        $bundleWorkflows = Join-Path $bundleRoot 'workflows'
        New-Item -ItemType Directory -Path $bundleWorkflows | Out-Null
        $workflows = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'workflows') -File -Filter '*.workflow.json' | Sort-Object Name)
        if ($workflows.Count -eq 0) { throw 'No versioned workflows were found to package.' }
        foreach ($workflow in $workflows) { Copy-Item -LiteralPath $workflow.FullName -Destination $bundleWorkflows }

        $metadata = [ordered]@{
            schemaVersion = 1
            product = 'ArcGIS Pro MCP Studio'
            version = $version
            status = $status
            runtimeIdentifier = $rid
            serverDeployment = 'framework-dependent'
            generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            compatibility = [ordered]@{
                arcGISPro = '3.7.1'
                arcGISProSdk = '3.7.0.1901'
                serverRuntime = '.NET 10 x64'
            }
            acceptance = [ordered]@{
                manualApprovalUiRequired = $true
                latestBuildRequiresHostReload = $true
                liveFeatureMetadataAndGeoprocessingAcceptanceRequired = $true
                liveArcPyAcceptanceRequiredWhenEnabled = $true
            }
            signed = $false
        }
        $utf8 = [Text.UTF8Encoding]::new($false)
        [IO.File]::WriteAllText((Join-Path $bundleRoot 'release.json'), (($metadata | ConvertTo-Json -Depth 5) + [Environment]::NewLine), $utf8)

        $manifest = Join-Path $bundleRoot 'checksums.sha256'
        $files = @(
            Get-ChildItem -LiteralPath $bundleRoot -Recurse -File |
                Where-Object { -not [string]::Equals($_.FullName, $manifest, [StringComparison]::OrdinalIgnoreCase) } |
                ForEach-Object {
                    [pscustomobject]@{
                        Relative = [IO.Path]::GetRelativePath($bundleRoot, $_.FullName).Replace('\', '/')
                        FullName = $_.FullName
                    }
                } | Sort-Object Relative
        )
        $lines = @($files | ForEach-Object {
            '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Relative
        })
        [IO.File]::WriteAllLines($manifest, $lines, $utf8)
        foreach ($file in $files) {
            $line = @($lines | Where-Object { $_.EndsWith("  $($file.Relative)", [StringComparison]::Ordinal) })
            if ($line.Count -ne 1) { throw "Missing or ambiguous checksum for '$($file.Relative)'." }
            $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($line[0].Substring(0, 64) -ne $actual) { throw "Checksum failed for '$($file.Relative)'." }
        }

        [IO.Compression.ZipFile]::CreateFromDirectory($stage, $tempZip, [IO.Compression.CompressionLevel]::Optimal, $false)
        $archive = [IO.Compression.ZipFile]::OpenRead($tempZip)
        try {
            $entries = @($archive.Entries.FullName | ForEach-Object { $_.Replace('\', '/') })
            foreach ($required in @(
                "$name/ArcGISProMCP.AddIn.esriAddinX",
                "$name/server/arcgis-pro-mcp.exe",
                "$name/server/skills/$($sourceSkills[0].Name)",
                "$name/docs/deployment.md",
                "$name/checksums.sha256")) {
                if ($required -notin $entries) { throw "Release archive is missing '$required'." }
            }
            # Development-only hosts and test support never ship.
            $devOnly = @($entries | Where-Object { $_ -match 'FakeHost|ArcGISProMCP\.Testing|scenarios/' })
            if ($devOnly.Count -gt 0) { throw "Release archive contains development-only files: $($devOnly -join ', ')" }
        } finally { $archive.Dispose() }

        [IO.File]::Move($tempZip, $bundlePath, $true)
        [pscustomobject]@{
            Bundle = $bundlePath
            Version = $version
            RuntimeIdentifier = $rid
            Status = $status
            Sha256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
            Signed = $false
            HostReloadAcceptanceRequired = $true
        }
    }
    finally {
        if (Test-Path -LiteralPath $stage) { Remove-ReleaseTemporaryPath -Path $stage -Prefix '.staging-' }
        if (Test-Path -LiteralPath $tempZip) { Remove-ReleaseTemporaryPath -Path $tempZip -Prefix '.bundle-tmp-' }
    }
}
finally { Pop-Location }
