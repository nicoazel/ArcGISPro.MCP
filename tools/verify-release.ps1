[CmdletBinding()]
param([switch]$Live, [string]$ImageUri)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet build ArcGISPro.MCP.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    foreach ($testProject in @('tests/ArcGISProMCP.Core.Tests', 'tests/ArcGISProMCP.Bridge.Tests', 'tests/ArcGISProMCP.Server.Tests')) {
        dotnet test $testProject -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
    }
    git diff --check
    if ($LASTEXITCODE -ne 0) { throw 'Diff whitespace validation failed.' }
    $null = & ./tools/pack-addin.ps1 -Configuration Release -SkipBuild
    $package = Join-Path $repoRoot 'artifacts/ArcGISProMCP.AddIn.esriAddinX'
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $expected = @('Config.daml', 'Install/ArcGISProMCP.AddIn.dll', 'Install/ArcGISProMCP.Core.dll', 'Install/ArcGISProMCP.Bridge.dll')
        $actual = @($zip.Entries.FullName | ForEach-Object { $_.Replace('\','/') })
        if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'Unexpected add-in package contents.' }
    }
    finally { $zip.Dispose() }
    if ($Live) {
        & ./tools/test-mcp.ps1 -Configuration Release -ApprovalProbe -ImageUri $ImageUri
    }
    [pscustomobject]@{
        Package = $package
        Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        PortableChecks = 'passed'
        LiveProtocolChecked = [bool]$Live
        HumanApprovalInteraction = 'requires separate manual UI acceptance'
        LiveFeatureMetadataGeoprocessing = 'requires separate disposable-project acceptance'
        LiveArcPyExecution = 'requires explicit opt-in and separate disposable-project acceptance'
        Signed = $false
    }
}
finally { Pop-Location }
