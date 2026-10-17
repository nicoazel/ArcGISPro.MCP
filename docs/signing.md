# Self-signed release installation

The self-signed 0.3.1 release signs both `ArcGISProMCP.AddIn.esriAddinX` and `server/arcgis-pro-mcp.exe`. This is a development preview, not a publicly trusted certificate or Esri certification. The ZIP keeps the original `ArcGISProMCP-0.3.1-win-x64-development-preview` folder so existing client paths still work.

## Verify before trusting

1. Verify the downloaded ZIP against its release `SHA256SUMS` and extract it.
2. Open `publisher-self-signed.cer` and inspect its details. The subject is `CN=Nico Azel - ArcGIS Pro MCP Development`.
3. Compare the certificate fingerprint with the publisher through an independently trusted channel. A certificate bundled with a download does not by itself establish identity.

Certificate SHA-1 thumbprint (identifier, not the file-signing hash):

```text
4F82B3AC72D5A9F55102CA2A7D101B77E0AC51E9
```

Signatures use SHA-256. The certificate expires October 17, 2027; these signatures are not timestamped. A renewed certificate will require another trust decision.

## Trust and install

On a computer you control, open the certificate, choose **Install Certificate > Current User**, and select **Trusted Root Certification Authorities**. If publisher policy also requires it, repeat for **Trusted Publishers**. Only do this after deciding to trust this publisher: it trusts other code signed with the same key as well. For managed computers, ask IT to deploy the certificate under organizational policy.

Check the gateway executable's **Properties > Digital Signatures** and the add-in's signature using `ArcGISSignAddIn.exe` in the ArcGIS Pro installation's `bin` folder. Keep signed/trusted add-in restrictions enabled. Continue with the [installation guide](deployment.md#install-users), using a disposable project and testing both approve and deny.

To revoke this trust, use `certmgr.msc` to remove only the certificate with the exact thumbprint above from your Current User Trusted Root and Trusted Publishers stores.

## What was verified

The upstream 0.3.1 ZIP matched its published checksum before signing. The add-in signature passed Windows OPC cryptographic verification. The executable carries the expected signer; Windows reports an untrusted chain until explicit trust is installed. No fresh live ArcGIS Pro installation test of this signed package has been completed. Existing live acceptance evidence covers the earlier build it names.

## Maintainer release process

The normal package script produces an unsigned intermediate ZIP; it cannot access this signing identity in CI. Sign the add-in with `ArcGISSignAddIn.exe` and the gateway with Authenticode, verify signatures, copy the public certificate and these instructions into the bundle, update `release.json`, then regenerate inner and outer checksums. Never include a private key. The development key is non-exportable in the maintainer's Windows Current User personal certificate store.

The self-signed release reuses the published 0.3.1 binaries. Its separate release tag records the updated documentation; it is not a claim that the binaries were rebuilt from that documentation commit.
