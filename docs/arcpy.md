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
