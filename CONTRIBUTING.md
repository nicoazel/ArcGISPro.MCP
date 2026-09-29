# Contributing

Thanks for your interest. ArcGIS Pro MCP Studio is a development preview maintained on a best-effort basis. Small, focused contributions are the easiest to review.

## Prerequisites

- Windows x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json` for the pinned feature band).
- [PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) (`pwsh`) for `tools/pack-addin.ps1`, `tools/verify-release.ps1` and `tools/package-release.ps1`, which use .NET APIs that Windows PowerShell 5.1 lacks and declare `#Requires -Version 7.0`. `run-urban-stress.ps1` needs it too, and `run-acceptance.ps1` (itself 5.1-compatible) runs every live harness with `pwsh`. `pack-gateway.ps1` and `test-fakehost.ps1` run in Windows PowerShell 5.1 as well.
- ArcGIS Pro 3.7 with a license, to load and exercise the add-in. The solution builds against the `Esri.ArcGISPro.Extensions30` NuGet package, so the build and portable tests do not need Pro, but the add-in runtime and any live testing do.

## Build and test

```powershell
dotnet build ArcGISPro.MCP.slnx -c Release
dotnet test ArcGISPro.MCP.slnx -c Release --no-build
./tools/test-fakehost.ps1 -Configuration Release   # FakeHost + gateway smoke, as CI runs it
```

Warnings are treated as errors; a change must build with 0 warnings and keep every test project under `tests/` green.

Before opening a pull request, run the same gate as CI:

```powershell
./tools/package-release.ps1
```

It runs `tools/verify-release.ps1` (Release build, every test project under `tests/`, `git diff --check`, add-in packaging and content inspection), then publishes the gateway, smoke-tests the MCP handshake offline, and builds the preview bundle under `artifacts/releases/`.

## Lint

CI lints the PowerShell scripts under `tools/` and the Python scripts under `tools/` and `evals/`. Every PowerShell script must parse in Windows PowerShell 5.1 and PowerShell 7.x (`PSUseCompatibleSyntax`); the scripts listed under Prerequisites as needing PowerShell 7 may use .NET APIs that 5.1 lacks, which the analyzer does not check. The `tools/` Python scripts target the ArcGIS Pro environment (Python 3.11).

```powershell
Install-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -Scope CurrentUser
Get-ChildItem tools -Filter *.ps1 -File | ForEach-Object { Invoke-ScriptAnalyzer -Path $_.FullName -Settings ./PSScriptAnalyzerSettings.psd1 }
```

```powershell
uvx ruff@0.16.9 check tools evals
uvx ruff@0.16.9 format --check tools evals
```

The analyzer settings live in `PSScriptAnalyzerSettings.psd1` and the Ruff settings in `ruff.toml`. CI currently excludes `PSUseDeclaredVarsMoreThanAssignments` for `tools/test-mcp.ps1` only.

Changes that touch ArcGIS Pro behavior should also be checked in a live Pro session against a disposable project. See [manual acceptance](docs/manual-acceptance.md) and describe what you tested in the pull request.

## Pull requests

- Keep pull requests small and about one thing. Split refactors from behavior changes.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for commit messages, for example `fix(workflows): stop on revision mismatch` or `docs: clarify autonomous mode`.
- Add or update tests with behavior changes.
- Update the docs and `CHANGELOG.md` (under `[Unreleased]`) when behavior, operations, or safety properties change. Documentation must describe what the code does, not what it is meant to do eventually.
- New operations follow the [extension rules](docs/architecture.md#extension-rules): a complete descriptor, an honest risk level, and `requiresConfirmation` for anything destructive or with external side effects.

## Security issues

Do not report vulnerabilities in public issues or pull requests. Follow [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contributions are licensed under the Apache-2.0 license in [LICENSE](LICENSE).
