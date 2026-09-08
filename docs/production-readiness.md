# Production readiness

Target: hardened workstation release for ArcGIS Pro 3.7.1 and embedded Rhino 8. Distribution to other users requires additional release gates. This checklist distinguishes implementation, automated tests and actual host acceptance; none is a substitute for the others.

**Release blocked:** after the successful embedded demo, Pro PID 25396 exited with a native `DADFLib.dll` fault during the idle interval. Managed dump inspection did not identify the native cause. See [crash evidence and isolation plan](native-crash-2026-09-08.md). This must be resolved before a production-ready claim.

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
- Final review added a whole-workflow initial revision check before any steps execute. A leading read or a continued failure must not refresh a stale/missing caller revision into authorization for later writes. Three additional handler tests passed, including a read-only workflow that requires no write revision. This last guard is in the packaged source/build but was added after the live runs above; live acceptance of it requires installation and a host reload.

Local evidence and packages are git-ignored. The portable release gate is `./tools/verify-release.ps1`; the human/host procedures are in [manual acceptance](manual-acceptance.md). Do not mark unchecked gates passed based on these results.

## Remaining limitations

The registry is curated, not the entire ArcGIS API. Advanced renderers, workspace connection management, PDF export and arbitrary ArcPy execution are not implemented. Jobs and idempotency records are not durable across crashes. Workflows are not transactional or resumable and cannot remotely grant approval. Optimistic revisions do not lock out user/third-party edits. Already-started noncooperative SDK/Rhino work can delay draining indefinitely; this release does not guarantee bounded shutdown. Resource limits cover the current process index; deletion is best effort and old-session orphan files are not a cross-restart disk quota. Audit/workflow history still needs retention limits. Production claims require the live gates above, not only passing mocks.

The Windows computer-use helper failed initialization in the current session; panel interaction therefore remains unverified until that helper is available or a person performs the documented review sequence.
