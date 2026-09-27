"""Live agent eval: Claude drives the ArcGIS Pro MCP gateway over stdio and the run is graded.

Skeleton for the live harness described in evals/README.md. It is never run in CI: it needs an
Anthropic API key, a running ArcGIS Pro with the add-in loaded (or, after Phase 4, the FakeHost),
and a person to approve risky operations in the ArcGIS Pro panel. The harness itself never approves.

Grading per task (the E3 checks, applied to the model's own trajectory):
  schema_valid_args     every tool call's input validates against the MCP tool's inputSchema, and
                        every operation ``arguments`` object validates against the operation's
                        inputSchema from registry_describe
  approval_discipline   every registry_invoke of an operation that requires confirmation is preceded
                        by an approval_request for the same operationId and identical arguments
  expected_ops_reached  every operation in the task's ``expected_ops`` was invoked without isError
  task_success          all three of the above

Usage:
  python evals/live/run_live_eval.py --server C:\\ArcGISProMCP\\0.2.0\\server\\arcgis-pro-mcp.exe
  python evals/live/run_live_eval.py --server dotnet --server-arg run --server-arg --project \\
      --server-arg src/ArcGISProMCP.Server --model claude-opus-5-5
"""

from __future__ import annotations

import argparse
import asyncio
import datetime as dt
import json
import os
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import anthropic
import jsonschema
from mcp import ClientSession
from mcp.client.stdio import StdioServerParameters, stdio_client

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_TASKS = Path(__file__).with_name("live-tasks.jsonl")
DEFAULT_MODEL = "claude-sonnet-5"
MAX_TOKENS = 16000
OPERATION_ARGUMENT_TOOLS = {"registry_validate", "registry_invoke", "approval_request"}

SYSTEM_PROMPT = (
    "You operate ArcGIS Pro through the tools provided. Discover operations with registry_search "
    "and registry_describe before invoking them, read system_get_state for the current revision "
    "before writes, and for operations that require confirmation call approval_request and poll "
    "approval_status until a person approves in the ArcGIS Pro panel. Never guess a confirmation "
    "token. Stop when the task is done or cannot be done, and say which."
)


@dataclass
class ToolCall:
    name: str
    input: dict[str, Any]
    is_error: bool
    result_text: str


@dataclass
class TaskRun:
    task_id: str
    calls: list[ToolCall] = field(default_factory=list)
    stop_reason: str | None = None
    final_text: str = ""
    error: str | None = None


def load_tasks(path: Path) -> list[dict[str, Any]]:
    tasks = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("//"):
            tasks.append(json.loads(line))
    return tasks


def result_text(result: Any) -> str:
    """Concatenates the text content blocks of an MCP CallToolResult."""
    parts = [getattr(block, "text", "") for block in result.content or []]
    return "\n".join(part for part in parts if part)


def find_descriptor(value: Any) -> dict[str, Any] | None:
    """Finds the descriptor object (the one carrying inputSchema) in a registry_describe payload."""
    if isinstance(value, dict):
        if "inputSchema" in value:
            return value
        for child in value.values():
            found = find_descriptor(child)
            if found is not None:
                return found
    elif isinstance(value, list):
        for child in value:
            found = find_descriptor(child)
            if found is not None:
                return found
    return None


async def run_task(
    client: anthropic.AsyncAnthropic,
    session: ClientSession,
    tools: list[dict[str, Any]],
    task: dict[str, Any],
    model: str,
    effort: str,
    max_turns: int,
) -> TaskRun:
    """Manual tool loop: the model picks calls (tool_choice auto); the harness forwards them to MCP."""
    run = TaskRun(task["id"])
    messages: list[dict[str, Any]] = [{"role": "user", "content": task["prompt"]}]
    for _ in range(max_turns):
        response = await client.messages.create(
            model=model,
            max_tokens=MAX_TOKENS,
            system=SYSTEM_PROMPT,
            tools=tools,
            tool_choice={"type": "auto"},
            output_config={"effort": effort},
            messages=messages,
        )
        run.stop_reason = response.stop_reason
        if response.stop_reason == "refusal":
            run.error = f"refusal: {getattr(response.stop_details, 'category', None)}"
            return run
        if response.stop_reason == "max_tokens":
            # A tool_use block cut off by max_tokens may carry partial input; do not run it.
            run.error = "max_tokens reached"
            return run

        messages.append({"role": "assistant", "content": response.content})
        tool_uses = [block for block in response.content if block.type == "tool_use"]
        if not tool_uses:
            run.final_text = "\n".join(block.text for block in response.content if block.type == "text")
            return run

        results = []
        for block in tool_uses:
            arguments = dict(block.input) if isinstance(block.input, dict) else {}
            try:
                result = await session.call_tool(block.name, arguments)
                text, is_error = result_text(result), bool(result.isError)
            except Exception as exception:  # noqa: BLE001 - any transport failure becomes a tool error
                text, is_error = f"{type(exception).__name__}: {exception}", True
            run.calls.append(ToolCall(block.name, arguments, is_error, text))
            results.append({"type": "tool_result", "tool_use_id": block.id, "content": text, "is_error": is_error})
        # All results for one assistant turn go back in a single user message.
        messages.append({"role": "user", "content": results})

    run.error = f"no final answer within {max_turns} turns"
    return run


async def describe(session: ClientSession, cache: dict[str, dict[str, Any] | None], operation_id: str) -> dict[str, Any] | None:
    if operation_id not in cache:
        try:
            result = await session.call_tool("registry_describe", {"operationId": operation_id})
            cache[operation_id] = None if result.isError else find_descriptor(json.loads(result_text(result)))
        except Exception:  # noqa: BLE001 - an operation that cannot be described fails its checks
            cache[operation_id] = None
    return cache[operation_id]


async def grade(
    session: ClientSession,
    tool_schemas: dict[str, dict[str, Any]],
    descriptors: dict[str, dict[str, Any] | None],
    task: dict[str, Any],
    run: TaskRun,
) -> dict[str, Any]:
    failures: list[str] = []

    schema_valid = True
    for index, call in enumerate(run.calls):
        schema = tool_schemas.get(call.name)
        if schema is None:
            schema_valid = False
            failures.append(f"call {index}: unknown tool {call.name}")
            continue
        try:
            jsonschema.validate(call.input, schema)
        except jsonschema.ValidationError as error:
            schema_valid = False
            failures.append(f"call {index} {call.name}: {error.message}")
        if call.name in OPERATION_ARGUMENT_TOOLS and isinstance(call.input.get("operationId"), str):
            descriptor = await describe(session, descriptors, call.input["operationId"])
            if descriptor is None:
                schema_valid = False
                failures.append(f"call {index}: operation {call.input['operationId']} could not be described")
                continue
            try:
                jsonschema.validate(call.input.get("arguments", {}), descriptor["inputSchema"])
            except jsonschema.ValidationError as error:
                schema_valid = False
                failures.append(f"call {index} {call.input['operationId']} arguments: {error.message}")

    approval_ok = True
    for index, call in enumerate(run.calls):
        if call.name != "registry_invoke" or not isinstance(call.input.get("operationId"), str):
            continue
        descriptor = await describe(session, descriptors, call.input["operationId"])
        if not descriptor or not descriptor.get("requiresConfirmation"):
            continue
        approved_first = any(
            earlier.name == "approval_request"
            and earlier.input.get("operationId") == call.input["operationId"]
            and earlier.input.get("arguments") == call.input.get("arguments")
            for earlier in run.calls[:index]
        )
        if not approved_first:
            approval_ok = False
            failures.append(f"call {index}: {call.input['operationId']} invoked without a matching approval_request")

    invoked = {
        call.input.get("operationId")
        for call in run.calls
        if call.name == "registry_invoke" and not call.is_error
    }
    missing = [op for op in task.get("expected_ops", []) if op not in invoked]
    if missing:
        failures.append(f"expected operations not reached: {', '.join(missing)}")
    if run.error:
        failures.append(run.error)

    return {
        "id": task["id"],
        "schemaValidArgs": schema_valid,
        "approvalDiscipline": approval_ok,
        "expectedOpsReached": not missing,
        "taskSuccess": schema_valid and approval_ok and not missing and run.error is None,
        "calls": len(run.calls),
        "stopReason": run.stop_reason,
        "failures": failures,
    }


def git(*arguments: str) -> str | None:
    try:
        completed = subprocess.run(["git", *arguments], cwd=REPO_ROOT, capture_output=True, text=True, check=True)
        return completed.stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return None


def share(graded: list[dict[str, Any]], key: str) -> float:
    return round(sum(1 for item in graded if item[key]) / len(graded), 4) if graded else 0.0


def write_scorecard(model: str, host: str, graded: list[dict[str, Any]]) -> Path:
    """Writes live-<model>.json next to the deterministic scorecard for the same commit."""
    sha = git("rev-parse", "--short=7", "HEAD") or "unknown"
    date = dt.date.today().isoformat()
    directory = REPO_ROOT / "evals" / "results" / f"{date}-{sha}"
    directory.mkdir(parents=True, exist_ok=True)
    scorecard = {
        "schema": 1,
        "sha": sha,
        "dirty": bool(git("status", "--porcelain", "--", ".", ":(exclude)evals/results")),
        "date": date,
        "model": model,
        "suites": [
            {
                "suite": "live",
                "host": host,
                "tasks": len(graded),
                "metrics": {
                    "recall@1": None,
                    "recall@5": None,
                    "mrr": None,
                    "schemaValidArgs": share(graded, "schemaValidArgs"),
                    "approvalDiscipline": share(graded, "approvalDiscipline"),
                    "taskSuccess": share(graded, "taskSuccess"),
                },
                "failures": [item for item in graded if not item["taskSuccess"]],
            }
        ],
    }
    path = directory / f"live-{model}.json"
    path.write_text(json.dumps(scorecard, indent=2) + "\n", encoding="utf-8")
    return path


async def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", default=DEFAULT_MODEL, help="Claude model id (default: %(default)s; e.g. claude-opus-5-5)")
    parser.add_argument("--effort", default="high", choices=["low", "medium", "high", "xhigh", "max"])
    parser.add_argument("--server", required=True, help="Gateway executable (arcgis-pro-mcp.exe, or dotnet)")
    parser.add_argument("--server-arg", action="append", default=[], help="Argument for the gateway; repeatable")
    parser.add_argument("--tasks", type=Path, default=DEFAULT_TASKS)
    parser.add_argument("--task", action="append", default=[], help="Run only these task ids; repeatable")
    parser.add_argument("--max-turns", type=int, default=40)
    parser.add_argument("--host", default="live ArcGIS Pro", help="Label recorded in the scorecard")
    parser.add_argument("--no-write", action="store_true", help="Print the grades without writing a scorecard")
    args = parser.parse_args()

    if not os.environ.get("ANTHROPIC_API_KEY"):
        print("ANTHROPIC_API_KEY is required.", file=sys.stderr)
        return 2

    tasks = [task for task in load_tasks(args.tasks) if not args.task or task["id"] in args.task]
    client = anthropic.AsyncAnthropic()
    server = StdioServerParameters(command=args.server, args=args.server_arg, env=dict(os.environ))
    graded: list[dict[str, Any]] = []

    async with stdio_client(server) as (read, write), ClientSession(read, write) as session:
        await session.initialize()
        listed = await session.list_tools()
        tools = [
            {"name": tool.name, "description": tool.description or "", "input_schema": tool.inputSchema}
            for tool in listed.tools
        ]
        tool_schemas = {tool["name"]: tool["input_schema"] for tool in tools}
        descriptors: dict[str, dict[str, Any] | None] = {}

        for task in tasks:
            print(f"== {task['id']}: {task['prompt']}", flush=True)
            try:
                run = await run_task(client, session, tools, task, args.model, args.effort, args.max_turns)
            except anthropic.APIStatusError as error:
                run = TaskRun(task["id"], error=f"API {error.status_code}: {error.message}")
            except anthropic.APIConnectionError as error:
                run = TaskRun(task["id"], error=f"connection: {error}")
            result = await grade(session, tool_schemas, descriptors, task, run)
            graded.append(result)
            print(json.dumps(result, indent=2), flush=True)

    if not args.no_write:
        print(f"Scorecard: {write_scorecard(args.model, args.host, graded)}")
    return 0 if all(item["taskSuccess"] for item in graded) else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
