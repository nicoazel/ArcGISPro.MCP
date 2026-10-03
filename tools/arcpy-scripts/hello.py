"""Harmless ArcPy probe for live acceptance (tools/run-live-operations.ps1).

Prints one JSON object with the ArcGIS version, licence level, Python version and the
arguments it received. Reads and writes nothing.
"""

from __future__ import annotations

import json
import platform
import sys

import arcpy


def main() -> int:
    install = arcpy.GetInstallInfo()
    print(
        json.dumps(
            {
                "script": "hello.py",
                "arcgisVersion": install.get("Version"),
                "product": install.get("ProductName"),
                "licenseLevel": arcpy.ProductInfo(),
                "python": platform.python_version(),
                "arguments": sys.argv[1:],
            }
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
