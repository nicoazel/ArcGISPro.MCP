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

The gateway exposes discovery, validation, invocation, workflow, skill, and resource primitives as 16 tools, plus read-only `arcgis://` MCP resources and prompts generated from skills and saved workflows. Individual GIS operations remain in the add-in registry and enter model context only when the client searches for or describes them. Durable background-job primitives are not implemented.

## Boundaries

- `ArcGISProMCP.Core` owns descriptors, the typed JSON schema builder (`JsonSchemas`), risk policy, revision checks, audit contracts, declarative workflows, and the geoprocessing toolbox catalog and risk tiers (read from toolbox metadata on disk, never by running toolbox code). It has no Esri, WPF, or MCP transport dependency.
- `ArcGISProMCP.Bridge` owns the local transport and the typed result contracts (`BridgeContracts`) shared by the add-in and the gateway. Messages carry a protocol version and request id and are capped at 8 MiB.
- `ArcGISProMCP.Server` is the stateless stdio MCP gateway. It has no Esri reference. Every tool returns a `{ ok, result, error }` envelope as structured content with an output schema, and bridge failures become `isError` results. It also serves the MCP resources and prompts.
- `ArcGISProMCP.Operations` owns the host-neutral side of the ArcGIS Pro operations: descriptors, input/output schemas, argument validation, result shaping and dispatcher (thread) ownership. It references only Core and talks to ArcGIS Pro through small service interfaces (`IProjectService`, `IMapService`, `ILayerService`, `IViewCaptureService`, `IFeatureService`), so `tests/ArcGISProMCP.Operations.Tests` runs the real operations against fake services without ArcGIS Pro.
- `ArcGISProMCP.AddIn` owns the ArcGIS Pro SDK: the service implementations in `ArcGIS/Services`, the operations that still call the SDK directly (layout, symbology, metadata, tables, layer edits, geoprocessing), lifecycle, the named-pipe listener, and the thin MVVM dockpane.
- Each Pro process defaults to `ArcGISProMCP.v1.<pid>` and publishes a local host record containing PID, process start, pipe, and current project. The gateway auto-selects a sole live host, honors `ARCGIS_PRO_MCP_HOST_PID` or `ARCGIS_PRO_MCP_PIPE`, and fails closed when multiple hosts are ambiguous.
- The product is ArcGIS-only: it has no Rhino dependency and registers no `rhino.*` operations.

## Operation lifecycle

1. Read `system_get_state` and retain the workspace revision.
2. Search or browse the registry by intent/domain.
3. Describe only the candidate operations needed for the task.
4. Validate arguments against the descriptor's schema and the current revision. Validation does not resolve layers or paths. Optionally invoke with `dryRun: true`: operations that implement `IDryRunnableOperation` (currently `gp.run`) run their static checks; the rest return a generic description. A dry run never executes, needs no revision or token, and consumes no approval.
5. In default mode, confirmation-gated operations use `approval_request` with the exact arguments and revision. A person reviews the dockpane; call `approval_status` (optionally with `waitSeconds` to wait for the decision) for the single-use token, then invoke unchanged arguments/revision. A host started with explicit autonomous control skips this token gate but retains current-revision, schema, audit, idempotency, and operation-limit enforcement.
6. Read returned resource handles for images or larger observations.

Every ArcGIS object exposed across the bridge uses a stable URI-derived handle. Handles are resolved on each call and are expected to become invalid when a project closes or replaces the referenced object.

## Threading

ArcGIS mapping, layout, data, and CIM work is routed through one `IOperationDispatcher` onto the Main CIM Thread. Pane activation and bitmap capture use the WPF UI dispatcher. Results crossing the process boundary are plain records or JSON.

Concurrent pipe connections keep discovery and review accessible, while one execution gate serializes host operations and entire workflows. Panel actions use the same handler rather than a second executor. Short accepted SDK writes drain to a known outcome. Client cancellation/disconnection does not prove that a write was cancelled; post-transmission failures return `outcome_unknown`. Workflows support pinned-version, process-local idempotency keys; they are not crash-recoverable jobs.

## Extension rules

New operations subclass `ProOperationBase`, declare a complete `OperationDescriptor`, and are registered in `ProOperationCatalog`. Input schemas are built with the typed `JsonSchemas` builder (`Object`, `String`, `Integer`, `Array`, ...), which self-checks every schema against the argument validator at startup; operations with a stable payload also declare an `outputSchema`, which `registry_describe` wraps in the `resultSchema` envelope. Registry ids are stable, lowercase, dot-separated names. A breaking schema change creates a new operation version or id; it does not silently reinterpret an existing workflow.

Saved workflows are immutable JSON DAGs containing registered operation ids, arguments, dependencies and observation hints. Validation on save and run enforces the workflow's optional `allowedOperations` list; operations that execute user code (`gp.run`, `arcpy.run-script`) must always be listed explicitly. Current replay executes declared order and checks dependencies; it does not schedule parallel branches. If the workspace revision changes mid-run, replay stops with `workspace_changed` instead of adopting the new revision. Visual checks in skills are guidance for the caller, not automated image assertions. Runtime history is recorded separately so ranking can improve without changing workflow definitions.
