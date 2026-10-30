"""Regenerates the OCR test page: Latin and Arabic text set by PdfNetStack.Html, then rasterized.

The page is rendered to PDF by the HTML engine in this repository (a throwaway console project in
a temporary folder), then rasterized at 200 dpi in gray with pdftoppm, as a scanner would see it.
The integration tests read it back with the PP-OCRv5 models and expect the text below.

The page is then cut into pieces, as some scanners store a page: horizontal strips of uneven
height (strips/strip-NN.png, top to bottom) and a grid of tiles (tiles/tile-R-C.png, row and
column). The tests place the pieces back on a page and expect the same pixels and text.

    python tools/make_ocr_fixtures.py            (needs the .NET SDK, pdftoppm and Pillow)
    python tools/make_ocr_fixtures.py --pieces   (only cuts the pieces from the existing page.png)
"""

import shutil
import subprocess
import sys
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


# Strip heights in rows, the last strip taking what is left. Uneven, as scanners differ, and placed
# so each boundary cuts through a line of text (the lines sit at rows 166-206, 262-329, 380-414
# and 468-511 of page.png).
STRIPS = [190, 90, 120, 90]

# Tile edges: across through the heading and the third line, down the middle of the page.
TILE_ROW_EDGES = [195, 400]
TILE_COLUMN_EDGES = [583]


def cut() -> None:
    """Cuts page.png into strips and tiles, gray PNGs like the page itself."""
    from PIL import Image

    page = Image.open(OUT / "page.png").convert("L")
    width, height = page.size
    for folder in ("strips", "tiles"):
        target = OUT / folder
        target.mkdir(exist_ok=True)
        for old in target.glob("*.png"):
            old.unlink()

    top = 0
    for index, rows in enumerate(STRIPS + [height - sum(STRIPS)]):
        page.crop((0, top, width, top + rows)).save(OUT / "strips" / f"strip-{index:02d}.png", optimize=True)
        top += rows

    xs = [0] + TILE_COLUMN_EDGES + [width]
    ys = [0] + TILE_ROW_EDGES + [height]
    for r in range(len(ys) - 1):
        for c in range(len(xs) - 1):
            page.crop((xs[c], ys[r], xs[c + 1], ys[r + 1])).save(OUT / "tiles" / f"tile-{r}-{c}.png", optimize=True)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    if "--pieces" in sys.argv[1:]:
        cut()
        return

    with tempfile.TemporaryDirectory() as work:
        folder = Path(work)
        (folder / "render.csproj").write_text(PROJECT)
        (folder / "Program.cs").write_text(PROGRAM)
        (folder / "page.html").write_text(HTML, encoding="utf-8")
        subprocess.run(["dotnet", "run", "-c", "Release", "--", "page.html", "page.pdf"], cwd=folder, check=True)
        subprocess.run(["pdftoppm", "-r", "200", "-gray", "-png", "-singlefile", "page.pdf", "page"], cwd=folder, check=True)
        shutil.copy(folder / "page.png", OUT / "page.png")
    cut()


if __name__ == "__main__":
    main()
