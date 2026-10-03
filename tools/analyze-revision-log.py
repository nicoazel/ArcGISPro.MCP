"""Correlate post-write settle timings with the operations that caused them.

Reads an ArcGIS Pro add-in revision log (``revisions-<pid>.log``, written when
``ARCGIS_PRO_MCP_REVISION_LOG=1``) and an operation audit log (``audit.jsonl``). Each settle line

    <utc timestamp> \t settle-ok|settle-timeout|settle-unsettled \t <drainMs> \t <sampleMs>
        \t <samples> \t <revision>

is written when the settle ends, inside the operation that wrote, so it is assigned to the
innermost audited operation whose ``startedAt``..``completedAt`` interval contains its timestamp
(plus a small slack). Prints, per operation id: settle count, budget cap (timeout) rate, unsettled
count, and p50/p95 of drain, sample and total milliseconds, then the same over all settles.

Usage:
    python tools/analyze-revision-log.py revisions-1234.log --audit audit.jsonl
    python tools/analyze-revision-log.py revisions-1234.log --audit audit.jsonl --json

Standard library only; Python 3.11.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path

SETTLE_KINDS = ("settle-ok", "settle-timeout", "settle-unsettled")
UNMATCHED = "(unmatched)"
_FRACTION = re.compile(r"(\.\d{6})\d+")


@dataclass(frozen=True)
class Settle:
    at: datetime
    outcome: str
    drain_ms: int
    sample_ms: int
    samples: int
    revision: str

    @property
    def total_ms(self) -> int:
        return self.drain_ms + self.sample_ms


@dataclass(frozen=True)
class Operation:
    operation_id: str
    started_at: datetime
    completed_at: datetime


def parse_timestamp(text: str) -> datetime:
    """Parses .NET round-trip ("O") timestamps, whose 7 fractional digits are cut to 6."""
    text = text.strip()
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    return datetime.fromisoformat(_FRACTION.sub(r"\1", text, count=1))


def read_settles(path: Path) -> tuple[list[Settle], int]:
    """Returns the settle lines and the number of settle lines in an older, untimed format."""
    settles: list[Settle] = []
    skipped = 0
    with path.open(encoding="utf-8", errors="replace") as handle:
        for line in handle:
            fields = line.rstrip("\r\n").split("\t")
            if len(fields) < 2 or fields[1] not in SETTLE_KINDS:
                continue
            try:
                settles.append(
                    Settle(
                        at=parse_timestamp(fields[0]),
                        outcome=fields[1],
                        drain_ms=int(fields[2]),
                        sample_ms=int(fields[3]),
                        samples=int(fields[4]),
                        revision=fields[5],
                    )
                )
            except (IndexError, ValueError):
                skipped += 1
    return settles, skipped


def read_operations(path: Path) -> list[Operation]:
    operations: list[Operation] = []
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            record = json.loads(line)
            if record.get("kind", "operation") != "operation":
                continue
            operations.append(
                Operation(
                    operation_id=record["operationId"],
                    started_at=parse_timestamp(record["startedAt"]),
                    completed_at=parse_timestamp(record["completedAt"]),
                )
            )
    return operations


def assign(settle: Settle, operations: list[Operation], slack: timedelta) -> str:
    """The innermost operation whose interval (widened by slack) contains the settle's end."""
    best: Operation | None = None
    for operation in operations:
        if operation.started_at - slack <= settle.at <= operation.completed_at + slack:
            duration = operation.completed_at - operation.started_at
            if best is None or duration < best.completed_at - best.started_at:
                best = operation
    return best.operation_id if best is not None else UNMATCHED


def percentile(values: list[int], fraction: float) -> int:
    """Nearest-rank percentile."""
    ordered = sorted(values)
    rank = max(1, math.ceil(fraction * len(ordered)))
    return ordered[rank - 1]


def summarize(settles: list[Settle]) -> dict[str, object]:
    timeouts = sum(1 for settle in settles if settle.outcome == "settle-timeout")
    unsettled = sum(1 for settle in settles if settle.outcome == "settle-unsettled")
    summary: dict[str, object] = {
        "count": len(settles),
        "timeouts": timeouts,
        "capRate": timeouts / len(settles),
        "unsettled": unsettled,
    }
    for name, values in (
        ("drainMs", [settle.drain_ms for settle in settles]),
        ("sampleMs", [settle.sample_ms for settle in settles]),
        ("totalMs", [settle.total_ms for settle in settles]),
        ("samples", [settle.samples for settle in settles]),
    ):
        summary[name] = {"p50": percentile(values, 0.50), "p95": percentile(values, 0.95)}
    return summary


def analyze(
    settles: list[Settle], operations: list[Operation], slack: timedelta
) -> dict[str, dict[str, object]]:
    if not operations:
        return {"TOTAL": summarize(settles)} if settles else {}
    groups: dict[str, list[Settle]] = defaultdict(list)
    for settle in settles:
        groups[assign(settle, operations, slack)].append(settle)
    report = {key: summarize(groups[key]) for key in sorted(groups)}
    if settles:
        report["TOTAL"] = summarize(settles)
    return report


def print_table(report: dict[str, dict[str, object]]) -> None:
    header = (
        f"{'operation':<28} {'count':>5} {'cap':>4} {'cap%':>6} {'unset':>5} "
        f"{'drain50':>7} {'drain95':>7} {'samp50':>6} {'samp95':>6} {'tot50':>6} {'tot95':>6}"
    )
    print(header)
    print("-" * len(header))
    for name, row in report.items():
        drain, sample, total = row["drainMs"], row["sampleMs"], row["totalMs"]
        print(
            f"{name:<28} {row['count']:>5} {row['timeouts']:>4} {row['capRate']:>6.1%} "
            f"{row['unsettled']:>5} {drain['p50']:>7} {drain['p95']:>7} "
            f"{sample['p50']:>6} {sample['p95']:>6} {total['p50']:>6} {total['p95']:>6}"
        )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("revision_log", type=Path, help="revisions-<pid>.log from the add-in")
    parser.add_argument("--audit", type=Path, help="audit.jsonl to attribute settles to operations")
    parser.add_argument(
        "--slack-ms",
        type=int,
        default=250,
        help="widen each operation interval by this much when matching (default 250)",
    )
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    args = parser.parse_args(argv)

    settles, skipped = read_settles(args.revision_log)
    operations = read_operations(args.audit) if args.audit else []
    if skipped:
        print(f"skipped {skipped} settle line(s) without timing (older add-in)", file=sys.stderr)
    if not settles:
        print("no timed settle lines found", file=sys.stderr)
        return 1

    report = analyze(settles, operations, timedelta(milliseconds=args.slack_ms))
    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print_table(report)
    return 0


if __name__ == "__main__":
    sys.exit(main())
