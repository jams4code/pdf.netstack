"""Compares PdfNetStack with other open source PDF optimizers on the public corpus.

Development tool only. Nothing in src/ depends on it.
Requires: pip install pypdfium2 numpy, the .NET 10 SDK, and whichever of qpdf, Ghostscript (gs)
and MuPDF (mutool) are on the PATH. A tool that is not installed is skipped and the report says so.

Usage:
  python tools/fetch_corpus.py --cache artifacts/corpus/files
  dotnet build src/PdfNetStack.Cli -c Release
  python tools/compare_optimizers.py [--cache artifacts/corpus/files] [--out artifacts/compare]
                                     [--only id,id] [--tools name,name] [--dpi 100] [--pages 6]
                                     [--jobs 4] [--timeout 900]

Bytes alone do not rank optimizers: a tool that downsamples every image or drops the tags, forms
and signatures always wins on size. So every output is scored three ways against its original:
  - size, as the tool wrote it (a tool that makes a file larger is charged for it);
  - rendering: PDFium renders the first pages and the last page of both, and the lowest SSIM on
    luma is kept, with page count, page geometry and PDFium's text compared too;
  - structure: tagging, form fields, outlines, attachments, signatures and annotations, counted by
    PDFium before and after. Anything the original had and the output lost is listed. A signed
    original whose output is not its own bytes with more appended has lost its signatures too:
    the signed byte ranges no longer hold what was signed, so they fail to verify.
An output "keeps quality" when it opens, has the same pages, the same text, nothing lost and a
lowest SSIM of at least --min-ssim (0.95, the gate the Balanced preset uses). The head-to-head
table then compares two tools only on the files where both keep quality, so a tool cannot win the
total by wrecking one large file.

SSIM is strict with scan grain and dithering: a scan reduced to 150 dpi can score 0.8 on a page
that looks the same, because the grain itself changed. Read "Outputs below the bar" with the pages
open before calling one a defect.

qpdf, MuPDF and Ghostscript merge incremental updates into one revision without being asked;
PdfNetStack keeps such a file unless told otherwise, so it runs here with --allow-incremental to
do the same work. It still keeps signed files as they are, which this scoring credits.

Each output is deleted once measured, so the run needs little disk.
"""

import argparse
import datetime
import json
import math
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import time
from multiprocessing import Pool

import fetch_corpus

ROOT = fetch_corpus.ROOT
DEFAULT_TOOL = os.path.join(ROOT, "src", "PdfNetStack.Cli", "bin", "Release", "net10.0", "PdfNetStack.Tool.dll")
DEFAULT_OUT = os.path.join(ROOT, "artifacts", "compare")

# name, family, lossy, command. {in} and {out} are replaced by paths; the first word is looked up
# on the PATH, except "pdfnetstack", which runs the command line tool built from this repository.
TOOLS = [
    ("pdfnetstack", "PdfNetStack", False, ["pdfnetstack", "optimize", "{in}", "{out}", "--min-saving", "0", "--allow-incremental"]),
    ("pdfnetstack-balanced", "PdfNetStack", True, ["pdfnetstack", "optimize", "{in}", "{out}", "--min-saving", "0", "--allow-incremental",
                                                   "--preset", "balanced"]),
    ("pdfnetstack-smallest", "PdfNetStack", True, ["pdfnetstack", "optimize", "{in}", "{out}", "--min-saving", "0", "--allow-incremental",
                                                   "--preset", "smallest"]),
    ("qpdf", "qpdf", False, ["qpdf", "--object-streams=generate", "--compress-streams=y", "--recompress-flate",
                             "--compression-level=9", "--warning-exit-0", "{in}", "{out}"]),
    ("mutool", "MuPDF", False, ["mutool", "clean", "-gggg", "-z", "{in}", "{out}"]),
    ("gs-printer", "Ghostscript", True, ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite",
                                         "-dPDFSETTINGS=/printer", "-sOutputFile={out}", "{in}"]),
    ("gs-ebook", "Ghostscript", True, ["gs", "-q", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=pdfwrite",
                                       "-dPDFSETTINGS=/ebook", "-sOutputFile={out}", "{in}"]),
]

# The pairs worth reading side by side: the same promise (lossless, or lossy at a similar setting).
PAIRS = [
    ("pdfnetstack", "qpdf"),
    ("pdfnetstack", "mutool"),
    ("pdfnetstack-balanced", "gs-printer"),
    ("pdfnetstack-smallest", "gs-ebook"),
]

FEATURES = ["tagged", "form", "outline", "attachments", "signatures", "annotations"]


def resolve(command, tool_dll):
    """The command line to run, or None when the program is not installed."""
    if command[0] == "pdfnetstack":
        return ["dotnet", tool_dll] + command[1:] if os.path.isfile(tool_dll) and shutil.which("dotnet") else None
    found = shutil.which(command[0])
    return [found] + command[1:] if found else None


def version_of(name, tool_dll):
    probes = {"qpdf": ["qpdf", "--version"], "mutool": ["mutool", "-v"], "gs": ["gs", "--version"]}
    if name == "pdfnetstack":
        return "this repository"
    probe = probes.get(name)
    if not probe or not shutil.which(probe[0]):
        return None
    done = subprocess.run(probe, capture_output=True, text=True)
    text = (done.stdout or done.stderr).strip().splitlines()
    return text[0] if text else "unknown"


def luma(image):
    """Rec. 601 luma of a PDFium bitmap (BGR or BGRA byte order)."""
    import numpy as np

    x = image[..., :3].astype(np.float64)
    return 0.114 * x[..., 0] + 0.587 * x[..., 1] + 0.299 * x[..., 2]


def ssim(a, b, window=7):
    """Mean SSIM of two luma planes, with a uniform window as scikit-image uses by default."""
    import numpy as np

    # A page smaller than the window (the corpus has 4 pt pages) is scored as one window.
    window = min(window, a.shape[0], a.shape[1])

    def box(z):
        c = np.pad(np.cumsum(np.cumsum(z, 0), 1), ((1, 0), (1, 0)))
        return (c[window:, window:] - c[:-window, window:] - c[window:, :-window] + c[:-window, :-window]) / (window * window)

    c1, c2 = (0.01 * 255) ** 2, (0.03 * 255) ** 2
    ma, mb = box(a), box(b)
    va = box(a * a) - ma * ma
    vb = box(b * b) - mb * mb
    cov = box(a * b) - ma * mb
    s = ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2))
    return float(s.mean())


def page_selection(count, limit):
    if count <= limit:
        return list(range(count))
    return list(range(limit - 1)) + [count - 1]


def features(doc):
    """What a reader would miss if an optimizer dropped it, as PDFium counts it."""
    import pypdfium2.raw as c

    annotations = 0
    for i in range(len(doc)):
        page = doc[i]
        annotations += c.FPDFPage_GetAnnotCount(page.raw)
        page.close()
    return {
        "tagged": int(bool(c.FPDFCatalog_IsTagged(doc.raw))),
        "form": int(doc.get_formtype() != c.FORMTYPE_NONE),
        "outline": sum(1 for _ in doc.get_toc(max_depth=64)),
        "attachments": c.FPDFDoc_GetAttachmentCount(doc.raw),
        "signatures": c.FPDF_GetSignatureCount(doc.raw),
        "annotations": annotations,
    }


def reference(path, dpi, limit):
    """Renders and counts what is compared in every output of one original."""
    import pypdfium2 as pdfium

    doc = pdfium.PdfDocument(path)
    try:
        if doc.get_formtype() != pdfium.raw.FORMTYPE_NONE:
            doc.init_forms()
        pages = {}
        for i in page_selection(len(doc), limit):
            page = doc[i]
            pages[i] = (page.get_size(), page.get_rotation(), page.get_textpage().get_text_range(),
                        luma(page.render(scale=dpi / 72.0, may_draw_forms=True).to_numpy()))
            page.close()
        return {"count": len(doc), "pages": pages, "features": features(doc)}
    finally:
        doc.close()


def appends_to(original, path):
    """Whether the file at path is the original's bytes, unchanged, with anything after them."""
    with open(original, "rb") as a, open(path, "rb") as b:
        while True:
            x = a.read(1 << 20)
            if not x:
                return True
            if b.read(len(x)) != x:
                return False


def score(ref, path, dpi, original):
    """Compares one output with its original's reference."""
    import pypdfium2 as pdfium

    r = {"problems": [], "min_ssim": None, "lost": []}
    try:
        doc = pdfium.PdfDocument(path)
    except Exception as ex:  # noqa: BLE001 - an output PDFium refuses is a result, not a crash
        r["problems"].append(f"PDFium cannot open it: {ex}")
        return r
    try:
        if doc.get_formtype() != pdfium.raw.FORMTYPE_NONE:
            doc.init_forms()
        if len(doc) != ref["count"]:
            r["problems"].append(f"page count {ref['count']} -> {len(doc)}")
            return r
        values = []
        text_pages = []
        for i, (size, rotation, text, image) in ref["pages"].items():
            page = doc[i]
            if page.get_size() != size or page.get_rotation() != rotation:
                r["problems"].append(f"page {i + 1}: geometry differs")
                page.close()
                continue
            if page.get_textpage().get_text_range() != text:
                text_pages.append(i + 1)
            other = luma(page.render(scale=dpi / 72.0, may_draw_forms=True).to_numpy())
            page.close()
            values.append(ssim(image, other) if image.shape == other.shape else 0.0)
        if text_pages:
            r["problems"].append(f"text differs on page(s) {text_pages[:5]}")
        r["min_ssim"] = min(values) if values else None
        now = features(doc)
        r["lost"] = [f"{k} {ref['features'][k]} -> {now[k]}" for k in FEATURES if now[k] < ref["features"][k]]
        signed = ref["features"]["signatures"]
        if signed and now["signatures"] >= signed and not appends_to(original, path):
            r["lost"].append(f"signatures {signed} no longer verify (signed bytes rewritten)")
        return r
    finally:
        doc.close()


def run_file(job):
    entry, source, tools, dpi, limit, timeout, min_ssim = job
    result = {"id": entry["id"], "bytes": os.path.getsize(source), "tools": {}}
    try:
        ref = reference(source, dpi, limit)
    except Exception as ex:  # noqa: BLE001 - an original PDFium cannot read is skipped and listed
        result["error"] = f"PDFium cannot read the original: {ex}"
        return result
    result["features"] = ref["features"]
    work = tempfile.mkdtemp(prefix="compare-")
    try:
        for name, command in tools:
            out = os.path.join(work, name + ".pdf")
            args = [a.replace("{in}", source).replace("{out}", out) for a in command]
            t = {"seconds": None, "bytes": None}
            started = time.perf_counter()
            try:
                done = subprocess.run(args, capture_output=True, timeout=timeout)
                t["seconds"] = round(time.perf_counter() - started, 3)
                failed = done.returncode != 0 or not os.path.isfile(out) or os.path.getsize(out) == 0
                if failed:
                    tail = done.stderr.decode("utf-8", "replace").strip().splitlines()[-2:]
                    t["error"] = f"exit {done.returncode}: " + " | ".join(tail)
            except subprocess.TimeoutExpired:
                t["error"] = f"timed out after {timeout} s"
            if "error" not in t:
                t["bytes"] = os.path.getsize(out)
                t.update(score(ref, out, dpi, source))
                t["keeps_quality"] = (not t["problems"] and not t["lost"] and t["min_ssim"] is not None
                                      and t["min_ssim"] >= min_ssim)
            else:
                t["keeps_quality"] = False
            if os.path.exists(out):
                os.remove(out)
            result["tools"][name] = t
    finally:
        shutil.rmtree(work, ignore_errors=True)
    return result


def pct(saved, total):
    return f"{100.0 * saved / total:.1f}%" if total else "n/a"


def mib(n):
    return f"{n / (1024 * 1024):.1f} MiB" if n >= 1024 * 1024 else f"{n / 1024:.0f} KiB"


def write_report(results, available, versions, args, out_dir):
    names = [n for n, *_ in TOOLS if n in available]
    lossy = {n: l for n, _, l, _ in TOOLS}
    files = [r for r in results if "error" not in r]
    lines = [
        "# Optimizer comparison",
        "",
        f"{len(files)} files from the public corpus (eng/corpus/manifest.json), "
        f"{datetime.date.today().isoformat()}, {platform.system()} {platform.machine()}.",
        f"Rendering: PDFium at {args.dpi} dpi, up to {args.pages} pages per file. "
        f"An output keeps quality when it has the same pages and text, loses nothing below, "
        f"and its lowest page SSIM is at least {args.min_ssim}.",
        "",
        "| Tool | Kind | Version | Total out | Saved | Keeps quality | Lost a feature | Failed | Time |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    total_in = sum(r["bytes"] for r in files)
    for n in names:
        rows = [r["tools"][n] for r in files]
        out = sum(t["bytes"] if t["bytes"] is not None else r["bytes"] for r, t in zip(files, rows))
        keeps = sum(1 for t in rows if t["keeps_quality"])
        lost = sum(1 for t in rows if t.get("lost"))
        failed = sum(1 for t in rows if "error" in t)
        seconds = sum(t["seconds"] or 0 for t in rows)
        lines.append(f"| {n} | {'lossy' if lossy[n] else 'lossless'} | {versions.get(n) or ''} | {mib(out)} | "
                     f"{pct(total_in - out, total_in)} | {keeps}/{len(files)} | {lost} | {failed} | {seconds:.0f} s |")
    lines += ["", f"Input: {mib(total_in)}. A failed output is counted at its original size.", ""]

    lines += ["## Head to head at equal quality", "",
              "Only files where both outputs keep quality count, so neither side wins by damaging a file.", "",
              "| PdfNetStack | Other | Files | PdfNetStack total | Other total | Smaller on | Larger on |",
              "|---|---|---:|---:|---:|---:|---:|"]
    for a, b in PAIRS:
        if a not in available or b not in available:
            continue
        both = [(r["tools"][a], r["tools"][b]) for r in files
                if r["tools"][a]["keeps_quality"] and r["tools"][b]["keeps_quality"]]
        ta, tb = sum(x["bytes"] for x, _ in both), sum(y["bytes"] for _, y in both)
        smaller = sum(1 for x, y in both if x["bytes"] < y["bytes"])
        larger = sum(1 for x, y in both if x["bytes"] > y["bytes"])
        lines.append(f"| {a} | {b} | {len(both)} | {mib(ta)} | {mib(tb)} | {smaller} | {larger} |")

    lost_lines = []
    for r in files:
        for n in names:
            t = r["tools"][n]
            if t.get("lost"):
                lost_lines.append(f"- {r['id']}, {n}: " + ", ".join(t["lost"]))
    if lost_lines:
        lines += ["", "## Features lost", ""] + lost_lines

    problem_lines = []
    for r in results:
        if "error" in r:
            problem_lines.append(f"- {r['id']}: {r['error']}")
            continue
        for n in names:
            t = r["tools"][n]
            if "error" in t:
                problem_lines.append(f"- {r['id']}, {n}: {t['error']}")
            elif t["problems"]:
                problem_lines.append(f"- {r['id']}, {n}: " + "; ".join(t["problems"]))
            elif t["min_ssim"] is not None and t["min_ssim"] < args.min_ssim:
                problem_lines.append(f"- {r['id']}, {n}: lowest SSIM {t['min_ssim']:.4f}")
    if problem_lines:
        lines += ["", "## Outputs below the bar", ""] + problem_lines

    missing = [n for n, *_ in TOOLS if n not in available]
    if missing:
        lines += ["", "Not installed, so not compared: " + ", ".join(missing) + "."]

    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "compare.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(out_dir, "compare.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"date": datetime.date.today().isoformat(), "dpi": args.dpi, "pages": args.pages,
                   "min_ssim": args.min_ssim, "versions": versions, "files": results}, f, indent=1)
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--cache", default=os.path.join(ROOT, "artifacts", "corpus", "files"))
    p.add_argument("--manifest", default=os.path.join(ROOT, "eng", "corpus", "manifest.json"))
    p.add_argument("--out", default=DEFAULT_OUT)
    p.add_argument("--tool", default=DEFAULT_TOOL, help="PdfNetStack.Tool.dll to run")
    p.add_argument("--only", help="comma-separated corpus ids")
    p.add_argument("--tools", help="comma-separated tool names, default all installed")
    p.add_argument("--dpi", type=int, default=100)
    p.add_argument("--pages", type=int, default=6)
    p.add_argument("--min-ssim", type=float, default=0.95)
    p.add_argument("--jobs", type=int, default=4)
    p.add_argument("--timeout", type=int, default=900)
    args = p.parse_args()

    manifest = fetch_corpus.load_manifest(args.manifest)
    entries = manifest["files"]
    if args.only:
        wanted = set(args.only.split(","))
        entries = [e for e in entries if e["id"] in wanted]
    wanted_tools = set(args.tools.split(",")) if args.tools else None

    tools, versions = [], {}
    for name, _, _, command in TOOLS:
        if wanted_tools and name not in wanted_tools:
            continue
        resolved = resolve(command, args.tool)
        if resolved:
            tools.append((name, resolved))
            versions[name] = version_of(command[0], args.tool)
    if not tools:
        sys.exit("No optimizer found. Build src/PdfNetStack.Cli or install qpdf, gs or mutool.")

    jobs, missing = [], []
    for e in entries:
        path = fetch_corpus.cached_path(args.cache, e)
        if os.path.isfile(path):
            jobs.append((e, path, tools, args.dpi, args.pages, args.timeout, args.min_ssim))
        else:
            missing.append(e["id"])
    if missing:
        print(f"{len(missing)} corpus file(s) not in the cache, skipped: {', '.join(missing[:10])}", file=sys.stderr)
    if not jobs:
        sys.exit("No corpus files found. Run tools/fetch_corpus.py first.")

    results = []
    with Pool(max(1, args.jobs)) as pool:
        for r in pool.imap_unordered(run_file, jobs):
            results.append(r)
            print(f"[{len(results)}/{len(jobs)}] {r['id']}", file=sys.stderr, flush=True)
    results.sort(key=lambda r: r["id"])
    print(write_report(results, {n for n, _ in tools}, versions, args, args.out))


if __name__ == "__main__":
    main()
