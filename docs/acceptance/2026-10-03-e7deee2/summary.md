# Acceptance evidence 2026-10-03 (e7deee2)

- Commit: `e7deee2493a07b224fb93b3ea553ecdf047ebf9c`
- Version: 0.3.0; .NET SDK 10.0.400; operator: nicoazel
- ArcGIS Pro: 3.7.0 (ArcGISPro.exe 3.7.1.1904); host PID 49484
- Package SHA-256: `ed96687d72b5d55865a0f3f006e389a52dd96d26ed9c2b60fe2d7773dcf9c66b`
- Autonomous mode on host: False
- All selected steps passed: True

## Implemented

- Add-in {D48E59D8-23D2-44C1-B3E2-39196360DA7A} version 0.3.0 exposing 41 host operations (as reported by system.get_state).

## Portable-tested

- verify (passed): Release build, every tests/*.Tests project, package contents.

## Live-tested

- pro-install - ArcGIS Pro 3.7.0 (3.7.1.1904); 4 add-in DLLs match the package
- host-probe - pid 49484, 41 operations, autonomousMode=False, project D:\scratch\mcp-acceptance\Acceptance-1003-0126.aprx
- smoke - protocol 2025-11-25, 16 tools, pendingCancelReview=True, imageBlock=False
- stress - 3 workflows x 3 runs; layouts inspected; captures non-blank
- operations - 41/41 operations covered; cases 90 passed, 0 failed, 0 skipped; cards 7 approved, 1 denied, 0 skipped; 0 messages flagged

## Visually inspected

- images/layout-tod.png: URBAN TEST 01 renders on the synthetic data: land-use categories, labelled development program, transit network and existing-fabric frames correct; legend clear of the basemap attribution. Cosmetic: the legend top edge sits over the Building 3 footprint; scale bar labels are tight.
- images/layout-green.png: URBAN TEST 02 renders: green infrastructure sites, loop corridors, development interface and neighborhood nodes correct; legend and scale bar clear of attribution and of each other.
- images/layout-mixed.png: URBAN TEST 03 renders: 3D massing extrudes over the public realm, mixed-use categories and public-realm fit correct; legend on the scene frame clear of attribution. Cosmetic: in frame 02 the scale bar and north arrow sit over parcels.
- Synthetic block grid does not align with the Esri basemap streets underneath (expected for generated data; attribution shown).

Source of these notes: Notes written from the 2026-10-03 pre-run captures of this commit and re-checked against this run's captures before the folder was committed.

## Blocked or not run

- feature-gp-arcpy: not selected for this run.

Standing limits (docs/deployment.md, Known limits) still apply; this record does not lift them.
