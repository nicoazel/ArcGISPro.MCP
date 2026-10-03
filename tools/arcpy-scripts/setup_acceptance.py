"""Create the disposable feature-editing fixture for live acceptance.

Used by tools/run-live-feature-gp-arcpy.ps1. Takes one argument, a data directory
(created when missing), and creates <data directory>/Acceptance.gdb/DesignSites: an
editable polygon feature class in EPSG:2272 with GlobalIDs and the fields Name (text),
Units (long), FAR (double) and ReviewDate (date), holding one row named 'Baseline Site'.
Prints one JSON object: featureClass, geodatabase, arcgisVersion, product.
"""

from __future__ import annotations

import datetime
import json
import sys
from pathlib import Path

import arcpy

SPATIAL_REFERENCE = 2272
BASELINE_RING = [
    (1366200.0, 416640.0),
    (1366290.0, 416640.0),
    (1366290.0, 416730.0),
    (1366200.0, 416730.0),
    (1366200.0, 416640.0),
]


def main(argv: list[str]) -> int:
    if len(argv) != 1:
        print("usage: setup_acceptance.py <data-directory>", file=sys.stderr)
        return 2

    arcpy.SetLogHistory(False)
    data_directory = Path(argv[0]).resolve()
    data_directory.mkdir(parents=True, exist_ok=True)
    geodatabase = data_directory / "Acceptance.gdb"
    if arcpy.Exists(str(geodatabase)):
        print(f"Refusing to overwrite an existing geodatabase: {geodatabase}", file=sys.stderr)
        return 3
    arcpy.management.CreateFileGDB(str(data_directory), geodatabase.name)

    spatial_reference = arcpy.SpatialReference(SPATIAL_REFERENCE)
    feature_class = geodatabase / "DesignSites"
    arcpy.management.CreateFeatureclass(
        str(geodatabase), feature_class.name, "POLYGON", spatial_reference=spatial_reference
    )
    arcpy.management.AddField(str(feature_class), "Name", "TEXT", field_length=100)
    arcpy.management.AddField(str(feature_class), "Units", "LONG")
    arcpy.management.AddField(str(feature_class), "FAR", "DOUBLE")
    arcpy.management.AddField(str(feature_class), "ReviewDate", "DATE")
    arcpy.management.AddGlobalIDs([str(feature_class)])

    polygon = arcpy.Polygon(
        arcpy.Array([arcpy.Point(x, y) for x, y in BASELINE_RING]), spatial_reference
    )
    fields = ["SHAPE@", "Name", "Units", "FAR", "ReviewDate"]
    with arcpy.da.InsertCursor(str(feature_class), fields) as cursor:
        cursor.insertRow((polygon, "Baseline Site", 80, 3.5, datetime.datetime(2026, 1, 15, 12, 0)))

    install = arcpy.GetInstallInfo()
    print(
        json.dumps(
            {
                "featureClass": str(feature_class),
                "geodatabase": str(geodatabase),
                "arcgisVersion": install.get("Version"),
                "product": install.get("ProductName"),
            }
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
