# Eval scorecard 2026-09-26 2290311

Model: none (deterministic retrieval)

| Suite | Tasks | recall@1 | recall@5 | MRR | Pass (own k) | Misses | Host |
|---|---:|---:|---:|---:|---:|---:|---|
| gp-search | 30 | 0.667 | 0.933 | 0.776 | 0.933 | 1 | ArcGIS Pro 3.7.1.1904 system toolboxes (2210 tools) |
| gp-search-fixture | 5 | 1.000 | 1.000 | 1.000 | 1.000 | 0 | synthetic fixture toolboxes (13 tools) |
| gp-search-holdout | 16 | 0.688 | 0.875 | 0.771 | 0.875 | 2 | ArcGIS Pro 3.7.1.1904 system toolboxes (2210 tools) |
| registry-search | 30 | 0.667 | 1.000 | 0.803 | 1.000 | 0 | OperationRegistry over 41 add-in descriptors (source fixture) |
| registry-search-holdout | 16 | 0.813 | 0.938 | 0.865 | 0.938 | 1 | OperationRegistry over 41 add-in descriptors (source fixture) |

## gp-search failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| gp-03 | keep only the areas where two polygon layers overlap | `analysis.Intersect`, `analysis.PairwiseIntersect` | 5 | miss | `ga.ArealInterpolationLayerToPolygons`, `cartography.IntersectingLayersMasks`, `na.MakeServiceAreaAnalysisLayer`, `analysis.ApportionPolygon`, `management.PolygonToLine` |
| gp-15 | compute the area of each polygon in acres | `management.CalculateGeometryAttributes` | 5 | 8 | `management.ComputeDirtyArea`, `3d.PolygonVolume`, `ga.ArealInterpolationLayerToPolygons`, `management.PolygonToLine`, `cartography.DelineateBuiltUpAreas` |

## gp-search-fixture failures

None.

## gp-search-holdout failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| gph-03 | stitch adjacent polygons together into one outline | `management.Dissolve`, `analysis.PairwiseDissolve`, `cartography.AggregatePolygons` | 5 | miss | `management.Mosaic`, `management.MosaicToNewRaster`, `management.PolygonToLine`, `analysis.PolygonNeighbors`, `analysis.CreateThiessenPolygons` |
| gph-05 | pull out the roads whose type is highway into a new feature class | `analysis.Select`, `conversion.ExportFeatures` | 5 | miss | `cartography.ResolveRoadConflicts`, `cartography.MergeDividedRoads`, `locref.ConfigureAddressFeatureClasses`, `management.AppendAnnotation`, `management.CreateFeatureclass` |

## registry-search failures

None.

## registry-search-holdout failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| rsh-11 | Move this manhole to a new location | `feature.update` | 5 | miss | `layout.ensure`, `map.ensure`, `feature.create`, `layer.add`, `layout.set-text` |
