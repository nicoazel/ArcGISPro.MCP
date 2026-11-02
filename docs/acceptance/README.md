# Acceptance evidence

This folder holds committed live acceptance evidence. Each entry records one run of the exact package built from one commit, installed and loaded in ArcGIS Pro on the maintainer's workstation.

**Only the maintainer, running ArcGIS Pro with the exact installed package, produces entries here.** Contributors and CI do not, and an entry is never written by hand. Portable tests, `-PlanOnly` output and working evidence under `artifacts/` are not acceptance evidence.

## Folder contract

One folder per run, named `<yyyy-MM-dd>-<sha7>` (local date of the run, first seven characters of the commit SHA):

| File | Contents |
|---|---|
| `manifest.json` | Machine-readable record: `schemaVersion`, `date`, `sha`, `dirty`, `describe`, `tag` (when HEAD is tagged), `version`, `dotnet`, `operator`, `pro` (install dir, registry and `ArcGISPro.exe` versions, running PIDs, host PID, operation count, add-in id), `package` (path and SHA-256 of the `.esriAddinX`), `dlls.built` / `dlls.loaded` (SHA-256 of the add-in DLLs in the package and in Pro's `AssemblyCache`), `sections[]` (each step's status, whether it mutates the project or requires autonomous mode, timing, detail, evidence paths), `autonomousMode`, `allPassed`, `visuallyInspected[]` (what the operator checked by eye, from `-VisuallyInspected`, when given), `visualNotesSource` (where those notes come from, from `-VisualNotesSource`, when given), `runNotes[]` (how the run was carried out, for example who decided the approval cards or which host prompt someone answered, from `-RunNotes`, when given), `evidence[]`, `evidencePathsRelative` (see below) |
| `summary.md` | Human-readable record, split into implemented, portable-tested, live-tested, visually inspected, run notes (when `-RunNotes` was given), and blocked or not run |
| `SHA256SUMS` | `<sha256>  <relative path>` for every other file in the folder, sorted, LF line endings |
| `host-probe/state.json` | `system.get_state` snapshot taken before the live sections |
| `smoke/result.json` | `tools/test-mcp.ps1` result (protocol, tool count, approval probe, image block) |
| `feature-gp-arcpy/*.json` | Request/result pairs and `summary.json` from `tools/run-live-feature-gp-arcpy.ps1`, when that section ran |
| `stress/summary.json` | `tools/run-urban-stress.ps1` summary, when that section ran. `-Commit` rewrites each case's `capture` to its committed image (`images/layout-<case>.png`), or to `not committed: <working path>` when the image was not copied; folders committed before this rewrite keep the working-evidence path |
| `operations/summary.json` | `tools/run-live-operations.ps1` summary, when that section ran: every operation with its cases (`name`, `kind` happy or negative, `status`, `errorCode`, `durationMs`, `approval`), `covered`, `coverage` (`total`, `covered`, `uncovered`), case counts, approval-card counts (`cards`; per card case `requested` and exactly one of `approved`, `denied`, `approvedDenyCard` (the DENY card was approved; its token was cancelled and the case failed), `requestFailed` (the approval request was refused, so no card was shown) or `skipped` (no decision), the two middle ones absent in summaries written before they were counted separately; per request `queued` and `expired`, which include re-queues), the message audit, `skipCards`, `aborted` (when the run stopped early) and `allPassed` |
| `operations/errors.md` | A Markdown table of every negative case's operation, error code and message, with the message audit (no exception type names, stack traces or local paths; under 500 characters) |
| `operations/results/*.json` | Request/result pair of every call of the operation matrix: `NNN-<operation>-<case>.request.json` / `.result.json` for bridge calls (approval calls are named `NNN-approval.request-<case>`, `NNN-approval.status-<case>` and so on), and `NNN-gateway-<tool>-<case>.*` for MCP gateway calls. `NNN` is the call sequence number |
| `audit.jsonl` | Audit records appended during the run (`%LOCALAPPDATA%\ArcGISProMCP\audit\operations.jsonl`) |
| `images/*.png` | At most five PNGs of at most 500 KB each: final layout captures from the stress run and operator screenshots (`-Screenshot`) |
| `ANNEX.md` | Optional, and the only file allowed in a folder after the run: a hand-written annex at the folder root whose first line is `# Annex added after the run, not produced by run-acceptance`. It is not listed in `SHA256SUMS`, and it never replaces or edits a file the run produced. It is for disclosures a run could not record because it predates `-RunNotes`; later runs record them with `-RunNotes` instead |

Any copied file larger than 1 MB (the JSON files, `audit.jsonl` and `operations/errors.md`) is left out and listed in `manifest.json` `evidenceSkipped`. `operations/console.log`, the transcript of what the operator was shown, stays in the working evidence under `artifacts/` and is never committed.

`tests/ArcGISProMCP.Core.Tests/AcceptanceManifestTests.cs` checks every folder: the manifest must match the schema, the tree must have been clean, every selected step must have passed (including `verify`, `pro-install` and `host-probe`), every built DLL hash must equal the loaded one, `autonomousMode` must be true if an autonomous-mode section passed, the folder name must match `date` and `sha`, and `SHA256SUMS` must list every file with the correct hash, except `ANNEX.md` at the folder root, which must not be listed and must start with the annex heading. A manifest whose `operations` step passed must include `operations/summary.json`. A folder with `operations/summary.json` must also show a live ArcGIS Pro host (not a FakeHost), a run that was neither a `-SkipCards` pre-run nor aborted, approval counts with one decision per card case, no failed case, `operations/errors.md`, and every registered operation (`tests/ArcGISProMCP.Operations.Tests/Fixtures/operation-descriptors.json`) covered by a passing happy-path case. For manifests with `"evidencePathsRelative": true` (written by `-Commit` since this rule was added), every path in `sections[].evidence` and `evidence[]`, and the path before `: ` in each `visuallyInspected[]` note (unless the note starts with `not committed: `), must name a file in the folder. `-Commit` rewrites working paths to the committed layout, for example `stress/tod/final-layout.png` to `images/layout-tod.png`, and drops evidence it did not copy (such as `preflight.json` and `logs/`). Older folders without the field list working-evidence paths and are exempt; the only such folder, from the first run, is not part of the public history (see [Public history](#public-history)). `.gitattributes` here stores the folders byte-for-byte so the checksums survive checkout on any platform.

## Producing an entry

Prerequisites: Windows PowerShell 5.1 or PowerShell 7 to run the script, PowerShell 7 (`pwsh`) on `PATH` for the harnesses it invokes, the .NET SDK from `global.json`, and ArcGIS Pro with the package built from a clean checkout of the commit being recorded.

1. Check the plan and the non-Pro facts. This builds nothing, contacts no ArcGIS Pro and writes nothing:

   ```powershell
   ./tools/run-acceptance.ps1 -PlanOnly
   ```

2. Build and package: `./tools/verify-release.ps1` (the script runs it again, but the package has to exist before you install it). Close ArcGIS Pro, install `artifacts/ArcGISProMCP.AddIn.esriAddinX`, and open a **disposable copy** of a project stored under a scratch folder such as `C:\MCP-scratch\mcp-acceptance`. Make a fresh copy of the disposable `.aprx` for each session and launch ArcGIS Pro on it: after a forced close, reopening the same project brings up ArcGIS Pro's Project Recovery prompt, which blocks the start until someone answers it.

3. Run the read-only smoke pass. It builds, tests and packages the commit again, confirms that the DLLs Pro loaded match the package, snapshots the state and runs `test-mcp.ps1 -ApprovalProbe`. The approval probe shows one pending review card in the dockpane and cancels it. Nothing is approved or changed:

   ```powershell
   ./tools/run-acceptance.ps1 -PipeName ArcGISProMCP.v1.<pid>
   ```

   `-PipeName` can be left out when exactly one live host is discovered.

4. For the full record, restart Pro with `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true` (required by `feature-gp-arcpy`, which runs confirmation-gated operations without tokens), reopen the disposable project, then:

   ```powershell
   ./tools/run-acceptance.ps1 -Sections smoke,feature-gp-arcpy,stress `
       -AllowProjectMutation -AllowAutonomous -DisposableRoot C:\MCP-scratch\mcp-acceptance `
       -RunsPerCase 3 -VisuallyInspected 'Three urban layouts: no blank frames, surrounds present' `
       -Commit
   ```

   The mutating sections refuse to run unless the open project is under `-DisposableRoot`. `feature-gp-arcpy` also refuses unless the host reports the `autonomous-control` capability. Turn autonomous mode off again afterwards.

5. For the operation matrix, restart Pro in **default mode** (no `ARCGIS_PRO_MCP_AUTONOMOUS_MODE`) with ArcPy enabled for the session as described in [arcpy.md](../arcpy.md#live-acceptance), reopen the disposable project and open the ArcGIS MCP pane (**Add-In** tab > **MCP Studio**). Check the ordered matrix and the cards you will decide first:

   ```powershell
   ./tools/run-live-operations.ps1 -PlanOnly
   ```

   To shake out the matrix before the operator session, run the script directly with `-SkipCards`. No review card is queued and every card case is recorded as skipped, so such a run never passes and is not evidence; its output goes to `artifacts/live-operations/<timestamp>/` unless you pass `-EvidenceDirectory`:

   ```powershell
   ./tools/run-live-operations.ps1 -PipeName ArcGISProMCP.v1.<pid> -DisposableRoot C:\MCP-scratch\mcp-acceptance -SkipCards
   ```

   Then run it for the record, last, after the other default-mode sections:

   ```powershell
   ./tools/run-acceptance.ps1 -Sections smoke,stress,operations `
       -AllowProjectMutation -DisposableRoot C:\MCP-scratch\mcp-acceptance -RunsPerCase 3 `
       -RunNotes 'Cards decided by <who>, <how>' -Commit
   ```

   Record with `-RunNotes` anything a reviewer needs to know about how the run was carried out and that the evidence files cannot show: who decided the cards and by what means, a card that expired and was re-queued, or an ArcGIS Pro prompt someone answered during the run. The notes go into `manifest.json` (`runNotes[]`) and `summary.md` ("Run notes") and are covered by `SHA256SUMS`.

   `operations` runs 90 cases (58 happy, 32 negative) covering all 41 operations. Near the end the console asks for eight review cards, one at a time: `Card N/8: <operation> on <target>`, followed by **click APPROVE ONCE** (seven cards) or **click DENY** (one card, a delete of the baseline feature). Read the card in the dockpane before clicking. A card expires after two minutes and is queued again; after ten minutes without a decision the case is recorded as skipped by the operator and the run continues. The last card opens a copy of the saved project (`<name>-reopen-<stamp>.aprx` under the disposable root), so Pro ends on that copy. The section is refused while the host reports `autonomous-control`, and it passes only when no case failed and all 41 operations are covered, which needs ArcPy enabled and a reachable portal (for `basemap.set`). The script exits 1 whenever `summary.json` does not report `allPassed`, including every `-SkipCards` pre-run and every aborted run.

   Options of `run-live-operations.ps1` beyond those above: `-OperatorTimeoutSeconds` (60-3600, default 600) is how long one card case waits for a decision, re-queuing expired cards, before it is recorded as skipped by the operator. `-Offline` skips the portal reachability check and treats the run as offline, so the `online` cases (`basemap.set` to a portal basemap) are skipped and the run cannot cover every operation. If the operator approves the DENY card, the script cancels the issued token (`approval.cancel`) before it fails the case.

   The `table-query-broken-layer` case renames the files of a disposable shapefile copy while a layer points at it. If ArcGIS Pro holds the `.shp` open so it cannot be renamed, the script puts back any file it did rename, prints a warning naming the locked files, and records `table-query-broken-layer` as skipped with that reason; `layer.add` and `table.query` stay covered by their other cases.

6. Review `docs/acceptance/<date>-<sha7>/`. The files contain local paths, the Windows user in `%LOCALAPPDATA%` paths and audit records. Then run the Core tests and commit the folder yourself. The script never stages or commits.

`-Commit` is refused for `-PlanOnly`, for `-SkipVerify`, for a dirty working tree, when the target folder already exists, and when any selected step did not pass. Working evidence, including failed runs, stays under `artifacts/acceptance/<timestamp>/`, which is git-ignored.

## What an entry does not prove

An entry covers one commit, one ArcGIS Pro version, one machine and the sections listed in its manifest. Sections run in autonomous mode say so; they do not show that the local review flow works. The `operations` section does exercise it, but only for the eight cards it asks for. Automated PNG checks are not visual inspection; only items passed with `-VisuallyInspected` count as inspected. The package is unsigned. The limits in [deployment.md](../deployment.md#known-limits) still apply.

## Run notes

- `2026-10-03-e7deee2` predates `-RunNotes`, so its disclosures are in [`2026-10-03-e7deee2/ANNEX.md`](2026-10-03-e7deee2/ANNEX.md), added after the run: the eight approval cards were decided in the ArcGIS MCP pane by Claude (Anthropic's assistant) through computer use, at the maintainer's direction, not by the maintainer's own clicks; the first `project.open` card expired undecided and was re-queued; and ArcGIS Pro's modal "Save all edits?" prompt during the final `project.open` was answered "Yes" by the same means, which is what saved the matrix's feature edits (`project.save` at that commit did not save them) and why that call took about 277 s.

## Earlier live passes (not evidence)

A live acceptance pass of the ArcGIS-only build was run on the maintainer's workstation on 2026-09-09. It covered MCP protocol and reconnect, multi-instance discovery, feature editing, layer metadata, SDK geoprocessing, the optional ArcPy runner (in autonomous mode), the urban layout workflows and the Pittsburgh showcase, and idle/shutdown behavior.

Two findings are retained:

- Explicit remote cancellation of an accepted SDK geoprocessing call is not implemented. A client disconnect after acceptance is reported as `outcome_unknown`.
- ArcGIS Pro shutdown was not perfectly repeatable across disposable instances; one instance needed a PID-scoped forced close after 75 seconds.

The evidence from that pass (result JSON, hashes and images under `artifacts/`) is **local only**: `artifacts/` is git-ignored and nothing in this repository lets a reader verify those results. Treat them as the maintainer's notes, not as release evidence.

## Public history

This repository's history was rewritten from the private development repository on 2026-10-03, before it was published, to remove third-party test data. Commits that predate the generated synthetic data set no longer contain `tests/data`; earlier revisions of that folder held third-party street and parcel data and local paths. The two early evidence folders, `2026-09-28-5f34f16` and `2026-09-29-96f6a5b`, were removed from every commit because their captures and records show that earlier data. They are kept in the private development archive only. No other file was changed, and commit messages, authors and dates are unchanged.

Because the trees changed, every commit SHA differs from the private repository. Committed evidence, eval scorecards, fixtures and docs record the private SHAs and were not edited, so that the evidence still verifies against its `SHA256SUMS`. The private SHAs that appear in them map to these public commits:

| Private SHA | Public SHA | Where it appears | Commit |
|---|---|---|---|
| `e7deee2` | `c976c51` | Evidence commit recorded in [`2026-10-03-e7deee2/manifest.json`](2026-10-03-e7deee2/manifest.json) (`sha`), its `summary.md` and `ANNEX.md`, README, CHANGELOG, ROADMAP, deployment.md, site | fix: feature queries return row identity; dirty flag out of the revision |
| `54e0b33` | `6f969b5` | Commit that added the `2026-10-03-e7deee2` folder | docs(acceptance): live evidence for v0.3.0 (e7deee2) |
| `28e53ab` | `4de9551` | Live spot-check of `project.open` and `project.save` recorded in [ROADMAP.md](../ROADMAP.md) | fix(project): restore unsaved_project_changes as a pre-token refusal |
| `64aa31b` | `cfc51a0` | Commit that recorded the `28e53ab` spot-check | docs(roadmap): record the 28e53ab live spot-check |
| `83cb4ac` | `d027f90` | Merge of private pull request 6 | Live acceptance evidence and four defects it found (private PR 6) |
| `6fea916` | `3b8ac8b` | Merge of private pull request 7, the private `main` this history was rewritten from | Phase 6: go public with v0.3.0 (private PR 7) |
| `5f34f16` | `1f5cdf7` | Commit of the first (removed) evidence folder | fix(workspace): settle writes on ArcGIS idle |
| `96f6a5b` | `9b5cfcb` | Commit of the second (removed) evidence folder | fix(tools): committed acceptance manifests list only files the folder contains |
| `5d1c587` | `c26c57b` | [`evals/results/2026-09-28-5d1c587`](../../evals/results/2026-09-28-5d1c587/scorecard.md), evals/README.md | fix(search): drop synonym entries fitted to single eval tasks |
| `832a4ac` | `e21ffe9` | [`evals/results/2026-09-28-832a4ac`](../../evals/results/2026-09-28-832a4ac/scorecard.md), evals/README.md | test(evals): regenerate the descriptor fixture for the phase-2 gp operations |
| `2290311` | `bd63b06` | [`evals/results/2026-09-26-2290311`](../../evals/results/2026-09-26-2290311/scorecard.md), evals/README.md | feat(gp): rank gp.search with the shared index and a core-toolbox prior |
| `547d2bd` | `ec956d8` | [`evals/results/2026-09-26-547d2bd`](../../evals/results/2026-09-26-547d2bd/scorecard.md) | feat(evals): add live eval harness skeleton |
| `0b2006f` | `4b09068` | evals/README.md ("No synonyms" measurement) | docs: rewrite README for the development preview and link the new docs |
| `059d0cb` | `978aa8b` | Sample manifest fixture and `AcceptanceManifestTests.cs` | Merge main into phase-2 |
| `e257eab` | `cfef445` | Tag `v0.2.0` | Separate ArcGIS-only product, add feature/metadata/ArcPy ops, docs hub |
