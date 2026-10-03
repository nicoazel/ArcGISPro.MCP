"""Exits with code 7 so live acceptance can check arcpy_exit_nonzero handling.

Used by tools/run-live-feature-gp-arcpy.ps1. Does not import ArcPy.
"""

from __future__ import annotations

import sys


def main() -> int:
    print("nonzero.py: exiting with code 7 on purpose", file=sys.stderr)
    return 7


if __name__ == "__main__":
    sys.exit(main())
