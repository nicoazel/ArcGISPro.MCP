[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ArcGISProMCP.AddIn\ArcGISProMCP.AddIn.csproj'
$framework = 'net10.0-windows10.0.22621.0'
$output = Join-Path $repoRoot "src\ArcGISProMCP.AddIn\bin\$Configuration\$framework"
$artifacts = Join-Path $repoRoot 'artifacts'
$package = Join-Path $artifacts 'ArcGISProMCP.AddIn.esriAddinX'
$stage = Join-Path $artifacts ('.staging-' + [Guid]::NewGuid().ToString('N'))

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Add-in build failed with exit code $LASTEXITCODE." }
}

New-Item -ItemType Directory -Path (Join-Path $stage 'Install') -Force | Out-Null
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src\ArcGISProMCP.AddIn\Config.daml') -Destination $stage
    foreach ($name in @('ArcGISProMCP.AddIn.dll', 'ArcGISProMCP.Core.dll', 'ArcGISProMCP.Bridge.dll')) {
        Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $stage 'Install')
    }

    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $package)
}
finally {
    $resolvedStage = [System.IO.Path]::GetFullPath($stage)
    $resolvedArtifacts = [System.IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a staging directory outside the artifacts directory.'
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}

if ($Install) {
    $register = 'C:\Program Files\ArcGIS\Pro\bin\RegisterAddIn.exe'
    if (-not (Test-Path -LiteralPath $register)) { throw "RegisterAddIn.exe was not found at '$register'." }
    $registration = Start-Process -FilePath $register -ArgumentList @($package, '/s') -Wait -PassThru -WindowStyle Hidden
    if ($registration.ExitCode -ne 0) { throw "RegisterAddIn.exe failed with exit code $($registration.ExitCode)." }
}

Get-Item -LiteralPath $package
