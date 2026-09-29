# Eval scorecard 2026-09-26 547d2bd

Model: none (deterministic retrieval)

| Suite | Tasks | recall@1 | recall@5 | MRR | Pass (own k) | Misses | Host |
|---|---:|---:|---:|---:|---:|---:|---|
| gp-search | 30 | 0.533 | 0.667 | 0.586 | 0.667 | 8 | ArcGIS Pro 3.7.1.1904 system toolboxes (2210 tools) |
| gp-search-fixture | 5 | 0.800 | 1.000 | 0.900 | 1.000 | 0 | synthetic fixture toolboxes (13 tools) |
| registry-search | 30 | 0.500 | 0.833 | 0.613 | 0.833 | 3 | OperationRegistry over 38 add-in descriptors (source fixture) |

## gp-search failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| gp-03 | keep only the areas where two polygon layers overlap | `analysis.Intersect`, `analysis.PairwiseIntersect` | 5 | miss | `na.ShareAsRouteLayers`, `ba.RemoveOverlapMultiple`, `parcel.FindGapsAndOverlaps`, `ba.RemoveOverlap`, `sfa.OverlayLayers` |
| gp-04 | combine several feature classes into a single one | `management.Merge`, `management.Append` | 5 | miss | `management.AppendAnnotation`, `ca.CellPhoneRecordsToFeatureClass`, `md.FeatureToNetCDF`, `mb.IterateFeatureClasses`, `locref.ConfigureAddressFeatureClasses` |
| gp-08 | distance from each well to the nearest river | `analysis.Near`, `analysis.GenerateNearTable` | 5 | miss | `stats.AverageNearestNeighbor`, `sa.DistanceAccumulation`, `na.MakeClosestFacilityAnalysisLayer`, `na.MakeLocationAllocationAnalysisLayer`, `ga.NearestNeighbor3D` |
| gp-11 | select rows with a SQL where clause | `management.SelectLayerByAttribute` | 5 | 8 | `analysis.Select`, `analysis.TableSelect`, `management.MakeTableView`, `management.MakeQueryTable`, `management.MakeFeatureLayer` |
| gp-12 | how many rows are in this table | `management.GetCount` | 5 | miss | `management.TableCompare`, `management.MatchPhotosToRowsByTime`, `conversion.TableToGeodatabase`, `management.TableToRelationshipClass`, `management.FeatureCompare` |
| gp-15 | compute the area of each polygon in acres | `management.CalculateGeometryAttributes` | 5 | miss | `management.ComputeDirtyArea`, `ga.ArealInterpolationLayerToPolygons`, `na.MakeServiceAreaAnalysisLayer`, `management.PolygonToLine`, `cartography.AggregatePolygons` |
| gp-17 | reproject a feature class to a different coordinate system | `management.Project` | 5 | miss | `locref.ConfigureAddressFeatureClasses`, `locref.ConfigureUtilityNetworkFeatureClass`, `locref.ConfigureUtilityNetworkFeatureClasses`, `3d.FeatureClassZToASCII`, `conversion.FeatureClassToShapefile` |
| gp-18 | permanently delete a feature class | `management.Delete` | 5 | miss | `3d.RemoveFeatureClassFromTerrain`, `management.RemoveFeatureClassFromTopology`, `locref.ConfigureAddressFeatureClasses`, `management.DeleteFeatures`, `3d.FeatureClassZToASCII` |
| gp-21 | fix invalid or broken geometries | `management.RepairGeometry` | 5 | miss | `management.RepairTrajectoryDatasetPaths`, `management.ExportMosaicDatasetPaths`, `management.CreateOrthoCorrectedRasterDataset`, `geocoding.CreateLocator`, `management.RepairMosaicDatasetPaths` |
| gp-25 | convert polygons to a raster | `conversion.PolygonToRaster`, `conversion.FeatureToRaster` | 5 | 20 | `ra.ConvertFeatureToRaster`, `ra.ConvertRasterToFeature`, `management.ConvertRasterFunctionTemplate`, `3d.RasterTin`, `3d.RasterDomain` |

## gp-search-fixture failures

None.

## registry-search failures

| Task | Query | Expected | k | First hit | Top 5 |
|---|---|---|---:|---:|---|
| rs-04 | Set up a local 3D scene called Massing Study | `map.ensure` | 5 | 10 | `layer.set-elevation`, `layout.set-text`, `metadata.update`, `basemap.set`, `feature.update` |
| rs-11 | Draw all the parks in one solid green fill | `symbology.set-simple` | 5 | miss | `feature.create`, `feature.delete`, `feature.update`, `arcpy.inspect-script`, `arcpy.run-script` |
| rs-15 | Find vacant parcels inside the downtown bounding box | `feature.query` | 5 | miss | `style.search`, `gp.run` |
| rs-16 | Highlight every fire hydrant within the study area | `feature.select` | 5 | miss | `map.clear-selection`, `map.list`, `project.save` |
| rs-28 | Show me a picture of what the map looks like now | `view.capture` | 5 | 10 | `layout.add-map-frame`, `map.activate`, `basemap.set`, `layout.inspect`, `layout.set-frame-extent` |
