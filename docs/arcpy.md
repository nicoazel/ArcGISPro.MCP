# ArcPy execution

`arcpy.inspect-script` and `arcpy.run-script` are an optional trusted-local escape hatch. They are not registered unless ArcPy is explicitly enabled before ArcGIS Pro starts. Normal geoprocessing should use `gp.run`.

## Configuration

Set these environment variables for the ArcGIS Pro process, then restart Pro:

```text
ARCGIS_PRO_MCP_ENABLE_ARCPY=true
ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT=C:\ArcGISProMCP\approved-arcpy-scripts
ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT=C:\ArcGISProMCP\arcpy-runs
```

Optional overrides are:

```text
ARCGIS_PRO_MCP_ARCPY_PYTHON_EXE=C:\Program Files\ArcGIS\Pro\bin\Python\envs\arcgispro-py3\python.exe
ARCGIS_PRO_MCP_ARCPY_MAX_TIMEOUT_SECONDS=300
ARCGIS_PRO_MCP_ARCPY_MAX_OUTPUT_CHARS=65536
ARCGIS_PRO_MCP_ARCPY_MAX_SCRIPT_BYTES=1048576
```

The script and working roots must be separate absolute directory trees. Scripts must be relative `.py` files below the configured script root and cannot be reached through a reparse point. The Python override must be an explicit `python.exe` whose environment contains ArcPy.

## Invocation

1. Place a reviewed script in the approved script root.
2. Call `arcpy.inspect-script` with its relative path.
3. Request local review for `arcpy.run-script` using the returned SHA-256, exact arguments, timeout, and current workspace revision.
4. Approve locally, then invoke with the single-use token and unchanged request.

The runner copies the approved bytes into a unique working directory and executes that stable copy through `ProcessStartInfo.ArgumentList`; it does not use a command shell or accept inline source or caller-supplied environment variables. Arguments, aggregate argument length, script size, timeout, stdout, and stderr are bounded. Cancellation and timeout attempt to terminate the process tree.

## Trust boundary

This is not an OS sandbox. An approved script runs with the signed-in user's file, network, ArcGIS, licensing, and data authority. A hostile script can cause arbitrary external effects and can deliberately detach work that best-effort process-tree termination may not stop. Custom Python paths are trusted local configuration and are not signature-verified.

The runner deletes its stable script copy. If the script creates other files in the run directory, that non-empty directory is retained rather than recursively deleting unknown outputs. Operators need a retention policy for those outputs.

## Live acceptance

The repository ships the scripts the live acceptance harnesses expect in `tools/arcpy-scripts/`: `hello.py` (prints the ArcGIS version, licence level and its arguments as JSON; reads and writes nothing), `nonzero.py` (exits with code 7), `timeout.py` (sleeps for ten minutes) and `setup_acceptance.py` (creates a disposable `Acceptance.gdb/DesignSites` feature class in the folder given as its argument). `tools/run-live-operations.ps1` inspects `hello.py`, checks that a path outside the root is refused, and runs `hello.py` once after you approve its card. `tools/run-live-feature-gp-arcpy.ps1` uses the other three.

ArcPy cases run only when the host reports the `arcpy` capability. To enable it for an acceptance session, set the variables in a PowerShell window and start ArcGIS Pro from that same window, so only this Pro process sees them:

```powershell
$env:ARCGIS_PRO_MCP_ENABLE_ARCPY = 'true'
$env:ARCGIS_PRO_MCP_ARCPY_SCRIPT_ROOT = '<repo>\tools\arcpy-scripts'        # your checkout, for example C:\src\ArcGISPro.MCP
$env:ARCGIS_PRO_MCP_ARCPY_WORKING_ROOT = 'C:\MCP-scratch\mcp-acceptance\arcpy-runs'  # <DisposableRoot>\arcpy-runs
& 'C:\Program Files\ArcGIS\Pro\bin\ArcGISPro.exe' 'C:\MCP-scratch\mcp-acceptance\Acceptance.aprx'
```

The script root is the repository checkout, so the hashes the run pins are those of the committed scripts. The working root must be a separate folder; it collects one run directory per execution. Without these variables the ArcPy cases are recorded as skipped, `arcpy.inspect-script` and `arcpy.run-script` stay uncovered, and the `operations` section cannot pass.
