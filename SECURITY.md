# Security policy

ArcGIS Pro MCP Studio is a **development preview**. The supported configuration is an interactive same-user workstation with dockpane approvals. Autonomous mode is an opt-in expert setting and is not recommended.

## Supported versions

| Version | Supported |
| --- | --- |
| 0.3.x (development preview) | Yes, best effort |
| < 0.3 | No |

Fixes land on `main` and ship in the next preview release. There are no backports.

## Reporting a vulnerability

Report vulnerabilities privately through GitHub: open a private security advisory at <https://github.com/nicoazel/ArcGISPro.MCP/security/advisories/new>, or open the repository's **Security** tab and choose **Report a vulnerability**. This needs private vulnerability reporting to be enabled on the repository (Settings > Code security > Private vulnerability reporting); if the link does not offer a report form, it is not enabled yet, so contact the maintainer through their GitHub profile and ask for a private channel without sharing details. Do not open a public issue, pull request, or discussion for a suspected vulnerability.

Include the affected version or commit, the ArcGIS Pro version, whether autonomous mode or ArcPy was enabled, reproduction steps, and the impact you observed. This is a single-maintainer project; expect an acknowledgement within a few days and a best-effort fix. Coordinated disclosure is appreciated.

## Threat model summary

The full model is in [docs/security.md](docs/security.md). In short:

- **Trust boundary: same-user named pipe.** The gateway talks to the add-in over a local named pipe restricted to the signed-in Windows user. Any process running as that user can connect. The bridge does not isolate applications running under the same account from each other.
- **Approvals.** In default mode, destructive and external-side-effect operations, and also `project.open`, `project.save` and `feature.update`, need a short-lived, single-use approval token that only a person in the ArcGIS Pro dockpane can issue. Tokens are bound to the operation version, canonical arguments and workspace revision. The gateway cannot approve its own requests.
- **ArcPy containment.** The ArcPy runner is disabled unless explicitly enabled before ArcGIS Pro starts. When enabled it runs hash-pinned scripts from a configured root with bounded arguments, output and time, and every run needs approval. It is not a sandbox: an approved script has the user's full file, network and ArcGIS authority.
- **`gp.run` runs user code.** The generic geoprocessing operation can run Python toolboxes (`.pyt`), script tools and Python expressions in tools such as Calculate Field, so arbitrary Python is reachable even when ArcPy is disabled. It is approval-gated in default mode; read the tool and arguments before approving.
- **Autonomous mode risk.** `ARCGIS_PRO_MCP_AUTONOMOUS_MODE=true` removes the approval gate for the whole host session. The connected client then effectively holds the user's ArcGIS authority, including arbitrary Python execution. Revision checks, schema validation, audit and operation limits still apply, but they are not a security boundary against a hostile client.

Out of scope: attacks that require prior code execution as the same Windows user, vulnerabilities in ArcGIS Pro itself, and risks created by approving a request without reading it.
