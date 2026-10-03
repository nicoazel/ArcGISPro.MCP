"""Generate the synthetic fixture data committed under tests/data.

Everything this script writes is invented: a regular street grid, parcels cut
from its blocks, building footprints inset into the parcels, a study boundary,
a proposal cluster and its massing. No third-party data is read, so the output
can be redistributed with the repository (CC0, see tests/data/README.md).

The layout is fixed and every random choice comes from ``random.Random(seed)``,
so the same seed always yields the same geometry and attributes. Geodatabase
files also hold generated identifiers (dataset UUIDs, GlobalIDs) and
timestamps, so the binary files differ between runs even when the content does
not.

Run with the ArcGIS Pro Python interpreter (ArcPy, 3D Analyst for the massing):

  "C:\\Program Files\\ArcGIS\\Pro\\bin\\Python\\envs\\arcgispro-py3\\python.exe" ^
    tools\\create-synthetic-test-data.py --template-aprx <disposable .aprx> ^
    --output tests\\data --replace

The data is built in a temporary folder (``--work-dir``), the massing is
derived from ProposedBuildings by tools/create-urban-massing-fixture.py, the
build is byte-scanned for strings that must not ship (local paths, product and
vendor names), and only then are tests/data/SHP, tests/data/MasterPlan.gdb and
tests/data/expected-statistics.json replaced. Other files in the output folder
(README.md) are left alone.
"""

from __future__ import annotations

import argparse
import datetime
import json
import math
import random
import runpy
import shutil
import sys
import tempfile
from pathlib import Path

import arcpy

SEED = 2272
EPSG = 2272  # NAD 1983 StatePlane Pennsylvania South FIPS 3702 (US feet)

# Street grid: 10 x 8 blocks on a 500 ft pitch with 60 ft rights of way.
X0, Y0 = 1_363_100.0, 414_800.0
PITCH = 500.0
HALF_STREET = 30.0
COLS, ROWS = 10, 8
ALLEY = 12.0  # mid-block alley between the two rows of lots

# The central 2 x 2 blocks form one superblock: the proposal site.
SUPERBLOCK = {(4, 3), (5, 3), (4, 4), (5, 4)}
PARK_BLOCKS = [(3, 4), (6, 3)]
# The study boundary is the street outline of the central 6 x 4 blocks.
BOUNDARY_COLS = (2, 8)
BOUNDARY_ROWS = (2, 6)

# A diagonal transit spine from the south-west to the north-east corner area.
SPINE = ((X0, Y0 + 500.0), (X0 + 5000.0, Y0 + 3500.0))

MULTIPART_PARCELS = 10

SHAPEFILES = (
    "Polygon_MixedMultiPart_Parcels",
    "Polyline_MultipartMix_Streets",
    "Polygons_Single_Buildings",
    "Point_Multi_Mixed",
    "Boundary_Multipart_Polygon",
)
GDB_NAME = "MasterPlan.gdb"
SHAPEFILE_EXTENSIONS = (".shp", ".shx", ".dbf", ".prj", ".cpg")
STATISTICS_FILE = "expected-statistics.json"

# Matched case-insensitively, as UTF-8/ASCII and as UTF-16LE (file geodatabase strings).
FORBIDDEN = ("_11_git", "users", "rhino", "scratch", "dynamap", "t068437", "urbanfootprint")

VERTICAL_NAMES = (
    "Alder", "Birch", "Cedar", "Dogwood", "Elm", "Fir",
    "Ginkgo", "Hazel", "Ironwood", "Juniper", "Kauri",
)
ORDINALS = ("1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th", "9th")

# land_use_1 -> (land_use_c, built_form, lot width range in ft, building height range in ft)
USES = {
    "single_family": ("residential", "detached_house", (44.0, 66.0), (18.0, 30.0)),
    "townhome": ("residential", "rowhouse", (30.0, 42.0), (26.0, 36.0)),
    "multifamily": ("residential", "apartment_block", (80.0, 140.0), (40.0, 85.0)),
    "retail_commercial": ("commercial", "main_street", (60.0, 110.0), (16.0, 32.0)),
    "office": ("employment", "office_block", (100.0, 160.0), (50.0, 120.0)),
    "civic_facilities": ("civic", "institutional", (90.0, 150.0), (22.0, 46.0)),
    "parks_recreation": ("open_space", "open_space", (0.0, 0.0), (0.0, 0.0)),
    "vacant": ("vacant", "vacant_lot", (50.0, 80.0), (0.0, 0.0)),
}
CIVIC_KINDS = ("school", "library", "community_center", "clinic", "fire_station")

# Zoning districts by distance from the transit spine; weights per land use.
ZONES = (
    (450.0, "MX-T", {"multifamily": 34, "retail_commercial": 28, "office": 18,
                     "civic_facilities": 6, "townhome": 14}),
    (1000.0, "MX-N", {"multifamily": 18, "retail_commercial": 12, "townhome": 24,
                      "single_family": 34, "civic_facilities": 7, "office": 5}),
    (math.inf, "R-1", {"single_family": 86, "townhome": 6, "civic_facilities": 4, "vacant": 4}),
)

FEATURE_CODES = {"residential": 100, "commercial": 200, "employment": 200, "civic": 300}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--output", required=True, type=Path, help="The tests/data folder to replace.")
    parser.add_argument("--template-aprx", required=True, type=Path,
                        help="A disposable .aprx, opened read-only to derive ProposedMassing.")
    parser.add_argument("--seed", type=int, default=SEED)
    parser.add_argument("--work-dir", type=Path, help="Build folder (default: a new temporary folder).")
    parser.add_argument("--replace", action="store_true",
                        help="Replace existing SHP, MasterPlan.gdb and expected-statistics.json in --output.")
    return parser.parse_args()


# --- geometry helpers -------------------------------------------------------

def r2(value: float) -> float:
    return round(value, 2)


def block_bounds(i: int, j: int) -> tuple[float, float, float, float]:
    return (X0 + i * PITCH + HALF_STREET, Y0 + j * PITCH + HALF_STREET,
            X0 + (i + 1) * PITCH - HALF_STREET, Y0 + (j + 1) * PITCH - HALF_STREET)


def ring(xmin: float, ymin: float, xmax: float, ymax: float) -> list[tuple[float, float]]:
    # Clockwise, as shapefile outer rings are.
    return [(xmin, ymin), (xmin, ymax), (xmax, ymax), (xmax, ymin), (xmin, ymin)]


def polygon(rings: list[list[tuple[float, float]]], sr: arcpy.SpatialReference) -> arcpy.Polygon:
    parts = arcpy.Array([arcpy.Array([arcpy.Point(x, y) for x, y in part]) for part in rings])
    return arcpy.Polygon(parts, sr)


def polyline(parts: list[list[tuple[float, float]]], sr: arcpy.SpatialReference) -> arcpy.Polyline:
    return arcpy.Polyline(arcpy.Array([arcpy.Array([arcpy.Point(x, y) for x, y in part]) for part in parts]), sr)


def distance_to_spine(x: float, y: float) -> float:
    (ax, ay), (bx, by) = SPINE
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (ax + t * dx), y - (ay + t * dy))


def zone_for_block(i: int, j: int) -> tuple[str, dict[str, int]]:
    xmin, ymin, xmax, ymax = block_bounds(i, j)
    distance = distance_to_spine((xmin + xmax) / 2.0, (ymin + ymax) / 2.0)
    for limit, name, weights in ZONES:
        if distance < limit:
            return name, weights
    raise AssertionError("unreachable")


def pick(rng: random.Random, weights: dict[str, int]) -> str:
    names = list(weights)
    return rng.choices(names, weights=[weights[name] for name in names])[0]


# --- feature class helpers --------------------------------------------------

def create(folder: Path, name: str, geometry: str, sr: arcpy.SpatialReference,
           fields: list[tuple[str, str, int | None]], has_z: str = "DISABLED") -> str:
    arcpy.management.CreateFeatureclass(str(folder), name, geometry, has_m="DISABLED",
                                        has_z=has_z, spatial_reference=sr)
    path = str(folder / name)
    shapefile = path.lower().endswith(".shp")
    if shapefile:
        # A new shapefile carries a placeholder 'Id' field. A shapefile needs one attribute
        # field, so swap in a temporary one, drop 'Id', and remove the temporary field last.
        arcpy.management.AddField(path, "tmp_field", "SHORT")
        arcpy.management.DeleteField(path, "Id")
    for field_name, field_type, length in fields:
        arcpy.management.AddField(path, field_name, field_type, field_length=length)
    if shapefile:
        arcpy.management.DeleteField(path, "tmp_field")
    return path


def insert(path: str, fields: list[str], rows: list[list]) -> None:
    with arcpy.da.InsertCursor(path, fields) as cursor:
        for row in rows:
            cursor.insertRow(row)


# --- datasets ---------------------------------------------------------------

def make_lot_attributes(rng: random.Random, use: str, acres: float, width: float) -> dict:
    du = hh = pop = emp_ret = emp_off = emp = 0
    if use in ("single_family", "townhome"):
        du = 1
        hh = 1 if rng.random() < 0.93 else 0
        pop = hh * rng.randint(1, 4)
    elif use == "multifamily":
        du = max(4, round(acres * rng.uniform(35.0, 70.0)))
        hh = round(du * rng.uniform(0.88, 0.96))
        pop = round(hh * rng.uniform(1.7, 2.3))
        emp_ret = rng.choice((0, 0, 2, 4, 6))
        emp = emp_ret
    elif use == "retail_commercial":
        emp_ret = max(2, round(acres * rng.uniform(25.0, 45.0)))
        emp_off = rng.choice((0, 0, 1, 3))
        if rng.random() < 0.3:
            du = max(2, round(acres * 15.0))
            hh = round(du * 0.9)
            pop = round(hh * 1.8)
        emp = emp_ret + emp_off
    elif use == "office":
        emp_off = max(10, round(acres * rng.uniform(80.0, 160.0)))
        emp_ret = rng.choice((0, 2, 5, 8))
        emp = emp_off + emp_ret
    elif use == "civic_facilities":
        emp = max(4, round(acres * rng.uniform(10.0, 30.0)))
    elif use == "parks_recreation":
        emp = rng.randint(1, 4)

    if use == "single_family":
        use2 = "detached_large" if width >= 56.0 else "detached_small"
    elif use == "townhome":
        use2 = "attached"
    elif use == "multifamily":
        use2 = "mid_rise" if du / max(acres, 1e-9) >= 50.0 else "low_rise"
    elif use == "retail_commercial":
        use2 = "mixed_use_retail" if du else "neighborhood_retail"
    elif use == "office":
        use2 = "office_mid_rise" if emp_off >= 60 else "office_low_rise"
    elif use == "civic_facilities":
        use2 = rng.choice(CIVIC_KINDS)
    elif use == "parks_recreation":
        use2 = "neighborhood_park"
    else:
        use2 = "vacant_lot"
    return {"land_use_2": use2, "du": du, "hh": hh, "pop": pop,
            "emp": emp, "emp_ret": emp_ret, "emp_off": emp_off}


def build_parcels(rng: random.Random) -> list[dict]:
    """Return parcel records: rings (list of rectangles), use, zone, attributes, front side."""
    parcels: list[dict] = []
    regular = [(i, j) for j in range(ROWS) for i in range(COLS)
               if (i, j) not in SUPERBLOCK and (i, j) not in PARK_BLOCKS]
    multipart_blocks = set(rng.sample(regular, MULTIPART_PARCELS))

    for j in range(ROWS):
        for i in range(COLS):
            xmin, ymin, xmax, ymax = block_bounds(i, j)
            if (i, j) in SUPERBLOCK:
                continue  # added below as one site of four quadrants
            if (i, j) in PARK_BLOCKS:
                parcels.append({"rects": [(xmin, ymin, xmax, ymax)], "use": "parks_recreation",
                                "zone": "OS", "front": "S"})
                continue
            zone, weights = zone_for_block(i, j)
            depth = (ymax - ymin - ALLEY) / 2.0
            rows = (("S", ymin, ymin + depth), ("N", ymax - depth, ymax))
            pair_width = 0.0
            if (i, j) in multipart_blocks:
                # One owner holds facing lots on both sides of the alley: a two-part parcel.
                use = pick(rng, weights)
                low, high = USES[use][2]
                pair_width = r2(rng.uniform(low, high))
                parcels.append({"rects": [(xmin, ymin, xmin + pair_width, ymin + depth),
                                          (xmin, ymax - depth, xmin + pair_width, ymax)],
                                "use": use, "zone": zone, "front": "S"})
            for front, row_min, row_max in rows:
                x = xmin + pair_width
                while xmax - x > 1e-6:
                    use = pick(rng, weights)
                    low, high = USES[use][2]
                    width = r2(rng.uniform(low, high))
                    if xmax - x - width < 35.0:
                        width = xmax - x
                    parcels.append({"rects": [(r2(x), r2(row_min), r2(x + width), r2(row_max))],
                                    "use": use, "zone": zone, "front": front})
                    x += width

    sxmin, symin, _, _ = block_bounds(4, 3)
    _, _, sxmax, symax = block_bounds(5, 4)
    mx, my = (sxmin + sxmax) / 2.0, (symin + symax) / 2.0
    for rect, use in (((sxmin, symin, mx, my), "multifamily"),
                      ((mx, symin, sxmax, my), "retail_commercial"),
                      ((sxmin, my, mx, symax), "office"),
                      ((mx, my, sxmax, symax), "civic_facilities")):
        parcels.append({"rects": [rect], "use": use, "zone": "MX-T", "front": "S", "site": True})

    for number, parcel in enumerate(parcels, start=1):
        area = sum((x2 - x1) * (y2 - y1) for x1, y1, x2, y2 in parcel["rects"])
        acres = round(area / 43_560.0, 4)
        width = parcel["rects"][0][2] - parcel["rects"][0][0]
        parcel["id"] = f"P{number:04d}"
        parcel["area_gross"] = acres
        parcel.update(make_lot_attributes(rng, parcel["use"], acres, width))
    return parcels


def write_parcels(folder: Path, sr, parcels: list[dict]) -> str:
    path = create(folder, "Polygon_MixedMultiPart_Parcels.shp", "POLYGON", sr, [
        ("id", "TEXT", 8), ("ZONE", "TEXT", 8), ("land_use_c", "TEXT", 16), ("land_use_1", "TEXT", 24),
        ("land_use_2", "TEXT", 24), ("built_form", "TEXT", 24), ("area_gross", "DOUBLE", None),
        ("pop", "DOUBLE", None), ("hh", "DOUBLE", None), ("du", "DOUBLE", None), ("emp", "DOUBLE", None),
        ("emp_ret", "DOUBLE", None), ("emp_off", "DOUBLE", None),
    ])
    fields = ["SHAPE@", "id", "ZONE", "land_use_c", "land_use_1", "land_use_2", "built_form",
              "area_gross", "pop", "hh", "du", "emp", "emp_ret", "emp_off"]
    rows = []
    for parcel in parcels:
        use = parcel["use"]
        shape = polygon([ring(*rect) for rect in parcel["rects"]], sr)
        rows.append([shape, parcel["id"], parcel["zone"], USES[use][0], use, parcel["land_use_2"],
                     USES[use][1], parcel["area_gross"], float(parcel["pop"]), float(parcel["hh"]),
                     float(parcel["du"]), float(parcel["emp"]), float(parcel["emp_ret"]),
                     float(parcel["emp_off"])])
    insert(path, fields, rows)
    return path


def write_streets(folder: Path, sr) -> str:
    path = create(folder, "Polyline_MultipartMix_Streets.shp", "POLYLINE", sr, [
        ("NAME", "TEXT", 32), ("FULL_NAME", "TEXT", 48), ("FCC", "TEXT", 4),
        ("SPEED", "SHORT", None), ("ONEWAY", "TEXT", 2),
    ])
    removed = {("V", 5, 3), ("V", 5, 4), ("H", 4, 4), ("H", 4, 5)}  # inside the superblock
    joined = {("V", 5, 2): ("V", 5, 5), ("H", 4, 3): ("H", 4, 6)}  # one street across the site
    joined_tail = set(joined.values())
    rows = []

    def segment(kind: str, line: int, index: int) -> list[tuple[float, float]]:
        if kind == "V":
            x = X0 + line * PITCH
            return [(x, Y0 + index * PITCH), (x, Y0 + (index + 1) * PITCH)]
        y = Y0 + line * PITCH
        return [(X0 + index * PITCH, y), (X0 + (index + 1) * PITCH, y)]

    for i in range(COLS + 1):
        arterial = i in (2, 5, 8)
        for j in range(ROWS):
            key = ("V", i, j)
            if key in removed or key in joined_tail:
                continue
            parts = [segment("V", i, j)]
            if key in joined:
                parts.append(segment(*joined[key]))
            name = VERTICAL_NAMES[i]
            rows.append([polyline(parts, sr), name, f"{name} Avenue", "A31" if arterial else "A41",
                         35 if arterial else 25, "B"])
    for j in range(ROWS + 1):
        arterial = j in (2, 6)
        oneway = "B" if arterial or j % 2 == 0 else ("FT" if j % 4 == 1 else "TF")
        for i in range(COLS):
            key = ("H", j, i)
            if key in removed or key in joined_tail:
                continue
            parts = [segment("H", j, i)]
            if key in joined:
                parts.append(segment("H", *joined[key][1:]))
            name = ORDINALS[j]
            rows.append([polyline(parts, sr), name, f"{name} Street", "A31" if arterial else "A41",
                         35 if arterial else 25, oneway])

    # The spine runs below grade across the superblock, so it is one two-part feature.
    (ax, ay), (bx, by) = SPINE
    sxmin, symin, _, _ = block_bounds(4, 3)
    _, _, sxmax, symax = block_bounds(5, 4)
    t_in = max((sxmin - ax) / (bx - ax), (symin - ay) / (by - ay))
    t_out = min((sxmax - ax) / (bx - ax), (symax - ay) / (by - ay))

    def at(t: float) -> tuple[float, float]:
        return (r2(ax + t * (bx - ax)), r2(ay + t * (by - ay)))

    rows.append([polyline([[at(0.0), at(t_in)], [at(t_out), at(1.0)]], sr),
                 "Transit Spine", "Transit Spine", "B11", 45, "B"])
    insert(path, ["SHAPE@", "NAME", "FULL_NAME", "FCC", "SPEED", "ONEWAY"], rows)
    return path


def write_buildings(folder: Path, sr, parcels: list[dict], rng: random.Random) -> str:
    path = create(folder, "Polygons_Single_Buildings.shp", "POLYGON", sr, [
        ("NAME", "TEXT", 48), ("FEATURECOD", "LONG", None), ("UPDATE_YEA", "LONG", None),
        ("height_ft", "DOUBLE", None),
    ])
    rows = []
    for parcel in parcels:
        use = parcel["use"]
        if use in ("parks_recreation", "vacant") or parcel.get("site"):
            continue
        residential = use in ("single_family", "townhome")
        if residential and rng.random() >= 0.22:
            continue  # only a sample of houses is mapped, as in many footprint layers
        count = 1 if residential else rng.choice((1, 1, 1, 2, 3))
        x1, y1, x2, y2 = parcel["rects"][0]
        depth = y2 - y1
        setback = rng.uniform(12.0, 20.0) if residential else rng.uniform(0.0, 6.0)
        building_depth = min(depth - setback - 10.0,
                             rng.uniform(36.0, 56.0) if residential else depth * rng.uniform(0.45, 0.7))
        if parcel["front"] == "S":
            by1, by2 = y1 + setback, y1 + setback + building_depth
        else:
            by1, by2 = y2 - setback - building_depth, y2 - setback
        side, gap = 5.0, 8.0
        slice_width = (x2 - x1 - 2 * side - (count - 1) * gap) / count
        category = USES[use][0]
        low, high = USES[use][3]
        for k in range(count):
            bx1 = x1 + side + k * (slice_width + gap)
            shape = polygon([ring(r2(bx1), r2(by1), r2(bx1 + slice_width), r2(by2))], sr)
            name = "" if residential else f"{use.replace('_', ' ').title()} {parcel['id']}-{k + 1}"
            rows.append([shape, name, FEATURE_CODES[category], rng.randint(2012, 2025),
                         round(rng.uniform(low, high) * 2.0) / 2.0])
    insert(path, ["SHAPE@", "NAME", "FEATURECOD", "UPDATE_YEA", "height_ft"], rows)
    return path


def write_points(folder: Path, sr) -> str:
    path = create(folder, "Point_Multi_Mixed.shp", "MULTIPOINT", sr,
                  [("Name", "TEXT", 32), ("Number", "DOUBLE", None)])
    centres = [((x1 + x2) / 2.0, (y1 + y2) / 2.0)
               for x1, y1, x2, y2 in (block_bounds(*b) for b in ((3, 4), (6, 3), (2, 2), (7, 5)))]
    multi = arcpy.Multipoint(arcpy.Array([arcpy.Point(x, y) for x, y in centres]), sr)
    single = arcpy.Multipoint(arcpy.Array([arcpy.Point(X0 + 3250.0, Y0 + 2750.0)]), sr)
    insert(path, ["SHAPE@", "Name", "Number"], [[multi, "Multi", 4.0], [single, "Single", 1.0]])
    return path


def write_boundary(folder: Path, sr) -> str:
    path = create(folder, "Boundary_Multipart_Polygon.shp", "POLYGON", sr,
                  [("Name", "TEXT", 48), ("Number", "DOUBLE", None)])
    outline = polygon([ring(X0 + BOUNDARY_COLS[0] * PITCH, Y0 + BOUNDARY_ROWS[0] * PITCH,
                            X0 + BOUNDARY_COLS[1] * PITCH, Y0 + BOUNDARY_ROWS[1] * PITCH)], sr)
    for block in PARK_BLOCKS:
        outline = outline.difference(polygon([ring(*block_bounds(*block))], sr))
    if outline.partCount != 1 or outline.boundary().partCount != 3:
        raise RuntimeError("The study boundary must be one polygon with two holes.")
    insert(path, ["SHAPE@", "Name", "Number"], [[outline, "Boundary with two Holes", 2.0]])
    return path


def u_shape(x: float, y: float, w: float, h: float, open_north: bool) -> list[tuple[float, float]]:
    nw, nd = w * 0.56, h * 0.7  # courtyard notch
    a, b = x + (w - nw) / 2.0, x + (w + nw) / 2.0
    if open_north:
        pts = [(x, y), (x, y + h), (a, y + h), (a, y + h - nd), (b, y + h - nd), (b, y + h),
               (x + w, y + h), (x + w, y)]
    else:
        pts = [(x, y), (x, y + h), (x + w, y + h), (x + w, y), (b, y), (b, y + nd), (a, y + nd), (a, y)]
    return [(r2(px), r2(py)) for px, py in pts] + [(r2(x), r2(y))]


def write_geodatabase(folder: Path, sr) -> Path:
    arcpy.management.CreateFileGDB(str(folder), GDB_NAME)
    gdb = folder / GDB_NAME

    proposal = create(gdb, "ProposedBuildings", "POLYGON", sr, [("Name", "TEXT", 64), ("Number", "DOUBLE", None)])
    widths, heights = (196.0, 214.0, 204.0), (110.0, 124.0, 116.0)
    gap_x = (800.0 - sum(widths)) / 2.0
    gap_y = (600.0 - sum(heights)) / 2.0
    left, bottom = X0 + 2500.0 - 400.0, Y0 + 2000.0 - 300.0
    rows, number, y = [], 0, bottom
    for row, h in enumerate(heights):
        x = left
        for w in widths:
            number += 1
            shape = polygon([u_shape(x, y, w, h, open_north=row < 2)], sr)
            rows.append([shape, f"Proposal {number}", float(number)])
            x += w + gap_x
        y += h + gap_y
    insert(proposal, ["SHAPE@", "Name", "Number"], rows)

    sites = create(gdb, "DesignSites", "POLYGON", sr,
                   [("Name", "TEXT", 64), ("Units", "LONG", None), ("FAR", "DOUBLE", None),
                    ("ReviewDate", "DATE", None)])
    arcpy.management.AddGlobalIDs(sites)
    site = polygon([ring(X0 + 3060.0, Y0 + 2060.0, X0 + 3200.0, Y0 + 2180.0)], sr)
    insert(sites, ["SHAPE@", "Name", "Units", "FAR", "ReviewDate"],
           [[site, "Baseline Site", 96, 2.5, datetime.datetime(2026, 1, 15)]])
    return gdb


def derive_massing(gdb: Path, template_aprx: Path) -> None:
    script = Path(__file__).resolve().parent / "create-urban-massing-fixture.py"
    saved = sys.argv
    sys.argv = [str(script), "--template-aprx", str(template_aprx), "--geodatabase", str(gdb), "--height", "60"]
    try:
        runpy.run_path(str(script), run_name="__main__")
    finally:
        sys.argv = saved


# --- verification and publishing --------------------------------------------

def scan(root: Path) -> list[str]:
    patterns = [(p, p.encode("ascii"), p.encode("utf-16-le")) for p in FORBIDDEN]
    hits = []
    for file in sorted(root.rglob("*")):
        if not file.is_file():
            continue
        data = file.read_bytes().lower()
        for text, ascii_form, utf16_form in patterns:
            if ascii_form in data or utf16_form in data:
                hits.append(f"{file.relative_to(root)}: '{text}'")
    return hits


def statistics(build: Path, seed: int) -> dict:
    shp, gdb = build / "SHP", build / GDB_NAME
    parcels = str(shp / "Polygon_MixedMultiPart_Parcels.shp")
    rows = list(arcpy.da.SearchCursor(parcels, ["area_gross", "pop", "du", "emp", "land_use_1"]))
    uses: dict[str, int] = {}
    for row in rows:
        uses[row[4]] = uses.get(row[4], 0) + 1

    def count(path: Path) -> int:
        return int(arcpy.management.GetCount(str(path))[0])

    def multipart(path: Path) -> int:
        return sum(1 for (shape,) in arcpy.da.SearchCursor(str(path), ["SHAPE@"]) if shape.isMultipart)

    counts = {name: count(shp / f"{name}.shp") for name in SHAPEFILES}
    counts.update({name: count(gdb / name) for name in ("ProposedBuildings", "ProposedMassing", "DesignSites")})
    return {
        "generator": "tools/create-synthetic-test-data.py",
        "seed": seed,
        "layer": "Polygon_MixedMultiPart_Parcels",
        "matched": len(rows),
        "area_gross": round(math.fsum(row[0] for row in rows), 4),
        "pop": int(math.fsum(row[1] for row in rows)),
        "du": int(math.fsum(row[2] for row in rows)),
        "emp": int(math.fsum(row[3] for row in rows)),
        "counts": counts,
        "multipart": {name: multipart(shp / f"{name}.shp")
                      for name in ("Polygon_MixedMultiPart_Parcels", "Polyline_MultipartMix_Streets",
                                   "Polygons_Single_Buildings")},
        "land_use_1": dict(sorted(uses.items())),
    }


def validate(stats: dict, build: Path) -> None:
    counts = stats["counts"]
    expected = {"Point_Multi_Mixed": 2, "Boundary_Multipart_Polygon": 1, "ProposedBuildings": 9,
                "ProposedMassing": 9, "DesignSites": 1}
    for name, value in expected.items():
        if counts[name] != value:
            raise RuntimeError(f"{name} should have {value} features, found {counts[name]}.")
    if stats["multipart"]["Polygons_Single_Buildings"] != 0:
        raise RuntimeError("Building footprints must be single part.")
    if not stats["multipart"]["Polygon_MixedMultiPart_Parcels"] or not stats["multipart"]["Polyline_MultipartMix_Streets"]:
        raise RuntimeError("Parcels and streets must mix single and multipart features.")
    for use in ("multifamily", "retail_commercial", "office", "parks_recreation", "civic_facilities"):
        if not stats["land_use_1"].get(use):
            raise RuntimeError(f"land_use_1 has no '{use}' parcels; the workflows symbolize it.")
    for name in SHAPEFILES:
        if arcpy.Describe(str(build / "SHP" / f"{name}.shp")).spatialReference.factoryCode != EPSG:
            raise RuntimeError(f"{name} is not EPSG:{EPSG}.")


def publish(build: Path, output: Path, replace: bool) -> None:
    targets = [output / "SHP", output / GDB_NAME, output / STATISTICS_FILE]
    existing = [t for t in targets if t.exists()]
    if existing and not replace:
        raise FileExistsError("Output already exists; re-run with --replace: "
                              + ", ".join(str(t) for t in existing))
    for target in existing:
        if target.is_dir():
            shutil.rmtree(target)
        else:
            target.unlink()
    (output / "SHP").mkdir(parents=True)
    for file in sorted((build / "SHP").iterdir()):
        if file.suffix.lower() in SHAPEFILE_EXTENSIONS:
            shutil.copy2(file, output / "SHP" / file.name)
    (output / GDB_NAME).mkdir()
    for file in sorted((build / GDB_NAME).iterdir()):
        if file.is_file() and not file.name.lower().endswith(".lock"):
            shutil.copy2(file, output / GDB_NAME / file.name)
    shutil.copy2(build / STATISTICS_FILE, output / STATISTICS_FILE)


def main() -> None:
    args = parse_args()
    output = args.output.resolve()
    template_aprx = args.template_aprx.resolve()
    if not template_aprx.is_file():
        raise FileNotFoundError(f"Template ArcGIS Pro project not found: {template_aprx}")
    if not output.is_dir():
        raise FileNotFoundError(f"Output folder not found: {output}")

    work = args.work_dir.resolve() if args.work_dir else Path(tempfile.mkdtemp(prefix="synthetic-test-data-"))
    build = work / "data"
    if build.exists():
        shutil.rmtree(build)
    (build / "SHP").mkdir(parents=True)

    # Keep geoprocessing history and metadata (which record local paths) out of the files.
    arcpy.SetLogHistory(False)
    arcpy.SetLogMetadata(False)
    arcpy.env.overwriteOutput = True
    sr = arcpy.SpatialReference(EPSG)
    rng = random.Random(args.seed)

    parcels = build_parcels(rng)
    write_parcels(build / "SHP", sr, parcels)
    write_streets(build / "SHP", sr)
    write_buildings(build / "SHP", sr, parcels, rng)
    write_points(build / "SHP", sr)
    write_boundary(build / "SHP", sr)
    gdb = write_geodatabase(build, sr)
    derive_massing(gdb, template_aprx)
    arcpy.management.Compact(str(gdb))

    stats = statistics(build, args.seed)
    validate(stats, build)
    (build / STATISTICS_FILE).write_text(json.dumps(stats, indent=2) + "\n", encoding="utf-8", newline="\n")
    arcpy.management.ClearWorkspaceCache()

    hits = scan(build)
    if hits:
        raise RuntimeError("Generated files contain forbidden strings:\n  " + "\n  ".join(hits))

    publish(build, output, args.replace)
    hits = scan(output)
    if hits:
        raise RuntimeError("Published files contain forbidden strings:\n  " + "\n  ".join(hits))
    print(json.dumps(stats, indent=2))
    print(f"Replaced {output / 'SHP'}, {output / GDB_NAME} and {output / STATISTICS_FILE}.")


if __name__ == "__main__":
    main()
