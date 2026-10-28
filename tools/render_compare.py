"""Independent fidelity check: render original and optimized PDFs with PDFium and compare.

Development tool only. It is not part of any package and nothing in src/ depends on it.
Requires: pip install pypdfium2 numpy

Usage:
  python tools/render_compare.py <original-dir> <optimized-dir> [--dpi 150] [--pages 6] [--jobs 8]

For every PDF present in both trees with different bytes, the script compares page count,
page boxes and rotation, extracted text per page, and the rendered RGB pixels of the first
pages plus the last page. Any difference is printed and the exit code is 1.
"""

import argparse
import hashlib
import os
import sys
from multiprocessing import Pool

import numpy as np
import pypdfium2 as pdfium


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def page_selection(count, limit):
    if count <= limit:
        return list(range(count))
    return list(range(limit - 1)) + [count - 1]


def compare(job):
    original, optimized, dpi, limit = job
    try:
        a = pdfium.PdfDocument(original)
        b = pdfium.PdfDocument(optimized)
    except Exception as ex:  # noqa: BLE001 - report every failure
        return original, [f"open failed: {ex}"], 0
    problems = []
    if len(a) != len(b):
        return original, [f"page count {len(a)} vs {len(b)}"], 0
    scale = dpi / 72.0
    pages = page_selection(len(a), limit)
    for i in pages:
        pa, pb = a[i], b[i]
        if pa.get_size() != pb.get_size() or pa.get_rotation() != pb.get_rotation():
            problems.append(f"page {i + 1}: geometry differs")
            continue
        ta = pa.get_textpage().get_text_range()
        tb = pb.get_textpage().get_text_range()
        if ta != tb:
            problems.append(f"page {i + 1}: text differs")
        ia = pa.render(scale=scale, may_draw_forms=True).to_numpy()
        ib = pb.render(scale=scale, may_draw_forms=True).to_numpy()
        if ia.shape != ib.shape:
            problems.append(f"page {i + 1}: bitmap size differs")
        elif not np.array_equal(ia, ib):
            diff = int(np.count_nonzero(np.any(ia != ib, axis=-1)))
            problems.append(f"page {i + 1}: {diff} pixels differ")
    a.close()
    b.close()
    return original, problems, len(pages)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("original")
    parser.add_argument("optimized")
    parser.add_argument("--dpi", type=int, default=150)
    parser.add_argument("--pages", type=int, default=6)
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()

    jobs = []
    for root, _, files in os.walk(args.optimized):
        for name in files:
            if not name.lower().endswith(".pdf"):
                continue
            out = os.path.join(root, name)
            src = os.path.join(args.original, os.path.relpath(out, args.optimized))
            if os.path.exists(src) and os.path.getsize(out) > 0 and sha(src) != sha(out):
                jobs.append((src, out, args.dpi, args.pages))

    failures = 0
    pages = 0
    with Pool(args.jobs) as pool:
        for original, problems, compared in pool.imap_unordered(compare, jobs, chunksize=4):
            pages += compared
            if problems:
                failures += 1
                print(f"DIFF {original}")
                for p in problems[:5]:
                    print(f"     {p}")
    print(f"documents compared: {len(jobs)}, pages rendered: {pages}, documents with differences: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
