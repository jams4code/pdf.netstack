# PdfNetStack on Azure Functions

A .NET 10 app on the isolated worker model with two functions:

- **HtmlToPdf**: `POST /api/html-to-pdf` with HTML as the body returns the PDF.
- **OptimizeBlob**: a PDF written to the `incoming` container is optimized into the `optimized`
  container under the same name. The blob trigger uses Event Grid.

The library is referenced from this repository (`../../../src`). In your own project, reference
the `PdfNetStack` and `PdfNetStack.Html` packages instead.

Nothing is created in your subscription until you run the deployment commands below.

## Files

| File | What it is |
|---|---|
| `Program.cs` | The worker host |
| `HtmlToPdf.cs` | HTTP trigger: HTML in, PDF out |
| `OptimizeBlob.cs` | Blob trigger: optimize to the temporary directory, upload to `optimized` |
| `host.json` | Five minute timeout, at most two blobs at a time per instance |
| `local.settings.json.example` | Settings for local runs against Azurite; copy to `local.settings.json` |
| `fonts/` | Fonts packaged with the app |

## Run locally

You need the [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
v4 and [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite).

```bash
cp local.settings.json.example local.settings.json
azurite --silent &
func start
curl -H "Content-Type: text/html" --data-binary @invoice.html -o invoice.pdf \
  http://localhost:7071/api/html-to-pdf
```

To run the blob function locally, create the `incoming` and `optimized` containers in Azurite,
upload a PDF to `incoming`, and post the event Event Grid would send:

```bash
curl -H "aeg-event-type: Notification" -H "Content-Type: application/json" \
  "http://localhost:7071/runtime/webhooks/blobs?functionName=Host.Functions.OptimizeBlob" \
  -d '[{"id":"1","eventType":"Microsoft.Storage.BlobCreated","subject":"/blobServices/default/containers/incoming/blobs/report.pdf","eventTime":"2026-01-01T00:00:00Z","dataVersion":"","data":{"api":"PutBlob","url":"http://127.0.0.1:10000/devstoreaccount1/incoming/report.pdf"}}]'
```

## Publish

Publish ready to run, so the first request after a scale out compiles little code:

```bash
dotnet publish -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true -o publish
```

Use `-r win-x64` for a Windows plan. Add fonts to `fonts/` first (see
[fonts/README.md](fonts/README.md)).

## Deploy to Flex Consumption

With the Azure CLI, in a resource group of your choice (all names below are placeholders):

```bash
az storage account create -n <storage> -g <group> -l <region> --sku Standard_LRS
az storage container create --account-name <storage> -n incoming --auth-mode login
az storage container create --account-name <storage> -n optimized --auth-mode login
az functionapp create -n <app> -g <group> --storage-account <storage> \
  --flexconsumption-location <region> --runtime dotnet-isolated --runtime-version 10.0 \
  --instance-memory 2048
az functionapp config appsettings set -n <app> -g <group> \
  --settings "PdfStorage=$(az storage account show-connection-string -n <storage> -g <group> -o tsv)"
(cd publish && zip -r ../app.zip .)
az functionapp deployment source config-zip -n <app> -g <group> --src app.zip
```

A managed identity with the Storage Blob Data Contributor role is better than a connection
string in production: set `PdfStorage__blobServiceUri` and `PdfStorage__credential` instead.

Then subscribe the function to blob creation in `incoming`. The endpoint carries the
`blobs_extension` system key of the app:

```bash
KEY=$(az functionapp keys list -n <app> -g <group> --query "systemKeys.blobs_extension" -o tsv)
az eventgrid system-topic create -n <storage>-events -g <group> -l <region> \
  --topic-type microsoft.storage.storageaccounts \
  --source "$(az storage account show -n <storage> -g <group> --query id -o tsv)"
az eventgrid system-topic event-subscription create -n optimize-pdf -g <group> \
  --system-topic-name <storage>-events --included-event-types Microsoft.Storage.BlobCreated \
  --subject-begins-with /blobServices/default/containers/incoming/ \
  --endpoint "https://<app>.azurewebsites.net/runtime/webhooks/blobs?functionName=Host.Functions.OptimizeBlob&code=$KEY"
```

Call the HTTP function with its function key:
`https://<app>.azurewebsites.net/api/html-to-pdf?code=<key>`.

## Memory, plans and the temporary directory

- **Flex Consumption** runs instances of 512 MB, 2,048 MB (the default) or 4,096 MB. Several
  invocations share an instance: HTTP concurrency per instance is set on the app, and blob
  concurrency by `maxDegreeOfParallelism` in `host.json` (2 here). Size memory for the
  concurrent work, not one request.
- **Consumption** caps an instance at 1.5 GB, shared by its concurrent invocations, and a run at
  10 minutes. Check that the plan and operating system you choose support .NET 10 on the
  isolated worker.
- **Optimize**: the sample sets `Streaming`, so the optimizer reads the file on demand from a
  copy in the temporary directory and writes the rewrite there too. Memory stays flat whatever
  the file size: on a generated 278 MB file it peaked at about 460 MB of working set and 50 MB of
  live managed memory, so two blobs at a time fit 2,048 MB instances. The temporary directory
  holds about three times the file size per concurrent blob. The sample skips inputs over
  `PDF_MAX_INPUT_MB` (an app setting, 100 by default).
- **HTML**: a few hundred MB per concurrent conversion is plenty for invoices and reports.
  `MaxPages` caps the output at 200 pages.
- **Temporary directory**: `Path.GetTempPath()` is the instance's local storage. It is limited and
  differs by plan, so keep `PDF_MAX_INPUT_MB` well below it. The library itself writes nothing
  to disk unless you give it a path.

## Fonts

Function hosts have few fonts installed or none, and what is there can change. The HTML function
loads the fonts in `fonts/` once per worker into a `PdfFontCollection` and sets
`UseSystemFonts = false`, so the output depends only on the HTML and the packaged fonts. Families
that are not packaged fall back to the standard 14 fonts. Nothing is fetched from the network:
put images and style sheets inline.

## Cost

The deployment above creates a storage account with two containers, a Flex Consumption app and an
Event Grid subscription. The app costs nothing while idle (no always ready instances are set);
stored blobs do. Application Insights is not set up; if you add it, ingestion beyond the free
monthly allowance is billed per GB.

Arithmetic with the list prices when this was written (check
[Functions pricing](https://azure.microsoft.com/pricing/details/functions/) for your region):
Flex Consumption on demand $0.000026 per GB-second of instance memory and $0.40 per million
executions, with a monthly free grant of 100,000 GB-seconds and 250,000 executions; Consumption
$0.000016 per GB-second and $0.20 per million, with 400,000 GB-seconds and 1 million executions
free. Blob storage (hot, LRS) is about $0.02 per GB-month, $0.05 per 10,000 writes and $0.004 per
10,000 reads. Event Grid's first 100,000 operations a month are free.

- 10,000 HTML conversions and 10,000 optimizations a month on Flex at 2,048 MB, 0.5 s and 3 s
  each: 10,000 x 0.5 x 2 + 10,000 x 3 x 2 = 70,000 GB-seconds and 20,000 executions, inside the
  free grant. Storage: 20,000 writes = $0.10, 10,000 reads = $0.004, and 90 GB stored for a week
  (5 MB in, 4 MB out each) about 90 x $0.02 x 7 / 30 = $0.42. **About $0.52**, less with a
  lifecycle rule that deletes old blobs.
- 1 million HTML conversions a month on Flex at 2,048 MB and 0.5 s: (1,000,000 - 100,000)
  GB-seconds x $0.000026 = $23.40, plus 750,000 billable executions x $0.40 / 1,000,000 = $0.30.
  **About $23.70.** At 512 MB instances: (250,000 - 100,000) x $0.000026 + $0.30 = **about
  $4.20**.
- The same million on Consumption, at about 0.5 GB measured per execution: 1,000,000 x 0.5 x 0.5
  = 250,000 GB-seconds and 1 million executions, inside the free grant. **$0.00.**

Delete the resource group to remove everything.
