# Architecture

ArcGIS Pro MCP Studio keeps the public MCP host out of the ArcGIS Pro process. The split limits dependency conflicts and keeps the language-model-facing tool surface small.

```text
MCP client
  -> ArcGISProMCP.Server (stdio, official C# MCP SDK)
  -> same-user named pipe (versioned, length-prefixed JSON)
  -> ArcGISProMCP.AddIn
  -> searchable operation registry
  -> ArcGIS Pro SDK adapters on the required thread
```

The gateway exposes discovery, validation, invocation, workflow, skill, and resource primitives. Individual GIS operations remain in the add-in registry and enter model context only when the client searches for or describes them. Durable background-job primitives are not implemented.

## Boundaries

- `ArcGISProMCP.Core` owns descriptors, risk policy, revision checks, audit contracts, and declarative workflows. It has no Esri, WPF, Rhino, or MCP transport dependency.
- `ArcGISProMCP.Bridge` owns the local transport. Messages carry a protocol version and request id and are capped at 8 MiB.
- `ArcGISProMCP.Server` is the stateless stdio MCP gateway. It has no Esri or Rhino reference.
- `ArcGISProMCP.AddIn` owns ArcGIS Pro SDK operations, lifecycle, the named-pipe listener, and the thin MVVM dockpane.
- McNeel's `Rhino-MCP-Platform` remains the Rhino modeling command surface. A small public Rhino.Inside peer contract exposes session discovery, startup and existing pull/preview/sync services.

## Operation lifecycle

1. Read `system_get_state` and retain the workspace revision.
2. Search or browse the registry by intent/domain.
3. Describe only the candidate operations needed for the task.
4. Validate arguments against the descriptor and live state.
5. For risky operations, call `approval_request` with the exact arguments and revision. A person reviews the dockpane; poll `approval_status` for the single-use token, then invoke unchanged arguments/revision. Remote approval grants are not implemented.
6. Read returned resource handles for images or larger observations.

Every ArcGIS object exposed across the bridge uses a stable URI-derived handle. Handles are resolved on each call and are expected to become invalid when a project closes or replaces the referenced object.

## Threading

ArcGIS mapping, layout, data, and CIM work is routed through one `IOperationDispatcher` onto the Main CIM Thread. Pane activation and bitmap capture use the WPF UI dispatcher. Results crossing the process boundary are plain records or JSON.

Concurrent pipe connections keep discovery and review accessible, while one execution gate serializes host operations and entire workflows. Panel actions use the same handler rather than a second executor. Short accepted SDK writes drain to a known outcome. Client cancellation/disconnection does not prove that a write was cancelled; post-transmission failures return `outcome_unknown`. Workflows support pinned-version process-local retry keys; they are not crash-recoverable jobs.

## Extension rules

New operations subclass `ProOperationBase`, declare a complete `OperationDescriptor`, and are registered in `ProOperationCatalog`. Registry ids are stable, lowercase, dot-separated names. A breaking schema change creates a new operation version or id; it does not silently reinterpret an existing workflow.

Saved workflows are immutable JSON DAGs containing registered operation ids, arguments, dependencies and observation hints. Current replay executes declared order and checks dependencies; it does not schedule parallel branches. Visual checks in skills are guidance for the caller, not automated image assertions. Runtime history is recorded separately so ranking can improve without changing workflow definitions.
