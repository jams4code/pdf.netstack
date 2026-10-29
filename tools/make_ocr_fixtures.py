"""Regenerates the OCR test page: Latin and Arabic text set by PdfNetStack.Html, then rasterized.

The page is rendered to PDF by the HTML engine in this repository (a throwaway console project in
a temporary folder), then rasterized at 200 dpi in gray with pdftoppm, as a scanner would see it.
The integration tests read it back with the PP-OCRv5 models and expect the text below.

    python tools/make_ocr_fixtures.py        (needs the .NET SDK and pdftoppm on PATH)
"""

import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tests" / "PdfNetStack.Tests" / "Fixtures" / "Ocr"

HTML = """<!DOCTYPE html><html><head><style>
@page { size: A5; margin: 1.5cm }
body { font: 13pt Georgia, serif; color: #111 }
h1 { font: bold 20pt sans-serif }
.ar { direction: rtl; font: 16pt sans-serif; text-align: right }
</style></head><body>
<h1>Invoice 2026-114</h1>
<p>Total amount due: 1,250.00 EUR by 30 November 2026.</p>
<p>The quick brown fox jumps over the lazy dog.</p>
<p class="ar">مرحبا بالعالم، هذا نص عربي</p>
</body></html>
"""

PROJECT = f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><ProjectReference Include="{ROOT / 'src' / 'PdfNetStack.Html' / 'PdfNetStack.Html.csproj'}" /></ItemGroup>
</Project>
"""

PROGRAM = """File.WriteAllBytes(args[1], PdfNetStack.Html.HtmlConverter.ConvertToPdf(File.ReadAllText(args[0])));
"""


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        folder = Path(work)
        (folder / "render.csproj").write_text(PROJECT)
        (folder / "Program.cs").write_text(PROGRAM)
        (folder / "page.html").write_text(HTML, encoding="utf-8")
        subprocess.run(["dotnet", "run", "-c", "Release", "--", "page.html", "page.pdf"], cwd=folder, check=True)
        subprocess.run(["pdftoppm", "-r", "200", "-gray", "-png", "-singlefile", "page.pdf", "page"], cwd=folder, check=True)
        shutil.copy(folder / "page.png", OUT / "page.png")


if __name__ == "__main__":
    main()
