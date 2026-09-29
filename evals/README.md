# Evals

Measures whether an agent can find the right thing to call. The deterministic suites run in every
`dotnet test` and make no model calls; the live harness puts a real Claude model in the loop and is
run by hand.

| Suite | What is searched | Tasks | Runs |
|---|---|---:|---|
| `registry-search` (E1) | `OperationRegistry.Search`, as `registry_search` calls it, over the add-in's 41 operation descriptors | 30 | always |
| `gp-search` (E2) | `ToolboxCatalog.Search`, as `gp.search` calls it, over the installed ArcGIS Pro system toolboxes | 30 | when `C:\Program Files\ArcGIS\Pro\Resources\ArcToolBox\toolboxes` exists |
| `registry-search-holdout` (E1) | as `registry-search`; held out from tuning | 16 | always |
| `gp-search-holdout` (E2) | as `gp-search`; held out from tuning | 16 | when Pro is installed |
| `gp-search-fixture` (E2 subset) | `ToolboxCatalog.Search` over the synthetic toolboxes in `tests/ArcGISProMCP.Core.Tests/Fixtures/toolboxes` | 5 | always |
| `live` | Claude choosing MCP tool calls against a running gateway | see `live/live-tasks.jsonl` | by hand |

Golden trajectories (E3: ordered tool calls replayed against the in-process server) wait for the
Phase 4 operations seam and FakeHost.

## Layout

```
evals/
  tasks/registry-search.jsonl     E1 tasks
  tasks/gp-search.jsonl           E2 tasks (installed Pro)
  tasks/gp-search-fixture.jsonl   E2 CI subset (synthetic toolboxes)
  tasks/*-holdout.jsonl           held-out E1/E2 tasks, not used for tuning
  baseline.json                   measured metrics the tests gate on
  ArcGISProMCP.Evals/             task loader, runner, metrics, scorecard writer (net10.0)
  results/<yyyy-MM-dd>-<sha7>/    committed scorecards (scorecard.json + scorecard.md)
  live/                           Python live harness (not run in CI)
```

The tests live in `tests/ArcGISProMCP.Evals.Tests` and carry `[Trait("Category", "eval")]`.

## Task format

One JSON object per line; blank lines and `//` comments are skipped.

```json
{"id":"rs-20","group":"features-tables-metadata","task":"What is the average and maximum building height?","expected":["table.statistics"],"k":5,"filters":{"maxRisk":"ReadOnly"}}
```

- `expected` lists acceptable answers. Any one of them in the ranking is a hit; they are alternatives
  (for example `analysis.Buffer` or `analysis.PairwiseBuffer`), not a set that must all appear.
- `k` (default 5) is the cutoff the task must meet to pass.
- `filters` (registry suite only) mirrors the `registry_search` arguments: `domain`, `capabilities`, `maxRisk`.
- `group` is a reporting label. The registry tasks cover every group in `docs/reference.md`.

Tasks are written the way a person asks, not copied from descriptor aliases. A task that fails stays
in the suite; the failure is the measurement.

### Held-out sets

`registry-search-holdout.jsonl` and `gp-search-holdout.jsonl` were written and committed before the
Phase 4 search tuning, and were not read while choosing synonyms, weights or toolbox priority. Report
them next to the main suites: a change that lifts the main suite but not the held-out one is fitting
the task wording, not improving search. Do not tune against them; write a fresh held-out set when
they stop being unseen.

## Metrics

Each search is asked for 20 results.

- **recall@1 / recall@5**: share of tasks with an expected id at rank 1 / in the top 5.
- **MRR**: mean of 1/rank of the first expected id (0 when it is not in the top 20).
- **taskPassRate**: share of tasks that met their own `k`.
- **meanFirstHitRank**: mean rank of the first expected id over tasks that found one.
- **misses**: tasks with no expected id in the top 20.

`schemaValidArgs`, `approvalDiscipline` and `taskSuccess` are trajectory metrics; they are `null` for
retrieval suites and filled in by the live harness.

## Running

```powershell
dotnet test tests/ArcGISProMCP.Evals.Tests --filter "Category=eval"
```

A suite fails when its recall@5 drops more than 0.05 below `baseline.json`, or when its task count
no longer matches the baseline. Per-task results, including every failing task and its top 5, are in
the test output.

To write a scorecard for the current commit:

```powershell
$env:EVALS_WRITE_RESULTS = '1'; dotnet test tests/ArcGISProMCP.Evals.Tests --filter "Category=eval"
```

This writes `evals/results/<yyyy-MM-dd>-<sha7>/scorecard.json` and `scorecard.md`. Suites are merged
into one scorecard, and `dirty` records whether the worktree had changes outside `evals/results`.
Commit the scorecard in its own commit after the commit it measures.

### Changing the baseline

`baseline.json` holds measured values, not targets. When search improves, commit the new scorecard
and raise the baseline in the same change. If you add or remove tasks, re-measure: the tests refuse
a baseline taken on a different task count. `gp-search` depends on the installed Pro version, which
the baseline note and the scorecard host record.

## Search tuning (Phase 4)

`registry_search` and `gp.search` share `Core/Search`: punctuation stripping, camel-case splitting,
light stemming, whole-word over prefix over compound matching, a softened inverse document frequency,
and a small curated synonym map of general GIS vocabulary (`SearchSynonyms`). `gp.search` also gives the
core system toolboxes a modest prior (`GpToolboxPriority`: x1.25 for analysis, management, conversion,
cartography, edit and stats; x1.1 for Spatial Analyst and 3D Analyst).

The free parameters (rarity floor, core factor) were chosen on the main suites only. The held-out suites
were written first and measured before and after, never used for choices.

| Suite | Tasks | recall@1 before -> after | recall@5 before -> after | MRR before -> after |
|---|---:|---|---|---|
| registry-search | 30 | 0.500 -> 0.667 | 0.833 -> 1.000 | 0.607 -> 0.803 |
| registry-search-holdout | 16 | 0.813 -> 0.813 | 0.938 -> 0.938 | 0.856 -> 0.865 |
| gp-search | 30 | 0.533 -> 0.667 | 0.667 -> 0.933 | 0.586 -> 0.776 |
| gp-search-holdout | 16 | 0.500 -> 0.688 | 0.688 -> 0.875 | 0.559 -> 0.771 |
| gp-search-fixture | 5 | 0.800 -> 1.000 | 1.000 -> 1.000 | 0.900 -> 1.000 |

"Before" is `832a4ac` (the descriptor fixture regenerated for the phase-2 gp operations, 41 descriptors);
"after" is `2290311`, scorecard in `results/2026-09-26-2290311`. gp suites ran against ArcGIS Pro 3.7.1.1904.

## Descriptors

E1 searches the add-in's real descriptors from one file:
`tests/ArcGISProMCP.Operations.Tests/Fixtures/operation-descriptors.json`, a dump of all 41 descriptors
taken from the built add-in's registry. E1 loads every descriptor in full, registers them in a real
`OperationRegistry` and searches it with the product code.

Two tests keep the dump true:

- `DescriptorGuardTests` (Operations.Tests) composes every portable operation in
  `src/ArcGISProMCP.Operations` with fake ArcGIS services and requires each descriptor to equal its dump
  entry field by field, schemas included.
- `DescriptorDumpTests` (Evals.Tests) covers the operations that stay in the add-in, which cannot be
  constructed without ArcGIS Pro: it reads the `OperationDescriptor.Create(...)` calls in
  `src/ArcGISProMCP.Operations` and `src/ArcGISProMCP.AddIn/Operations`, requires the same set of ids as
  the dump, and compares the search-relevant fields (title, summary, tags, aliases, capabilities, risk,
  confirmation, user-code flag).

When a descriptor changes on purpose, edit its dump entry in the same commit and re-run the evals.

## Live harness

`live/run_live_eval.py` connects to the gateway over stdio with the `mcp` client, gives Claude the
gateway's tools, and runs a manual tool loop (`tool_choice` auto). A person approves risky
operations in the ArcGIS Pro panel; the harness never approves. Each task is graded on:

- **schemaValidArgs**: every tool input validates against the tool's `inputSchema`, and every operation
  `arguments` object against the operation's schema from `registry_describe`.
- **approvalDiscipline**: every `registry_invoke` of a confirmation-gated operation follows an
  `approval_request` for the same operation and identical arguments.
- **expectedOpsReached**: every operation in the task's `expected_ops` was invoked without an error.
- **taskSuccess**: all three, with no refusal, truncation or turn limit.

```powershell
py -3.11 -m pip install -r evals/live/requirements.txt
$env:ANTHROPIC_API_KEY = '...'
py -3.11 evals/live/run_live_eval.py --server C:\ArcGISProMCP\0.2.0\server\arcgis-pro-mcp.exe
py -3.11 evals/live/run_live_eval.py --server C:\ArcGISProMCP\0.2.0\server\arcgis-pro-mcp.exe --model claude-opus-5-5
```

The default model is `claude-sonnet-5`. Results go to `evals/results/<date>-<sha7>/live-<model>.json`
unless `--no-write` is passed. Run it against a disposable copy of a project: the tasks write.
