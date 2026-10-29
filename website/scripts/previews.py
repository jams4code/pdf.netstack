"""Renders page 1 of selected sample outputs to WebP for the guides, with PDFium.

Development tool only: pypdfium2 and Pillow run at build time and nothing of them ships.
Run after scripts/run-samples.mjs. A sample asks for a preview with a line

    // preview: <name> <output.pdf> [crop]

where crop (0 to 1, default 1) keeps the top part of the page. Pages show the image as
/previews/<name>.webp. Output: public/previews/<name>.webp, 640 pixels wide. Images no sample
asks for any more are deleted.

Usage: python scripts/previews.py [work-dir]   (default: samples/work)
"""

import re
import sys
from pathlib import Path

import pypdfium2 as pdfium

WIDTH = 640
PREVIEW = re.compile(r"^// preview: ([\w-]+) (\S+\.pdf)(?: ([\d.]+))?\s*$", re.MULTILINE)


def requested(samples):
    """Yields (name, sample folder, pdf file, crop) for every preview line."""
    seen = set()
    for sample in sorted(samples.glob("*.cs")):
        for match in PREVIEW.finditer(sample.read_text(encoding="utf-8")):
            name, pdf, crop = match.group(1), match.group(2), float(match.group(3) or 1)
            if name in seen:
                raise SystemExit(f"{sample.name}: preview name {name} is used twice")
            if not 0 < crop <= 1:
                raise SystemExit(f"{sample.name}: crop must be between 0 and 1")
            seen.add(name)
            yield name, sample.stem, pdf, crop


def main():
    website = Path(__file__).resolve().parent.parent
    samples = website / "samples"
    work = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else samples / "work"
    out = website / "public" / "previews"
    out.mkdir(parents=True, exist_ok=True)
    missing = 0
    written = set()
    for name, folder, file, crop in requested(samples):
        pdf_path = work / folder / file
        if not pdf_path.exists():
            print(f"missing {pdf_path}: run scripts/run-samples.mjs first", file=sys.stderr)
            missing += 1
            continue
        pdf = pdfium.PdfDocument(str(pdf_path))
        # Form fields and signature boxes draw from their widget appearances.
        pdf.init_forms()
        page = pdf[0]
        scale = WIDTH / page.get_width()
        image = page.render(scale=scale, may_draw_forms=True).to_pil().convert("RGB")
        image = image.crop((0, 0, image.width, round(image.height * crop)))
        image.save(out / f"{name}.webp", "WEBP", quality=82, method=6)
        written.add(f"{name}.webp")
        print(f"{name}.webp {image.width}x{image.height}")
        page.close()
        pdf.close()
    if not missing:
        for stale in out.glob("*.webp"):
            if stale.name not in written:
                stale.unlink()
                print(f"removed {stale.name}")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
