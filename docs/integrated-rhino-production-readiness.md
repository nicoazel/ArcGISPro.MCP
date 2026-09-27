# Historical integrated Rhino/ArcGIS production-readiness record

This file is retained as evidence from the former combined checkout. It is not the release checklist for the standalone ArcGIS Pro MCP; see [current production readiness](production-readiness.md).

Target: hardened workstation release for ArcGIS Pro 3.7.1 and embedded Rhino 8. Distribution to other users requires additional release gates. This checklist distinguishes implementation, automated tests and actual host acceptance; none is a substitute for the others.

**Historical release blocker:** after the successful embedded demo, Pro PID 25396 exited through a `DADFLib.dll` fail-fast. Native dump analysis recovered the initiating MFC `CResourceException` in Rhino panel creation, but not yet why that resource creation failed. See [crash evidence and isolation plan](integrated-rhino-native-crash-2026-09-08.md). This had to be resolved for the combined product; it is not acceptance evidence for the separated ArcGIS-only product.

## Hardening in this pass

- Concurrent bounded pipe handling, stalled-frame deadline, same-pipe ownership lease and graceful asynchronous draining.
- Host operation serialization with access/revision checks after queuing.
- Fail-closed modeless approval requests, expiry, cancellation and atomically consumed tokens; no remote approval method.
- Idempotency keys retain uncertain/faulted executions; transport failures after transmission explicitly warn that outcome is unknown.
- Whole-workflow serialization and immutable-version retry keys; audit/history failures retain known outcomes with explicit warnings.
- Common ArcGIS layer/property/edit/layout events advance optimistic revisions, including edits to an already-dirty project.
- Bounded supported JSON Schema subset, including actual renderer/label/layout constraints and malformed schema rejection.
- Workflow failures remain failures in results/rankings; dependent steps do not execute after failed prerequisites.
- Companion Rhino.Inside existing-object geometry replacement and failed-baseline preservation.

## Acceptance gates

| Gate | Required evidence |
| --- | --- |
| Build and portable tests | Clean Release solution build; Core, Bridge and companion sync tests pass |
| Actual MCP transport | stdio initialize/list/call, 16 tools, live state, skill read and native image block |
| Local approval UI | Request visible with exact arguments/revision; approve once, deny, expire, cancel, disconnect/reconnect; no mutation before approval |
| Host concurrency | Health/discovery remain usable during long operation; serialized writes reject stale queued revisions |
| Reverse geometry | Live embedded Rhino replacement retains GUID/attributes, multipart correctness, clean second preview, locked-object failure leaves baseline unchanged |
| Recovery | Disconnect and host shutdown while work queued/running; no detached mutations or silent retries; reopening saved disposable project |
| Demo regression | Existing three-map/one-layout workflow succeeds and exported image is visually reviewed |
| Data safety | Tests use disposable file geodatabase; no forced conflict resolution or deletion of user data |
| Distribution | Signed add-in, deployment/rollback documentation, supported SDK/runtime matrix and dependency/license review |

## Verified on 2026-09-08 (0.2.0)

- Release solution: zero warnings and errors; Core 49/49 and Bridge 18/18 tests passed. Companion Core sync tests: 191/191 passed (258 total). Companion add-in still has seven pre-existing build warnings.
- Package inspection: exactly the manifest and three intended install assemblies. NuGet vulnerability lookup reported no known vulnerable packages in configured sources; this is not a full dependency/license/security audit.
- ArcGIS Pro 3.7.1 host PID 25396: McNeel adopted the embedded Rhino listener on port 10500. Both RhinoArcGIS.Core and RhinoArcGIS.Rhino were loaded from the installed Esri assembly cache, not harness-local production copies.
- Compiled `tools/RhinoArcGIS.HostSmoke` harness in the companion repository passed against real RhinoCommon in that host. Its disposable headless document exercised GUID/name/layer/color/metadata preservation, multipart readback without UI groups, obsolete-part removal, foreign-layer-part preservation, and duplicate-identity/locked-object rejection. The active saved model remained unmodified with 572 objects. This is adapter acceptance, NOT a complete GIS-to-Rhino SyncCoordinator roundtrip.
- The pinned 42-step presentation workflow succeeded first without Rhino loaded and again with embedded Rhino running. Results: `artifacts/demo/hardening-presentation.result.json` and `artifacts/demo/hardening-presentation-with-rhino.result.json`. All 42 steps succeeded in each run.
- Native 2400-by-1553 export was visually inspected. The three-map composition, titles, category key, north arrow and source/disclaimer text are present. Scale-bar labels are crowded and further cartographic polish is warranted. Fixture parcels/streets and illustrative buildings are not authoritative zoning/transit or a site-feasibility study.
- Real stdio MCP verification passed protocol 2025-11-25, 16 entry tools, live state, registry search, skill read, native image blocks, and a pending-review/cancel probe while the pane rendered. This probe did not approve or execute geoprocessing.
- A live WPF crash caused by default TwoWay `Run.Text` bindings to read-only approval properties was fixed with explicit OneWay bindings; source regression tests now cover these bindings. The post-fix pending/cancel probe passed across subsequent hosts.
- An earlier host disappeared after five style-only workflow steps, without a corresponding Pro crash report; cause remains unresolved. A later saved host exited with code 0. Neither observation establishes safe shutdown during an in-flight operation. Subsequent full runs passed, including with Rhino active.
- Final review added a whole-workflow initial revision check before any steps execute. A leading read or a continued failure must not refresh a stale/missing caller revision into authorization for later writes. Three additional handler tests passed, including a read-only workflow that requires no write revision. The exact package containing this guard is installed and reloaded; targeted live mutation/workflow acceptance of the guard remains required.
- Follow-up native analysis identified the original C++ exception as `CResourceException` thrown while Rhino created panel windows; DADFLib is the terminal fail-fast recorder in this dump. The companion add-in now retains `WindowStyle.Normal` as its default but accepts `RHINOINSIDE_ARCGIS_WINDOW_STYLE=hidden|no-window` for controlled one-factor isolation, and the opt-in test bridge reports the selected style. No live embedded comparison has passed yet.
- With ArcGIS Pro closed, the previous MCP add-in was preserved under git-ignored `artifacts/rollback` and package SHA-256 `1218A93B562FBEC348BAFB05B0FFA29889635BBF38826DA39B01205D6A4FA43B` was installed. The companion package on disk matches the new diagnostic build at SHA-256 `D242FC1463C89FD666A97B32710D858F346C3671562195FECFBF37B05356DA8B`. Neither result establishes which assemblies a future host actually loads; compare the cached/loaded DLL hashes after restart.
- ArcGIS Pro restarted against `MasterPlanDemo.aprx` as PID 8516 and remained responsive. Esri's refreshed cache matched the packaged MCP DLL (`F2E35546B5AF44A329BA3911FEC1DAFDE12C44F32FAC518213CA8A9DDA421CEF`) and companion DLL (`F81829587B59B77983B4AC40E4F5EA670836724259946D50D99815E64B9D4B5D`). The live release gate passed MCP protocol 2025-11-25, 16-tool discovery, live state, skill read, registry search, and pending/cancelled review. No image URI was supplied; human approval, mutation and embedded sync were not exercised.

Local evidence and packages are git-ignored. The portable release gate is `./tools/verify-release.ps1`; the human/host procedures are in [manual acceptance](manual-acceptance.md). Do not mark unchecked gates passed based on these results.

## Remaining limitations

The registry is curated, not the entire ArcGIS API. Advanced renderers, workspace connection management, PDF export and arbitrary ArcPy execution are not implemented. Jobs and idempotency records are not durable across crashes. Workflows are not transactional or resumable and cannot remotely grant approval. Optimistic revisions do not lock out user/third-party edits. Already-started noncooperative SDK/Rhino work can delay draining indefinitely; this release does not guarantee bounded shutdown. Resource limits cover the current process index; deletion is best effort and old-session orphan files are not a cross-restart disk quota. Audit/workflow history still needs retention limits. Production claims require the live gates above, not only passing mocks.

The Windows computer-use helper failed initialization in the current session; panel interaction therefore remains unverified until that helper is available or a person performs the documented review sequence.
