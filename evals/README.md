# Evals

Measures whether an agent can find the right thing to call. The deterministic suites run in every
`dotnet test` and make no model calls; the live harness puts a real Claude model in the loop and is
run by hand.

| Suite | What is searched | Tasks | Runs |
|---|---|---:|---|
| `registry-search` (E1) | `OperationRegistry.Search`, as `registry_search` calls it, over the add-in's 38 operation descriptors | 30 | always |
| `gp-search` (E2) | `ToolboxCatalog.Search`, as `gp.search` calls it, over the installed ArcGIS Pro system toolboxes | 30 | when `C:\Program Files\ArcGIS\Pro\Resources\ArcToolBox\toolboxes` exists |
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
  fixtures/operation-descriptors.json   interim descriptor fixture (see below)
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

## Descriptor fixture (interim)

The add-in's descriptors live in an assembly that references Esri DLLs, so tests cannot build the real
operation catalog yet. Until the Phase 4 operations seam moves descriptors into an Esri-free project,
`fixtures/operation-descriptors.json` carries the search-relevant fields (id, title, summary, tags,
aliases, capabilities, risk, domain, confirmation, user-code flag) extracted from
`src/ArcGISProMCP.AddIn/Operations/*.cs`. E1 registers them in a real `OperationRegistry` and searches
it with the product code.

`DescriptorFixtureTests` fails when the fixture no longer matches the source. Regenerate it with:

```powershell
$env:EVALS_UPDATE_FIXTURE = '1'; dotnet test tests/ArcGISProMCP.Evals.Tests --filter "FullyQualifiedName~DescriptorFixtureTests"
```

After the seam, build the registry from `ProOperationCatalog` and delete the fixture and its extractor.

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
