# Security and operational limits

The add-in is local automation running with the signed-in ArcGIS Pro user's authority. It is not a sandbox for hostile code or geoprocessing tools.

- Named-pipe connections are restricted to the same Windows user. One Pro process per default pipe is supported.
- Requests are versioned, correlated and limited to 8 MiB.
- Mutating operations require a current workspace revision. Revision checks are an optimistic guard, not a database transaction or a lock against all external edits.
- Registry invocations can use process-lifetime idempotency keys. The cache is not durable across Pro restarts.
- Operation audit records store argument hashes and results in LocalAppData/ArcGISProMCP/audit.
- Confirmed operations fail closed unless `confirmationToken` is exactly `interactive`, which requests a fresh local Yes/No prompt (default No). Approval is specific to the displayed arguments and workspace revision; it is not a reusable bearer token. Rhino sync and generic geoprocessing require this approval.
- The generic geoprocessing runner has the user's GIS authority. A toolbox can contain destructive tools or user code; use trusted toolbox names and disposable output paths. Do not expose this bridge to an untrusted client.
- Workflows reference registered operations and cannot embed a script step. This does not sandbox a geoprocessing tool's internals.
- Captured images use opaque handles. Files remain in LocalAppData/ArcGISProMCP/resources; automatic retention cleanup is not implemented. Resource handles expire with the process.
- The public Rhino peer state excludes document file paths and geometry. GIS operation results can include source paths and project identifiers.
- Arbitrary ArcPy execution is not implemented.

Before production distribution: sign packages, bound/expire caches and resource storage, replace modal confirmation with an expiring review queue, extend multi-instance routing, and validate deployment against the organization's ArcGIS licensing and data policies. Long-running operations currently occupy the single bridge connection; durable jobs and a separately responsive control plane are not implemented.
