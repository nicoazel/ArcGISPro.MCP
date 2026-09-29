# Eval scorecard 2026-09-28 5d1c587

Model: none (deterministic retrieval)

| Suite | Tasks | recall@1 | recall@5 | MRR | Pass (own k) | Misses | Host |
|---|---:|---:|---:|---:|---:|---:|---|
| gp-search | 30 | 0.667 | 0.900 | 0.751 | 0.900 | 3 | ArcGIS Pro 3.7.1.1904 system toolboxes (2210 tools) |
| gp-search-fixture | 5 | 0.800 | 1.000 | 0.867 | 1.000 | 0 | synthetic fixture toolboxes (13 tools) |
| gp-search-holdout | 16 | 0.688 | 0.875 | 0.771 | 0.875 | 2 | ArcGIS Pro 3.7.1.1904 system toolboxes (2210 tools) |
| registry-search | 30 | 0.600 | 0.867 | 0.716 | 0.867 | 1 | OperationRegistry over 41 add-in descriptors (descriptor dump) |
| registry-search-holdout | 16 | 0.813 | 0.938 | 0.865 | 0.938 | 1 | OperationRegistry over 41 add-in descriptors (descriptor dump) |

## gp-search failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| gp-03 | keep only the areas where two polygon layers overlap | `analysis.Intersect`, `analysis.PairwiseIntersect` | 5 | miss | `ga.ArealInterpolationLayerToPolygons`, `cartography.IntersectingLayersMasks`, `na.MakeServiceAreaAnalysisLayer`, `analysis.ApportionPolygon`, `management.PolygonToLine` |
| gp-12 | how many rows are in this table | `management.GetCount` | 5 | miss | `conversion.TableToGeodatabase`, `management.PivotTable`, `management.TableToRelationshipClass`, `conversion.TableToDBASE`, `management.MatchPhotosToRowsByTime` |
| gp-15 | compute the area of each polygon in acres | `management.CalculateGeometryAttributes` | 5 | miss | `management.ComputeDirtyArea`, `management.PolygonToLine`, `ga.ArealInterpolationLayerToPolygons`, `3d.PolygonVolume`, `management.ComputeMosaicCandidates` |

## gp-search-fixture failures

None.

## gp-search-holdout failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| gph-03 | stitch adjacent polygons together into one outline | `management.Dissolve`, `analysis.PairwiseDissolve`, `cartography.AggregatePolygons` | 5 | miss | `management.Mosaic`, `management.MosaicToNewRaster`, `management.PolygonToLine`, `analysis.PolygonNeighbors`, `analysis.CreateThiessenPolygons` |
| gph-05 | pull out the roads whose type is highway into a new feature class | `analysis.Select`, `conversion.ExportFeatures` | 5 | miss | `cartography.ResolveRoadConflicts`, `cartography.MergeDividedRoads`, `locref.ConfigureAddressFeatureClasses`, `management.AppendAnnotation`, `management.CreateFeatureclass` |

## registry-search failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| rs-13 | Show street names on the roads using the NAME field in 10 pt Arial | `label.configure` | 5 | 9 | `map.activate`, `layer.set-appearance`, `layout.activate`, `table.statistics`, `basemap.set` |
| rs-16 | Highlight every fire hydrant within the study area | `feature.select` | 5 | miss | `map.clear-selection` |
| rs-28 | Show me a picture of what the map looks like now | `view.capture` | 5 | 9 | `map.activate`, `layer.set-appearance`, `layout.activate`, `layout.add-map-frame`, `map.clear-selection` |
| rs-30 | Get the checksum of analysis.py so I can ask for approval to run it | `arcpy.inspect-script` | 5 | 6 | `arcpy.run-script`, `gp.run`, `gp.query`, `metadata.get`, `project.get` |

## registry-search-holdout failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| rsh-11 | Move this manhole to a new location | `feature.update` | 5 | miss | `layout.ensure`, `map.ensure`, `feature.create`, `layer.add`, `layout.set-text` |
