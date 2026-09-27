"""Build an explicitly illustrative, block-scale Pittsburgh mixed-use fixture.

The source inputs are the repository's EPSG:2272 (Pennsylvania South, US feet)
fixtures.  This is a reproducible design-study generator, not a zoning,
feasibility, market, or construction document.  It deliberately writes only
the named ``PittsburghShowcase*`` feature classes, and requires ``--replace``
before it will replace a prior run.

Run with ArcGIS Pro's Python interpreter, for example:

  propy.bat tools/create-pittsburgh-block-showcase.py --template-aprx ... \
    --parcels-source ... --buildings-source ... --streets-source ... \
    --output-geodatabase ... --replace
"""

import argparse
from pathlib import Path
from typing import Iterable, List, Optional, Tuple

import arcpy


EPSG_PENNSYLVANIA_SOUTH = 2272
OUTPUT_NAMES = (
    "PittsburghShowcaseFootprints",
    "PittsburghShowcaseMassing",
    "PittsburghShowcaseProgram",
    "PittsburghShowcasePublicRealm",
    "PittsburghShowcaseStreets",
    "PittsburghShowcaseBlocks",
    "PittsburghShowcaseContext",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--template-aprx", required=True, type=Path,
                        help="A disposable or copied .aprx used only for 3D conversion.")
    parser.add_argument("--parcels-source", required=True, type=Path)
    parser.add_argument("--buildings-source", required=True, type=Path)
    parser.add_argument("--streets-source", required=True, type=Path)
    parser.add_argument("--output-geodatabase", required=True, type=Path)
    parser.add_argument("--replace", action="store_true",
                        help="Replace only prior PittsburghShowcase* outputs.")
    return parser.parse_args()


def require_epsg_2272(path: Path) -> arcpy.SpatialReference:
    if not arcpy.Exists(str(path)):
        raise FileNotFoundError(f"Source fixture not found: {path}")
    spatial_reference = arcpy.Describe(str(path)).spatialReference
    if spatial_reference.factoryCode != EPSG_PENNSYLVANIA_SOUTH:
        raise ValueError(
            f"{path.name} must use EPSG:{EPSG_PENNSYLVANIA_SOUTH}; "
            f"found {spatial_reference.factoryCode or 'unknown'}.")
    return spatial_reference


def output_path(geodatabase: Path, name: str) -> str:
    return str(geodatabase / name)


def clear_outputs(geodatabase: Path, replace: bool) -> None:
    existing = [output_path(geodatabase, name) for name in OUTPUT_NAMES
                if arcpy.Exists(output_path(geodatabase, name))]
    if existing and not replace:
        raise FileExistsError(
            "Showcase outputs already exist. Re-run with --replace to replace only: "
            + ", ".join(Path(item).name for item in existing))
    for item in existing:
        arcpy.management.Delete(item)


def polygon(points: Iterable[Tuple[float, float]], sr: arcpy.SpatialReference) -> arcpy.Polygon:
    ring = arcpy.Array([arcpy.Point(x, y) for x, y in points])
    return arcpy.Polygon(ring, sr)


def rectangle(x: float, y: float, width: float, height: float,
              sr: arcpy.SpatialReference) -> arcpy.Polygon:
    return polygon(((x, y), (x + width, y), (x + width, y + height),
                    (x, y + height), (x, y)), sr)


def line(points: Iterable[Tuple[float, float]], sr: arcpy.SpatialReference) -> arcpy.Polyline:
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in points]), sr)


def create_feature_class(geodatabase: Path, name: str, geometry: str,
                         sr: arcpy.SpatialReference,
                         fields: List[Tuple[str, str, Optional[int]]]) -> str:
    path = output_path(geodatabase, name)
    arcpy.management.CreateFeatureclass(str(geodatabase), name, geometry,
                                        has_m="DISABLED", has_z="DISABLED",
                                        spatial_reference=sr)
    for field_name, field_type, length in fields:
        arcpy.management.AddField(path, field_name, field_type,
                                  field_length=length if length else None)
    return path


def create_program_and_footprints(geodatabase: Path, sr: arcpy.SpatialReference,
                                  origin_x: float, origin_y: float) -> Tuple[str, str]:
    fields = [("name", "TEXT", 64), ("program", "TEXT", 32), ("floors", "SHORT", None),
              ("height_ft", "DOUBLE", None), ("res_units", "LONG", None),
              ("jobs", "LONG", None), ("gross_sf", "DOUBLE", None)]
    footprints = create_feature_class(geodatabase, "PittsburghShowcaseFootprints", "POLYGON", sr, fields)
    program = create_feature_class(geodatabase, "PittsburghShowcaseProgram", "POLYGON", sr, fields)
    # Compact varied floorplates create a plausible active edge around a central civic mews.
    buildings = [
        ("Penn Avenue Lofts", "Housing", 12, 144, 148, 10, 168000, 35, 255, 118, 128),
        ("Market Hall", "Retail", 2, 28, 0, 78, 42000, 170, 305, 178, 72),
        ("Liberty Hotel", "Hospitality", 10, 120, 0, 142, 185000, 370, 255, 100, 150),
        ("Mellon Workhouse", "Workspace", 18, 216, 24, 640, 318000, 505, 255, 126, 156),
        ("Civic Forum", "Civic", 5, 64, 0, 96, 76000, 505, 448, 152, 116),
        ("Bloomfield Flats", "Housing", 7, 84, 92, 12, 104000, 74, 495, 137, 104),
        ("Smallman Studios", "Workspace", 8, 96, 36, 210, 132000, 248, 495, 128, 106),
        ("Bakery Row", "Retail", 3, 42, 16, 114, 61000, 404, 495, 106, 106),
        ("Gardenside Homes", "Housing", 6, 72, 74, 6, 82000, 570, 610, 126, 114),
        ("Butler Exchange", "Retail", 4, 52, 18, 164, 96000, 570, 760, 145, 105),
        ("Community Health", "Civic", 4, 48, 0, 130, 89000, 340, 700, 175, 105),
        ("Corner House", "Housing", 9, 108, 86, 20, 118000, 92, 690, 176, 106),
    ]
    rows = [
        [rectangle(origin_x + x, origin_y + y, width, depth, sr), name, use,
         floors, height, units, jobs, gross]
        for name, use, floors, height, units, jobs, gross, x, y, width, depth in buildings
    ]
    field_names = ["SHAPE@"] + [field[0] for field in fields]
    # File geodatabases allow only one active write transaction in this process.
    # Populate the two intentionally parallel representations sequentially.
    with arcpy.da.InsertCursor(footprints, field_names) as footprint_cursor:
        for row in rows:
            footprint_cursor.insertRow(row)
    with arcpy.da.InsertCursor(program, field_names) as program_cursor:
        for row in rows:
            program_cursor.insertRow(row)
    return footprints, program


def create_public_realm(geodatabase: Path, sr: arcpy.SpatialReference,
                        origin_x: float, origin_y: float) -> str:
    realm = create_feature_class(
        geodatabase, "PittsburghShowcasePublicRealm", "POLYGON", sr,
        [("name", "TEXT", 64), ("realm_type", "TEXT", 32), ("area_sf", "DOUBLE", None)])
    spaces = [
        ("Civic Mews", "pedestrian mews", 222, 330, 245, 116),
        ("Market Square", "plaza", 300, 374, 172, 96),
        ("Rain Garden Walk", "green link", 515, 590, 42, 280),
        ("Penn Parklet", "parklet", 50, 625, 190, 48),
        ("Festival Court", "shared street", 250, 625, 265, 58),
    ]
    with arcpy.da.InsertCursor(realm, ["SHAPE@", "name", "realm_type", "area_sf"]) as cursor:
        for name, kind, x, y, width, height in spaces:
            shape = rectangle(origin_x + x, origin_y + y, width, height, sr)
            cursor.insertRow([shape, name, kind, shape.getArea("PLANAR", "SQUAREFEET")])
    return realm


def create_streets_and_blocks(geodatabase: Path, sr: arcpy.SpatialReference,
                              origin_x: float, origin_y: float,
                              fixture_streets: Path) -> Tuple[str, str]:
    streets = create_feature_class(
        geodatabase, "PittsburghShowcaseStreets", "POLYLINE", sr,
        [("name", "TEXT", 64), ("street_type", "TEXT", 32)])
    blocks = create_feature_class(
        geodatabase, "PittsburghShowcaseBlocks", "POLYGON", sr,
        [("block_id", "TEXT", 16), ("role", "TEXT", 32)])
    street_rows = [
        ("Penn Avenue", "main street", ((0, 0), (780, 0))),
        ("Liberty Avenue", "complete street", ((0, 910), (780, 910))),
        ("Mellon Street", "neighborhood street", ((0, 0), (0, 910))),
        ("Smallman Street", "neighborhood street", ((780, 0), (780, 910))),
        ("Market Street", "shared street", ((0, 560), (780, 560))),
        ("Civic Mews", "pedestrian connection", ((220, 330), (515, 330))),
    ]
    with arcpy.da.InsertCursor(streets, ["SHAPE@", "name", "street_type"]) as cursor:
        # Retain the fixture's linework as the wider city-block context, then add
        # the deliberately illustrative local grid and pedestrian connection.
        with arcpy.da.SearchCursor(str(fixture_streets), ["SHAPE@"]) as source_cursor:
            for (shape,) in source_cursor:
                if shape:
                    cursor.insertRow([shape, "Existing street fixture", "existing street context"])
        for name, kind, points in street_rows:
            cursor.insertRow([line(((origin_x + x, origin_y + y) for x, y in points), sr), name, kind])
    with arcpy.da.InsertCursor(blocks, ["SHAPE@", "block_id", "role"]) as cursor:
        for block_id, role, x, y, width, height in [
            ("A", "active mixed-use", 22, 22, 360, 510),
            ("B", "civic employment", 398, 22, 360, 510),
            ("C", "housing and services", 22, 582, 360, 306),
            ("D", "mixed-use neighborhood", 398, 582, 360, 306),
        ]:
            cursor.insertRow([rectangle(origin_x + x, origin_y + y, width, height, sr), block_id, role])
    return streets, blocks


def create_massing(footprints: str, geodatabase: Path, template_aprx: Path) -> str:
    # ArcGIS Pro extrusion converts the varied height_ft field into a Z-enabled multipatch.
    aprx = arcpy.mp.ArcGISProject(str(template_aprx))
    scene = aprx.createMap("Pittsburgh Showcase Fixture", "SCENE")
    layer = scene.addDataFromPath(footprints)
    layer.extrusion("MAX_HEIGHT", "$feature.height_ft")
    massing = output_path(geodatabase, "PittsburghShowcaseMassing")
    arcpy.ddd.Layer3DToFeatureClass(layer, massing, "", "DISABLE_COLORS_AND_TEXTURES")
    return massing


def validate_outputs(massing: str, program: str, realm: str, streets: str) -> None:
    description = arcpy.Describe(massing)
    massing_count = int(arcpy.management.GetCount(massing)[0])
    if description.shapeType.lower() != "multipatch" or not description.hasZ:
        raise RuntimeError("Pittsburgh showcase massing must be Z-enabled multipatch geometry.")
    if massing_count != 12 or description.extent.ZMax <= description.extent.ZMin:
        raise RuntimeError("Pittsburgh showcase massing must contain 12 varied-height buildings.")
    expected = {"Housing", "Retail", "Hospitality", "Workspace", "Civic"}
    actual = {row[0] for row in arcpy.da.SearchCursor(program, ["program"])}
    if not expected.issubset(actual):
        raise RuntimeError(f"Program mix is incomplete: expected {sorted(expected)}, found {sorted(actual)}.")
    if int(arcpy.management.GetCount(realm)[0]) < 5 or int(arcpy.management.GetCount(streets)[0]) < 6:
        raise RuntimeError("Showcase must retain connected public-realm and street context.")


def main() -> None:
    args = parse_args()
    template_aprx = args.template_aprx.resolve()
    geodatabase = args.output_geodatabase.resolve()
    if not template_aprx.is_file():
        raise FileNotFoundError(f"Template ArcGIS Pro project not found: {template_aprx}")
    if not geodatabase.is_dir() or geodatabase.suffix.lower() != ".gdb":
        raise FileNotFoundError(f"Output file geodatabase not found: {geodatabase}")
    parcels = args.parcels_source.resolve()
    buildings = args.buildings_source.resolve()
    streets = args.streets_source.resolve()
    spatial_reference = require_epsg_2272(parcels)
    require_epsg_2272(buildings)
    require_epsg_2272(streets)
    clear_outputs(geodatabase, args.replace)

    extent = arcpy.Describe(str(parcels)).extent
    # Locate the study on the fixture's city-block field rather than inventing a location.
    origin_x = extent.XMin + max(60.0, (extent.width - 780.0) / 2.0)
    origin_y = extent.YMin + max(60.0, (extent.height - 910.0) / 2.0)
    arcpy.conversion.ExportFeatures(str(buildings), output_path(geodatabase, "PittsburghShowcaseContext"))
    footprints, program = create_program_and_footprints(geodatabase, spatial_reference, origin_x, origin_y)
    realm = create_public_realm(geodatabase, spatial_reference, origin_x, origin_y)
    street_network, _ = create_streets_and_blocks(
        geodatabase, spatial_reference, origin_x, origin_y, streets)
    massing = create_massing(footprints, geodatabase, template_aprx)
    validate_outputs(massing, program, realm, street_network)
    print(f"Created illustrative Pittsburgh block showcase in {geodatabase}: 12 buildings, 5 public-realm spaces, 6 streets.")


if __name__ == "__main__":
    main()
