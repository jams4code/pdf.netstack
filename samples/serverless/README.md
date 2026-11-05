# Serverless samples

Two small apps that run PdfNetStack in serverless hosts:

- [aws-lambda](aws-lambda/README.md): a Native AOT function on `provided.al2023` (arm64) that
  optimizes PDFs uploaded to S3 and converts HTML to PDF behind a function URL, with a SAM
  template.
- [azure-functions](azure-functions/README.md): a .NET isolated worker app with an HTTP trigger
  that converts HTML to PDF and a blob trigger that optimizes PDFs.

They reference the library from this repository by project reference, build with the rest of the
repository's analyzers, and are compiled in CI (`dotnet build samples/serverless/Serverless.sln`).
They are samples, not packages: nothing here ships, and the repository's license check, which
covers the shipping packages, does not cover them. Each README estimates what running it costs;
nothing is created in a cloud account until you deploy it yourself.

## Packages and licenses

The library packages themselves have no dependencies on .NET 8 and later. The samples add the
host SDKs:

| Sample | Package | License |
|---|---|---|
| aws-lambda | Amazon.Lambda.RuntimeSupport 2.3.0 | Apache-2.0 |
| aws-lambda | Amazon.Lambda.Core 3.4.0 | Apache-2.0 |
| aws-lambda | Amazon.Lambda.Serialization.SystemTextJson 3.0.1 | Apache-2.0 |
| aws-lambda | Amazon.Lambda.APIGatewayEvents 3.0.1 | Apache-2.0 |
| aws-lambda | Amazon.Lambda.S3Events 4.0.0 | Apache-2.0 |
| aws-lambda | AWSSDK.S3 4.0.104.8, AWSSDK.Core (transitive) | Apache-2.0 |
| aws-lambda | SnapshotRestore.Registry (transitive) | Apache-2.0 |
| azure-functions | Microsoft.Azure.Functions.Worker 2.52.0 | MIT |
| azure-functions | Microsoft.Azure.Functions.Worker.Sdk 2.1.0 | MIT |
| azure-functions | Microsoft.Azure.Functions.Worker.Extensions.Http 3.3.0 | MIT |
| azure-functions | Microsoft.Azure.Functions.Worker.Extensions.Storage.Blobs 6.8.2 | MIT |
| azure-functions | Azure.Core, Azure.Identity, Azure.Storage.Blobs, Microsoft.Extensions.*, Microsoft.Identity.Client, System.* (transitive) | MIT |
| azure-functions | Grpc.Net.Client, Grpc.Core.Api and the other Grpc.* packages (transitive) | Apache-2.0 |
| azure-functions | Google.Protobuf (transitive) | BSD-3-Clause |

`dotnet list <project> package --include-transitive` prints the full list.

## What the samples show

- **No system fonts.** Serverless hosts have few fonts or none. Both HTML functions load the
  fonts packaged in their `fonts/` folder once, into a static `PdfFontCollection`, and set
  `UseSystemFonts = false`.
- **Shared state across invocations.** Options, fonts and SDK clients are created once per
  instance and shared by every invocation, concurrent ones included. PdfNetStack's own caches are
  thread safe and bounded.
- **Writable storage.** The library writes only to the paths and streams you give it. The
  optimize functions write their output to the temporary directory (`/tmp` on Lambda) and upload
  it from there.
- **Limits.** Input size, page count and response size are checked before the work starts.
- **Cold start.** Native AOT with invariant globalization on Lambda, ready to run on Functions.
