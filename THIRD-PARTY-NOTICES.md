# Third party notices

## PdfNetStack and PdfNetStack.Tool

On .NET 8 and later: no third party runtime dependencies, only the .NET base class library.

On .NET Standard 2.0 (.NET Framework, Mono, Unity), PdfNetStack uses Microsoft's span polyfill:
`System.Memory`, `System.Buffers`, `System.Numerics.Vectors` and
`System.Runtime.CompilerServices.Unsafe`, all MIT. Every .NET Standard 2.0 library also carries the
implicit `NETStandard.Library` (MIT) and its `Microsoft.NETCore.Platforms` 1.1.0, which holds only
platform metadata (`runtime.json`), no code, under the Microsoft .NET Library license.

## PdfNetStack.Azure

Depends on the Azure SDK for .NET and its transitive packages, all MIT licensed:

| Package | License |
|---|---|
| Azure.Core | MIT |
| Azure.Identity | MIT |
| Azure.Storage.Blobs (pinned 12.29.2) | MIT |
| Azure.Storage.Common | MIT |
| Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal | MIT |
| Microsoft.IdentityModel.Abstractions | MIT |
| Microsoft.Bcl.AsyncInterfaces, Microsoft.Extensions.* abstractions | MIT |
| System.ClientModel, System.Memory.Data, System.IO.Hashing, System.IO.Pipelines | MIT |
| System.Diagnostics.DiagnosticSource, System.Text.Json, System.Text.Encodings.Web | MIT |
| System.Security.Cryptography.ProtectedData | MIT |

`Azure.Storage.Blobs` 12.30 and later depend on `Apache.Arrow` (Apache-2.0), which is why the
version is pinned. `eng/check-licenses.ps1` enforces this list in CI.

## Development only

Not shipped in any package: MSTest (MIT) for tests, and `pypdfium2` (Apache-2.0 and BSD-3-Clause)
plus NumPy (BSD-3-Clause) for the optional render comparison script in `tools/`. The JPEG test
fixtures are synthetic images written by Pillow (MIT-CMU) with libjpeg-turbo (IJG, BSD-3-Clause
and zlib licenses) through `tools/make_jpeg_fixtures.py`; no code from either is used.
