# Negative-case error messages

Every negative case that failed as a call, with the message a client would see. Audit: no exception type names, no stack traces, no local paths, under 500 characters; the plan's expected fragments (argument or layer names) are checked as part of each case.

| Case | Operation | Error code | Message | Audit |
| --- | --- | --- | --- | --- |
| gp-describe-unknown-tool | gp.describe | tool_not_found | Tool 'nope.Tool' is not in the toolbox catalog. Use gp.search to find the execution name. | ok |
| gp-describe-bare-name | gp.describe | tool_not_found | Tool 'Buffer' is not in the toolbox catalog. Did you mean: analysis.Buffer, 3d.Buffer3D, analysis.GraphicBuffer, analysis.PairwiseBuffer, analysis.MultipleRingBuffer? | ok |
| layer-list-missing-map | layer.list | map_not_found | Map 'No Such Map' was not found. | ok |
| basemap-set-unknown | basemap.set | invalid_arguments | Unknown basemap 'NotABasemap'. Use a Pro basemap name such as None, Topographic, Streets, Imagery, or OpenStreetMap. | ok |
| layer-set-appearance-missing-layer | layer.set-appearance | layer_not_found | Layer 'No Such Layer' was not found in map 'Ops Map'. | ok |
| symbology-set-unique-values-polyline | symbology.set-unique-values | invalid_arguments | Layer 'Streets' has geometry type 'Polyline'; this category renderer supports polygon layers only. | ok |
| layer-set-appearance-stale-revision | layer.set-appearance | workspace_revision_mismatch | Workspace changed: expected 'stale-ops-matrix-revision', current '5599ba2b58ca29f2'. Refresh state before writing. | ok |
| layer-set-appearance-no-revision | layer.set-appearance | workspace_revision_required | Operation 'layer.set-appearance' changes the workspace and requires the revision from system_get_state. | ok |
| table-query-broken-layer | table.query | layer_data_source_unavailable | The data source of layer 'Broken' is unavailable (broken or missing). Repair its data source in ArcGIS Pro, or re-add it with layer.add using the same name and a valid source. | ok |
| layer-set-elevation-invalid-mode | layer.set-elevation | invalid_arguments | $.mode: Value must match one of the declared enum values. | ok |
| layout-add-map-frame-off-page | layout.add-map-frame | element_outside_page | Map frame 'Off Page Frame' at x 40, y 40 with size 5 x 5 does not fit on the 17 x 11 Inch page. Keep x and y at least 0 and x + width, y + height within the page. | ok |
| layout-ensure-surround-off-page | layout.ensure-surround | element_outside_page | Map surround 'Off Page Bar' at x 16, y 1 with size 2.5 x 0.5 does not fit on the 17 x 11 Inch page. Keep x and y at least 0 and x + width, y + height within the page. | ok |
| layout-ensure-surround-missing-frame | layout.ensure-surround | frame_not_found | Map frame 'No Such Frame' was not found on layout 'Ops Layout'. | ok |
| layout-inspect-missing-layout | layout.inspect | layout_not_found | Layout 'No Such Layout' was not found. | ok |
| layout-ensure-surround-unknown-kind | layout.ensure-surround | invalid_arguments | $.kind: Value must match one of the declared enum values. | ok |
| layout-ensure-surround-zero-width | layout.ensure-surround | invalid_arguments | $.width: Number must be greater than 0. | ok |
| gp-query-not-allowed | gp.query | tool_not_query_allowed | 'analysis.Buffer' is not a read-only query tool. gp.query runs only: management.GetCellValue, management.GetCount, management.GetRasterProperties. Use gp.run (with review) for other tools. | ok |
| gp-query-invalid-name | gp.query | invalid_arguments | $.tool: String does not match the required pattern. | ok |
| gp-run-dry-run-idempotency-conflict | gp.run | dry_run_idempotency_conflict | dryRun cannot be combined with idempotencyKey; a dry run executes nothing and is never cached. | ok |
| arcpy-inspect-escape | arcpy.inspect-script | arcpy_script_rejected | scriptPath escapes the configured script root. | ok |
| feature-create-point-geometry | feature.create | invalid_arguments | Geometry type 'Point' does not match layer shape type 'Polygon'. | ok |
| feature-create-two-vertices | feature.create | invalid_arguments | geometry.coordinates must contain at least 3 positions. | ok |
| feature-create-unknown-field | feature.create | invalid_arguments | Unknown field 'NoSuchField'. | ok |
| gp-run-without-token | gp.run | confirmation_required | Operation 'gp.run' requires confirmation bound to these arguments and workspace revision. | ok |
| feature-update-without-token | feature.update | confirmation_required | Operation 'feature.update' requires confirmation bound to these arguments and workspace revision. | ok |
| metadata-update-without-token | metadata.update | confirmation_required | Operation 'metadata.update' requires confirmation bound to these arguments and workspace revision. | ok |
| arcpy-run-without-token | arcpy.run-script | confirmation_required | Operation 'arcpy.run-script' requires confirmation bound to these arguments and workspace revision. | ok |
| feature-delete-without-token | feature.delete | confirmation_required | Operation 'feature.delete' requires confirmation bound to these arguments and workspace revision. | ok |
| project-save-without-token | project.save | confirmation_required | Operation 'project.save' requires confirmation bound to these arguments and workspace revision. | ok |
| project-open-without-token | project.open | confirmation_required | Operation 'project.open' requires confirmation bound to these arguments and workspace revision. | ok |
| feature-delete-denied | feature.delete | confirmation_required | Operation 'feature.delete' requires confirmation bound to these arguments and workspace revision. | ok |
