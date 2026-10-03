## Summary

<!-- What does this change and why? Link the issue it addresses, if any. -->

## Checklist

- [ ] `dotnet build ArcGISPro.MCP.slnx -c Release` succeeds with 0 warnings (warnings are errors).
- [ ] Every test project under `tests/` is green; behaviour changes add or update tests.
- [ ] `./tools/package-release.ps1` passes locally (the same gate as CI: verify, tests, packaging, offline MCP handshake).
- [ ] `CHANGELOG.md` has an entry under `[Unreleased]` if behaviour, operations or safety properties changed.
- [ ] Docs describe what the code does now, not what it is meant to do eventually.
- [ ] New or changed operations follow the [extension rules](https://github.com/nicoazel/ArcGISPro.MCP/blob/main/docs/architecture.md#extension-rules): complete descriptor, honest risk level, `requiresConfirmation` for anything destructive or with external side effects.
- [ ] No claim of live ArcGIS Pro verification unless a committed `docs/acceptance/<date>-<sha7>/` folder backs it; otherwise describe what was tested manually below.

## Live testing

<!-- Did you exercise this in ArcGIS Pro against a disposable project? Which ArcGIS Pro build, which mode (default/autonomous), which operations or workflows? Write "not tested live" if it was not. -->
