# Historical integrated Rhino/ArcGIS native-host crash

This evidence belongs to the former embedded-Rhino product configuration. It does not establish a defect in the separated ArcGIS-only package, which still requires its own live stability soak.

Status: unresolved; the initiating native path is now narrowed to Rhino panel creation, but the reason the panel resource could not be created is not yet proven. Do not interpret the completed demo or portable tests as a stable-host soak test.

## Observed evidence

- ArcGIS Pro 3.7.1.1904, process 25396 (`0x6334`), exited with code -1 after the embedded-Rhino smoke and 42-step presentation both completed successfully.
- Last audited registry operation: `view.capture`, completed 2026-09-08 12:39:45.792 UTC. The project save completed at 12:39:43.655 UTC. Subsequent protocol/image/pending-cancel probes passed.
- Windows Application Error event 1000 at 2026-09-08 08:44:04 America/New_York reported `C:\Program Files\ArcGIS\Pro\bin\DADFLib.dll`, version 13.7.0.1901, exception `0xc0000602`, offset `0x2f178`, report ID `8bce9ebb-9e2a-4491-96d9-9e86e69341f2`. Native analysis shows DADFLib recorded the terminal fail-fast; it was not the initiating exception.
- Matching Esri dump: `%LOCALAPPDATA%\ESRI\ErrorReports\ArcGISPro_3.7.1.1904_8864ED1A-5C7E-43C8-83E0-371643DF2A28_09_08_2026_08_44_06.dmp` (16,484,511 bytes; SHA-256 `30208006B3737CA0C921227B6DBD3CDFCFE06BC8CA1CE804A67DB684C11C78F6`). Leave this local; dumps can contain private process data and have not been uploaded.
- The dump reports 6 minutes 50 seconds of process uptime. Its comment identifies the last active view as `esri_layouts_layoutPane` and the last command as `esri_layouts_selectByRectangleTool`; the failure was therefore roughly four minutes after the last audited `view.capture`, not a long-duration idle soak.
- The active Rhino document was last verified saved/unmodified, 572 objects. The latest exported layout and saved disposable project remain available under `artifacts/demo`.
- This is distinct from the earlier managed WPF read-only binding failure, which was fixed and regression-tested.

## Checks performed

`dotnet-dump` 10.0.731102 was installed only under git-ignored `artifacts/diagnostics` and used to read this dump. Managed thread/stack inspection completed. No ArcGISProMCP, RhinoInside or RhinoArcGIS frames were present in the captured managed stacks; that does NOT rule out earlier add-in/native interop corruption. The local output is `artifacts/diagnostics/pro-25396-managed-stacks.txt`.

The available managed inspector cannot show the native fault stack. Microsoft documents this boundary in [dotnet-dump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump). The official WinDbg 1.2606.22001.0 MSIX bundle was downloaded but not installed globally, its published SHA-256 was verified (`12E63FB884347567BDD35F67F7AAD61B26A08F8404553DAD6951A10776F7D771`), and its x64 CDB 10.0.29617.1000 was run from git-ignored `artifacts/diagnostics/windbg-offline` with symbols cached under `artifacts/diagnostics/symbols`.

The native evidence changes the working diagnosis:

- `!analyze -v` shows the terminal sequence `KERNELBASE!RaiseFailFastException` -> `DADFLib!DADFCore::FailFast` -> `ucrtbase!abort` -> `terminate` -> `hostpolicy!std::thread::~thread`, while a foreign exception was unwinding through CoreCLR and the ArcGIS Pro WPF dispatcher.
- The recovered original exception record is C++ EH `0xe06d7363`, exact type `CResourceException`, thrown by `mfc140u!AfxThrowResourceException`.
- Pre-unwind stack remnants contain `rhcommon_c!RhinoPanels_CreatePanelWindow`, `RhinoCore!RhinoPanels::CInterop::CreatePanelWindow`, and the CLR P/Invoke thunk. Installed Rhino metadata resolves two panel GUIDs on that stack to Rhino's **Properties** (`34ffb674-c504-49d9-9fcd-99cc811dcda2`) and **Display** (`b68e9e9f-c79c-473c-a7ef-846a11dc4e7b`) panels.
- The add-in currently starts `RhinoCore` with `WindowStyle.Normal`, which creates the full visible Rhino window and its panel UI. This correlation strongly implicates the full-UI panel creation/restoration path, but it does not yet prove why MFC resource creation failed, which component requested the panel, or whether the trigger belongs to Rhino, Pro hosting, graphics/window resources, or our integration behavior.

The most useful local outputs are `pro-25396-native-analysis.txt`, `pro-25396-cpp-exception-record.txt`, `pro-25396-original-context.txt`, `pro-25396-original-foreign-exception-stack.txt`, and `pro-25396-unwind-stack-scan.txt`, all under git-ignored `artifacts/diagnostics`.

## Next isolation steps

1. With a person controlling add-in enablement and normal shutdown, compare the same saved disposable project: baseline Pro; MCP only; Rhino.Inside loaded without Rhino startup; embedded Rhino started; McNeel listener started; headless harness; presentation workflow. Record versions/PIDs and keep an idle observation period after each stage. Change one factor at a time.
2. Use the diagnostic-only `RHINOINSIDE_ARCGIS_WINDOW_STYLE` setting to repeat the embedded-Rhino stage as `normal`, `hidden`, and `no-window` while leaving the shipped default as `normal`. `Hidden` still creates Rhino UI; `NoWindow` is the headless control and may not support panel- or UI-dependent plug-ins. The test bridge now reports the selected style in `status` and `launch` responses.
3. If a reproducible stack implicates our code, add a regression before fixing it. If native vendor analysis is needed, review the dump for sensitive content and obtain authorization before sending it externally.
4. Rerun live approval, complete GIS-to-Rhino roundtrip, idle stability and normal-shutdown gates on the final installed package. Until then this build remains a development preview.
