# Native host crash - release blocker

Status: unresolved; no root cause assigned. Do not interpret the completed demo or portable tests as a stable-host soak test.

## Observed evidence

- ArcGIS Pro 3.7.1.1904, process 25396 (`0x6334`), exited with code -1 after the embedded-Rhino smoke and 42-step presentation both completed successfully.
- Last audited registry operation: `view.capture`, completed 2026-09-08 12:39:45.792 UTC. The project save completed at 12:39:43.655 UTC. Subsequent protocol/image/pending-cancel probes passed.
- Windows Application Error event 1000 at 2026-09-08 08:44:04 America/New_York: faulting module `C:\Program Files\ArcGIS\Pro\bin\DADFLib.dll`, version 13.7.0.1901, exception `0xc0000602`, offset `0x2f178`, report ID `8bce9ebb-9e2a-4491-96d9-9e86e69341f2`.
- Matching Esri dump: `%LOCALAPPDATA%\ESRI\ErrorReports\ArcGISPro_3.7.1.1904_8864ED1A-5C7E-43C8-83E0-371643DF2A28_09_08_2026_08_44_06.dmp` (16,484,511 bytes). Leave this local; dumps can contain private process data and have not been uploaded.
- The active Rhino document was last verified saved/unmodified, 572 objects. The latest exported layout and saved disposable project remain available under `artifacts/demo`.
- This is distinct from the earlier managed WPF read-only binding failure, which was fixed and regression-tested.

## Checks performed

`dotnet-dump` 10.0.731102 was installed only under git-ignored `artifacts/diagnostics` and used to read this dump. Managed thread/stack inspection completed. No ArcGISProMCP, RhinoInside or RhinoArcGIS frames were present in the captured managed stacks; that does NOT rule out earlier add-in/native interop corruption. The local output is `artifacts/diagnostics/pro-25396-managed-stacks.txt`.

The available managed inspector cannot show the native fault stack. Microsoft documents this boundary in [dotnet-dump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump). WinDbg/CDB were not found in the checked local tool locations. The faulting module name alone is insufficient to blame Esri, Rhino, the add-in, graphics, or the environment.

## Next isolation steps

1. Open the matching dump in a native Windows debugger, load matching symbols and obtain exception context and native stack (`!analyze -v`, `.ecxr`, `kv` in WinDbg). Follow [Esri's crash-analysis guidance](https://support.esri.com/en-us/knowledge-base/workflow-when-arcmap-or-arcgis-pro-crash-000026378). Do not reset profiles, uninstall software or replace native DLLs based solely on this event.
2. With a person controlling add-in enablement and normal shutdown, compare the same saved disposable project: baseline Pro; MCP only; Rhino.Inside loaded without Rhino startup; embedded Rhino started; McNeel listener started; headless harness; presentation workflow. Record versions/PIDs and keep an idle observation period after each stage. Change one factor at a time.
3. If a reproducible stack implicates our code, add a regression before fixing it. If native vendor analysis is needed, review the dump for sensitive content and obtain authorization before sending it externally.
4. Rerun live approval, complete GIS-to-Rhino roundtrip, idle stability and normal-shutdown gates on the final installed package. Until then this build remains a development preview.
