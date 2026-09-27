# ArcGIS-only manual acceptance

Use the exact packaged add-in and gateway against a disposable ArcGIS Pro project and file geodatabase. Record the package hash, installed assembly path/hash, ArcGIS Pro version, process id, project copy, request/result JSON, audit records, output datasets, and before/after screenshots. Portable tests are prerequisites, not live acceptance.

## Recording evidence

`tools/run-acceptance.ps1` automates the recordable parts of this checklist. It captures the commit, ArcGIS Pro version, package hash and the hashes of the add-in DLLs that Pro actually loaded. It runs the existing harnesses and writes `manifest.json`, `summary.md` and `SHA256SUMS`. With `-Commit` it copies them to `docs/acceptance/<yyyy-MM-dd>-<sha7>/`. It covers Baseline steps 1–3 (`smoke`), the scripted feature, metadata, geoprocessing and ArcPy runs (`feature-gp-arcpy`, **autonomous mode only**) and the urban workflow stress run (`stress`). It is read-only unless you pass `-AllowProjectMutation` with a `-DisposableRoot`. Start with `./tools/run-acceptance.ps1 -PlanOnly`. The folder contract and the full procedure are in [acceptance/README.md](acceptance/README.md). The fixture data used by `stress` is described in [tests/data/README.md](../tests/data/README.md).

The script does not cover the steps that need a person: approving, denying or letting reviews expire in the dockpane, multi-instance routing, undo/redo, reload checks and visual inspection. Run those from the lists below and record what you inspected with `-VisuallyInspected`.

## Baseline

1. Run `./tools/verify-release.ps1` and `./tools/package-release.ps1` from the repository root.
2. Close ArcGIS Pro, install the exact package, reopen the disposable project, and confirm the loaded assembly hash matches the package.
3. Verify MCP initialize, 16 gateway tools, live state, registry search/describe/validate, skill lookup, and a native image observation.
4. Confirm the registry contains no `rhino.*` operation and the workspace reports no Rhino capability.
5. Verify autonomous control is disabled by default. On a separate trusted-host run, enable it explicitly, confirm the workspace/panel warning, invoke a harmless risky operation without a token, and verify the `autonomous_control` notice and audit record.
6. Open two disposable ArcGIS Pro projects concurrently. Verify distinct per-PID discovery records/pipes, an actionable ambiguity error without a selector, and stable repeated routing when each gateway uses its selected `ARCGIS_PRO_MCP_HOST_PID`.

## Feature data

Create a disposable GlobalID-enabled point, polyline, and polygon feature class with editable text, integer, double, date, and GUID fields.

1. Run `feature.layer.describe` and verify geometry, spatial reference, ObjectID, GlobalID, field types, editability, and source information.
2. Run bounded attribute and envelope queries. Verify the requested limit, ObjectID/GlobalID identity, spatial relationship, field projection, invalid-field rejection, unsafe-where rejection, and non-feature-layer rejection.
3. Exercise `feature.select` in `new` and `add` modes, then clear the selection. Verify invalid modes fail without changing selection.
4. Create one feature of each supported geometry type, read it back, update attributes and geometry by GlobalID, and prove the same feature changed without duplication.
5. Verify wrong geometry types, over-length strings, read-only/system fields, nullability violations, non-editable layers, unknown identities, and stale workspace revisions fail without mutation.
6. Request local review for one exact `feature.delete`, approve once, delete the target, and prove token reuse and changed arguments fail. Repeat with denial, expiry, cancellation, and stale revision; the feature must remain.
7. Exercise ArcGIS undo/redo where the data source supports it and record actual behavior. Do not claim transactional rollback across workflows.

## Metadata

1. Run `metadata.get` on a map-layer-owned and, where available, source-owned feature layer. Record support, editability, scope, storage, values, and XML length.
2. On an editable disposable layer, request review and update title, summary, description, tags, credits, and use limitations. Read back after save and after project reload; unrelated metadata XML must remain.
3. Verify an empty patch, unsupported layer, non-editable/source-owned metadata, oversized values, token reuse, and stale revision fail without changing metadata.

## Geoprocessing

Run at least `analysis.Buffer`, `analysis.Clip` or `analysis.Intersect`, and `management.GetCount` with explicit scratch outputs and environments.

1. Describe and validate the exact `gp.run` arguments, request local review, then invoke unchanged arguments/revision.
2. Verify output paths, overwrite behavior, add-to-map/history/refresh flags, return value, derived values/types, warnings, messages, timing, and audit correlation.
3. Repeat successful tools ten times with unique idempotency keys and deterministic scratch outputs. Verify an exact same-session retry does not duplicate work.
4. Verify invalid tool, invalid parameters, license failure where safely reproducible, cancellation, disconnect, timeout, existing-output rejection, and outcome_unknown reconciliation. Never blindly retry a possibly accepted mutation.

## ArcPy

ArcPy acceptance applies only when the optional execution surface is explicitly enabled. Use a reviewed script file inside an explicitly allowed scratch root.

1. Verify disabled-by-default behavior and rejection outside the allowed root.
2. Run a harmless version/environment probe and a scratch feature-class calculation. Record the resolved ArcGIS Python executable, script hash, working directory, timeout, exit code, bounded stdout/stderr, and output data.
3. Verify confirmation, stale revision, denial, expiry, cancellation, timeout/process-tree cleanup, output truncation, missing script, invalid working directory, and nonzero exit behavior.
4. Inspect the exact script before approval. ArcPy is arbitrary code with the signed-in user's authority; this operation is not a sandbox.

## Stability and release gate

Run the three urban workflows ten times each on the exact installed build, including their geoprocessing, feature-query/edit, metadata, layout, save, capture, and reload checks. Visually inspect every final layout and verify no blank frame, duplicated feature, leaked scratch output, stale selection, or unexpected project mutation. Observe idle operation and normal shutdown after the stress run.

Release evidence must separately state what is implemented, portable-tested, live-tested, visually inspected, and still blocked. An unsigned package, a rebuilt DLL not loaded by Pro, or successful standalone tests are not production acceptance.
