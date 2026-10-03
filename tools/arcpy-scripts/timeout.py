"""Sleeps far longer than any acceptance timeout so arcpy_timed_out can be checked.

Used by tools/run-live-feature-gp-arcpy.ps1 with timeoutSeconds = 1. Does not import ArcPy.
"""

from __future__ import annotations

import sys
import time


def main() -> int:
    print("timeout.py: sleeping until the runner stops this process", flush=True)
    time.sleep(600)
    return 0


if __name__ == "__main__":
    sys.exit(main())
