"""Create the deterministic 3D massing fixture used by urban stress tests.

Run with the ArcGIS Pro Python interpreter. The source footprints use a
projected coordinate system whose linear unit is feet, so the fixed extrusion
height below is also expressed in feet.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import arcpy


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--template-aprx", required=True, type=Path)
    parser.add_argument("--geodatabase", required=True, type=Path)
    parser.add_argument("--height", type=float, default=60.0)
    args = parser.parse_args()

    template_aprx = args.template_aprx.resolve()
    geodatabase = args.geodatabase.resolve()
    source = geodatabase / "ProposedBuildings"
    output = geodatabase / "ProposedMassing"

    if not template_aprx.is_file():
        raise FileNotFoundError(f"Template ArcGIS Pro project not found: {template_aprx}")
    if not geodatabase.is_dir():
        raise FileNotFoundError(f"Fixture geodatabase not found: {geodatabase}")
    if not arcpy.Exists(str(source)):
        raise FileNotFoundError(f"Source footprint feature class not found: {source}")
    if args.height <= 0:
        raise ValueError("--height must be greater than zero")

    project = arcpy.mp.ArcGISProject(str(template_aprx))
    scene = project.createMap("Urban Massing Fixture", "SCENE")
    layer = scene.addDataFromPath(str(source))
    layer.extrusion("MAX_HEIGHT", format(args.height, "g"))

    arcpy.env.overwriteOutput = True
    arcpy.ddd.Layer3DToFeatureClass(
        layer,
        str(output),
        "",
        "DISABLE_COLORS_AND_TEXTURES",
    )

    description = arcpy.Describe(str(output))
    count = int(arcpy.management.GetCount(str(output))[0])
    if description.shapeType.lower() != "multipatch" or not description.hasZ:
        raise RuntimeError("Generated fixture is not a Z-enabled multipatch feature class")
    if count != 9:
        raise RuntimeError(f"Generated fixture should contain 9 massing features, found {count}")
    if description.extent.ZMax <= description.extent.ZMin:
        raise RuntimeError("Generated fixture has no vertical extent")

    print(
        f"Created {output}: {count} multipatches, "
        f"z={description.extent.ZMin:g}..{description.extent.ZMax:g}"
    )


if __name__ == "__main__":
    main()
