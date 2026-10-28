# PdfNetStack.Tool

`pdfnetstack`: a cross-platform command line tool to compress, optimize, merge, split, rotate,
encrypt and decrypt PDF files. Lossless by default, every output verified against its source.
Built on the [PdfNetStack](https://www.nuget.org/packages/PdfNetStack) library by Jamal
Abdelkhalek at [JADEV](https://github.com/jams4code). MIT licensed, no Ghostscript.

```bash
dotnet tool install -g PdfNetStack.Tool
```

## Usage

```bash
pdfnetstack optimize in.pdf out.pdf                      # lossless compression
pdfnetstack optimize in.pdf out.pdf --jpeg-quality 60    # smaller, lossy on JPEG images only
pdfnetstack batch ./incoming ./optimized --report report.csv
pdfnetstack batch ./corpus                               # measure only, writes nothing
pdfnetstack inspect in.pdf                               # where the bytes go
pdfnetstack verify original.pdf other.pdf                # check any tool's output

pdfnetstack merge out.pdf a.pdf b.pdf c.pdf
pdfnetstack split in.pdf ./pages
pdfnetstack extract in.pdf out.pdf 1-3,7,9-
pdfnetstack rotate in.pdf out.pdf 90 2-4
pdfnetstack meta in.pdf out.pdf --title "Annual report" --author "Finance"

pdfnetstack encrypt in.pdf out.pdf --user-password u --owner-password o --allow print,copy
pdfnetstack decrypt in.pdf out.pdf --password o
```

Run `pdfnetstack --help` for every option. Requires the .NET 8 or later runtime.

## Links

- [Documentation and source](https://github.com/jams4code/pdf.netstack)
- [Changelog](https://github.com/jams4code/pdf.netstack/blob/main/CHANGELOG.md)
- [Report an issue](https://github.com/jams4code/pdf.netstack/issues)
