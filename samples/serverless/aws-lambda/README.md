# PdfNetStack on AWS Lambda

A Native AOT function on the `provided.al2023` runtime (arm64) with two entry points:

- **optimize**: a PDF uploaded under `incoming/` in the sample's bucket is optimized and written
  under `optimized/` with the same name. The function is triggered by the S3 event.
- **html**: HTML posted to the function URL comes back as a PDF.

The library is referenced from this repository (`../../../src`). In your own project, reference
the `PdfNetStack` and `PdfNetStack.Html` packages instead.

Nothing is created in your account until you run `sam deploy`.

## Files

| File | What it is |
|---|---|
| `Program.cs` | Starts the runtime client with the handler `PDF_FUNCTION` names |
| `OptimizeHandler.cs` | S3 event: download, optimize to `/tmp`, upload |
| `HtmlHandler.cs` | Function URL: HTML in, PDF out (base64, as Lambda requires for binary bodies) |
| `template.yaml` | SAM template: bucket, both functions, log groups with 14 day retention |
| `build.Dockerfile` | Amazon Linux 2023 with clang and the .NET SDK, to publish the executable |
| `fonts/` | Fonts packaged with the function |

## Build

Native AOT compiles for one operating system and architecture, and the executable links against
the C library of the machine that builds it. Build on Amazon Linux 2023 for arm64, as the
function runs. On an arm64 machine (Apple silicon, Graviton, Windows on Arm) this is native; on
x64, Docker emulates arm64 and the build is slower.

Pick a .NET 10 SDK version and copy its SHA-512 from the
[.NET download page](https://dotnet.microsoft.com/download/dotnet/10.0) (Linux, Arm64, tar.gz):

```bash
docker build --platform linux/arm64 -f build.Dockerfile \
  --build-arg DOTNET_SDK_VERSION=<version> --build-arg DOTNET_SDK_SHA512=<sha512> \
  -t pdfnetstack-lambda-build .

# From this folder: mount the whole repository, the sample references ../../../src.
docker run --rm --platform linux/arm64 -v "$(pwd)/../../..:/repo" \
  -w /repo/samples/serverless/aws-lambda pdfnetstack-lambda-build \
  dotnet publish -c Release -r linux-arm64 -o publish
```

`publish/bootstrap` is the function. Add fonts to `fonts/` before publishing (see
[fonts/README.md](fonts/README.md)).

## Deploy

With the [AWS SAM CLI](https://docs.aws.amazon.com/serverless-application-model/) and
credentials for the target account:

```bash
sam deploy --guided --stack-name pdfnetstack-sample --capabilities CAPABILITY_IAM
```

There is no `sam build` step: the template points at `publish/`. Use a lower case stack name, as
it becomes part of the bucket name.

## Try it

```bash
BUCKET=$(aws cloudformation describe-stacks --stack-name pdfnetstack-sample \
  --query "Stacks[0].Outputs[?OutputKey=='BucketName'].OutputValue" --output text)
aws s3 cp report.pdf "s3://$BUCKET/incoming/report.pdf"
# a few seconds later:
aws s3 cp "s3://$BUCKET/optimized/report.pdf" report-optimized.pdf

URL=$(aws cloudformation describe-stacks --stack-name pdfnetstack-sample \
  --query "Stacks[0].Outputs[?OutputKey=='HtmlFunctionUrl'].OutputValue" --output text)
curl --aws-sigv4 "aws:amz:<region>:lambda" --user "$AWS_ACCESS_KEY_ID:$AWS_SECRET_ACCESS_KEY" \
  -H "x-amz-security-token: $AWS_SESSION_TOKEN" -H "Content-Type: text/html" \
  --data-binary @invoice.html -o invoice.pdf "$URL"
```

Leave out the `x-amz-security-token` header when your credentials have no session token.

## Memory and /tmp

- **CPU follows memory.** Lambda gives one vCPU at 1,769 MB and more above that. The optimizer
  recompresses streams in parallel on every core it gets (`MaxDegreeOfParallelism`, the processor
  count by default), so more memory also means faster runs.
- **Optimize**: the sample sets `Streaming`, so the optimizer reads the file on demand from a
  copy in `/tmp` and writes the rewrite there too. Memory stays flat whatever the file size: on
  a generated 278 MB file it peaked at about 460 MB of working set and 50 MB of live managed
  memory. The default 2,048 MB is for the CPU it brings, not for the file. Uploads larger than
  `MaxInputMB` (600) are skipped and logged. Lowering `MaxDegreeOfParallelism` lowers the peak,
  at the cost of time.
- **HTML**: 1,024 MB is plenty for invoices and reports; the extra CPU over 512 MB shortens
  conversions. `MaxPages` caps the output at 200 pages.
- **/tmp** is the only writable directory. While a file is optimized it holds the input copy,
  the rewrite and the output, about three times the file size (`EphemeralStorage` is 2,048 MB in
  the template, which suits the 600 MB limit; up to 10,240 MB is possible). Each is deleted when
  it is closed. The library itself writes nothing to disk unless you give it a path.
- **Responses**: a synchronous Lambda response is at most 6 MB, so the HTML function refuses PDFs
  over 4 MB (base64 adds a third). For larger documents, write the PDF to S3 and return a link.

## Fonts

Lambda has no system fonts. The HTML function loads the fonts in `fonts/` once per instance into
a `PdfFontCollection` and sets `UseSystemFonts = false`, so the output depends only on the HTML
and the packaged fonts. Families the HTML names that are not packaged fall back to the standard
14 fonts. Nothing is fetched from the network: put images and style sheets inline.

## Cold start

The function is compiled ahead of time and uses invariant globalization, so the first request
of a new instance does no JIT compilation. On a development machine, the first HTML conversion in
a fresh Native AOT process took about 16 ms against about 770 ms under the JIT, and the first
text extraction about 3 ms against about 140 ms. Lambda's own initialization (downloading the
code, starting the process) comes on top; check `Init Duration` in the logs.

## Cost

The sample creates one S3 bucket (objects expire after 7 days), two functions and two log groups
(14 day retention). Nothing runs or costs anything while idle except stored objects and logs.

Arithmetic with the us-east-1 list prices when this was written (check
[Lambda pricing](https://aws.amazon.com/lambda/pricing/) and [S3 pricing](https://aws.amazon.com/s3/pricing/)
for your Region): arm64 compute $0.0000133334 per GB-second, $0.20 per million requests, S3
Standard $0.023 per GB-month, $0.005 per 1,000 PUT and $0.0004 per 1,000 GET requests. Lambda's
free tier includes 1 million requests and 400,000 GB-seconds a month.

- 10,000 HTML conversions a month at 1 GB and 0.3 s each: 10,000 x 0.3 x 1 = 3,000 GB-seconds,
  inside the free tier. **$0.00.**
- 10,000 optimizations a month of 5 MB files at 2 GB and 3 s each: 10,000 x 3 x 2 = 60,000
  GB-seconds, inside the free tier. S3: 20,000 PUT (uploads and outputs) x $0.005 / 1,000 =
  $0.10, 10,000 GET x $0.0004 / 1,000 = $0.004, and about 10,000 x 9 MB = 90 GB at most stored
  for a week, about 90 x $0.023 x 7 / 30 = $0.48. **About $0.58.**
- Past the free tier, 2 million HTML conversions at 1 GB and 0.3 s: (600,000 - 400,000)
  GB-seconds x $0.0000133334 = $2.67, plus 1 million billable requests x $0.20 = $0.20.
  **About $2.87.**

The extra `/tmp` storage of the optimize function (1.5 GB above the free 512 MB) costs
$0.0000000309 per GB-second: 10,000 x 3 x 1.5 x $0.0000000309 = $0.0014. CloudWatch Logs ingestion
($0.50 per GB) stays in cents at these volumes. Run `sam delete` to remove everything; empty the
bucket first.
