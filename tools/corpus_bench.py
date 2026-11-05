"""Runs the public benchmark corpus through PdfNetStack and writes a Markdown and a JSON report.

Development tool only. Nothing in src/ depends on it.
Requires: pip install pypdfium2 numpy, and the .NET 10 SDK.

Usage:
  python tools/fetch_corpus.py --cache artifacts/corpus/files
  dotnet build tools/PdfNetStack.CorpusRunner -c Release
  python tools/corpus_bench.py [--cache artifacts/corpus/files] [--out artifacts/corpus/report]
                               [--only id,id] [--dpi 100] [--pages 6] [--jobs 4] [--timeout 900]

For every file in eng/corpus/manifest.json, tools/PdfNetStack.CorpusRunner runs in its own
process to:
  - optimize it under each profile (lossless; lossy: JPEG quality 75, 150 dpi, photographs to
    JPEG, SSIM gate 0.95, progressive JPEG; balanced: the preset, palettes and JPEG quality 75
    at full resolution behind the same gate), measuring size, time and peak memory, and check
    every kept rewrite again with PdfEquivalence;
  - extract the text of every page and decode every image, from the original and from each
    rewrite, and compare them.
PDFium (through pypdfium2) then renders the first pages and the last page of the original and of
each rewrite and compares page count, geometry, PDFium's own text and the pixels: a lossless
rewrite must render identically, a lossy one is measured by PSNR.

The report lists failures (anything that would be a bug: a crash, a rewrite that fails a check, a
lossless rewrite that renders or reads differently) and warnings (what the library cannot read
yet, such as an image it does not decode). The exit code is 1 when there is any failure.
"""

import argparse
import datetime
import json
import math
import os
import platform
import subprocess
import sys
from multiprocessing import Pool

import fetch_corpus

ROOT = fetch_corpus.ROOT
DEFAULT_RUNNER = os.path.join(ROOT, "tools", "PdfNetStack.CorpusRunner", "bin", "Release", "net10.0", "PdfNetStack.CorpusRunner.dll")
DEFAULT_OUT = os.path.join(ROOT, "artifacts", "corpus", "report")
PROFILES = ["lossless", "lossy", "balanced"]
# Below this PSNR a lossy page is listed as visibly changed. It is a note, not a failure.
VISIBLE_PSNR = 30.0


def run_runner(runner, args, timeout):
    """Runs the runner once and returns its JSON, or a dict with "error" describing the failure."""
    try:
        done = subprocess.run(["dotnet", runner] + args, capture_output=True, timeout=timeout)
    except subprocess.TimeoutExpired:
        return {"error": f"timed out after {timeout} s"}
    out = done.stdout.decode("utf-8", "replace").strip()
    try:
        data = json.loads(out.splitlines()[-1]) if out else {}
    except json.JSONDecodeError:
        data = {}
    if not data:
        tail = done.stderr.decode("utf-8", "replace").strip().splitlines()[-3:]
        return {"error": f"runner exited with {done.returncode}: " + " | ".join(tail)}
    return data


def compare_extraction(original, rewrite, lossless):
    """Problems found comparing what the library reads from the original and from a rewrite."""
    problems = []
    if "error" in rewrite:
        return [f"rewrite cannot be read: {rewrite['error']}"]
    if original.get("pages") != rewrite.get("pages"):
        return [f"page count {original.get('pages')} vs {rewrite.get('pages')}"]
    a, b = original.get("page_text_sha256", []), rewrite.get("page_text_sha256", [])
    differing = [i + 1 for i, (x, y) in enumerate(zip(a, b)) if x != y]
    if differing:
        problems.append(f"extracted text differs on {len(differing)} page(s), first {differing[:5]}")
    if original.get("images") != rewrite.get("images"):
        problems.append(f"images drawn {original.get('images')} vs {rewrite.get('images')}")
    elif rewrite.get("images_decoded", 0) < original.get("images_decoded", 0):
        problems.append(f"images decoded {original.get('images_decoded')} vs {rewrite.get('images_decoded')}")
    elif lossless and original.get("decoded_images_sha256") != rewrite.get("decoded_images_sha256"):
        problems.append("decoded image pixels differ")
    return problems


def page_selection(count, limit):
    if count <= limit:
        return list(range(count))
    return list(range(limit - 1)) + [count - 1]


def render_job(job):
    """Renders an original and its rewrites with PDFium and compares them page by page."""
    import numpy as np
    import pypdfium2 as pdfium

    original, rewrites, dpi, limit = job
    results = {}
    try:
        a = pdfium.PdfDocument(original)
    except Exception as ex:  # noqa: BLE001 - PDFium refusing the original is reported, not fatal
        return original, {"original_error": f"PDFium cannot open the original: {ex}"}
    scale = dpi / 72.0
    pages = page_selection(len(a), limit)
    cache = {}
    for profile, path, lossless in rewrites:
        r = {"pages_compared": 0, "identical_pages": 0, "problems": [], "min_psnr": None}
        try:
            b = pdfium.PdfDocument(path)
        except Exception as ex:  # noqa: BLE001
            r["problems"].append(f"PDFium cannot open the rewrite: {ex}")
            results[profile] = r
            continue
        if len(a) != len(b):
            r["problems"].append(f"page count {len(a)} vs {len(b)}")
            results[profile] = r
            b.close()
            continue
        psnrs = []
        for i in pages:
            pa, pb = a[i], b[i]
            if pa.get_size() != pb.get_size() or pa.get_rotation() != pb.get_rotation():
                r["problems"].append(f"page {i + 1}: geometry differs")
                continue
            if i not in cache:
                cache[i] = (pa.get_textpage().get_text_range(), pa.render(scale=scale, may_draw_forms=True).to_numpy().copy())
            ta, ia = cache[i]
            if ta != pb.get_textpage().get_text_range():
                r["problems"].append(f"page {i + 1}: PDFium text differs")
            ib = pb.render(scale=scale, may_draw_forms=True).to_numpy()
            r["pages_compared"] += 1
            if ia.shape != ib.shape:
                r["problems"].append(f"page {i + 1}: bitmap size differs")
                continue
            if np.array_equal(ia, ib):
                r["identical_pages"] += 1
                psnrs.append(math.inf)
                continue
            diff = ia.astype(np.int32) - ib.astype(np.int32)
            mse = float(np.mean(diff * diff))
            psnr = 10 * math.log10(255 * 255 / mse)
            psnrs.append(psnr)
            if lossless:
                changed = int(np.count_nonzero(np.any(diff != 0, axis=-1)))
                r["problems"].append(f"page {i + 1}: {changed} pixels differ (PSNR {psnr:.1f} dB)")
        finite = [p for p in psnrs if p != math.inf]
        r["min_psnr"] = round(min(finite), 2) if finite else None
        results[profile] = r
        b.close()
    a.close()
    return original, results


def optimize_and_check(entry, source, out_dir, runner, timeout, original):
    """Runs every profile on one file. Returns the per profile results."""
    results = {}
    for profile in PROFILES:
        target = os.path.join(out_dir, profile, *entry["file"].split("/"))
        if os.path.exists(target):
            os.remove(target)
        r = run_runner(runner, ["optimize", source, target, "--profile", profile], timeout)
        r["problems"] = []
        if "error" in r:
            r["problems"].append("optimizer: " + r["error"])
        elif r.get("disposition") == "Optimized":
            if r.get("equivalence_difference"):
                r["problems"].append("PdfEquivalence: " + r["equivalence_difference"])
            if "error" not in original:
                rewrite = run_runner(runner, ["extract", target], timeout)
                r["extract"] = summarize_extract(rewrite)
                r["problems"] += compare_extraction(original, rewrite, profile == "lossless")
            r["output"] = target
        elif r.get("reason", "").startswith(("Verification failed", "Rewrite failed")):
            r["problems"].append(r["reason"])
        results[profile] = r
    return results


def summarize_extract(e):
    """The extraction result without the per page hashes, for the report."""
    return {k: v for k, v in e.items() if k != "page_text_sha256"}


def megabytes_per_second(size, ms):
    return size / 1048576 / (ms / 1000) if ms else 0.0


def human(size):
    for unit, step in (("GiB", 1 << 30), ("MiB", 1 << 20), ("KiB", 1 << 10)):
        if size >= step:
            return f"{size / step:.1f} {unit}"
    return f"{size} B"


def environment(runner):
    import pypdfium2

    commit = os.environ.get("GITHUB_SHA")
    if not commit:
        try:
            commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
        except OSError:
            commit = ""
    dotnet = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
    return {
        "date": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M UTC"),
        "commit": commit,
        "os": f"{platform.system()} {platform.release()} ({platform.machine()})",
        "processors": os.cpu_count(),
        "dotnet_sdk": dotnet,
        "pypdfium2": pypdfium2.version.PYPDFIUM_INFO.version,
        "pdfium": str(pypdfium2.version.PDFIUM_INFO.build),
        "runner": os.path.relpath(runner, ROOT).replace("\\", "/"),
    }


def write_markdown(path, report):
    env, docs = report["environment"], report["documents"]
    lines = [
        "# PdfNetStack corpus benchmark",
        "",
        f"{env['date']}, commit `{env['commit'][:12]}`, {env['os']}, {env['processors']} processors, "
        f".NET SDK {env['dotnet_sdk']}, PDFium {env['pdfium']} (pypdfium2 {env['pypdfium2']}).",
        "",
        "Profiles: **lossless** is the default optimizer; **lossy** adds JPEG quality 75, downsampling to 150 dpi, "
        "photographs to JPEG and progressive JPEG behind an SSIM gate of 0.95; **balanced** is the preset of that name: "
        "palettes for drawings and screenshots and JPEG quality 75 at full resolution, behind the same gate. All accept "
        "files with incremental updates or repaired cross-references and keep any rewrite that is not larger. Time is the optimizer call, "
        "including its own equivalence proof; memory is the peak working set of the process that ran it.",
        "",
        "## Summary",
        "",
        "| Profile | Documents | Optimized | Kept as is | Input | Output | Saving | Optimizer time | Throughput (optimized) | Max peak memory |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for profile in PROFILES:
        rows = [d["profiles"][profile] for d in docs if profile in d.get("profiles", {}) and "disposition" in d["profiles"][profile]]
        if not rows:
            continue
        before = sum(r["input_bytes"] for r in rows)
        after = sum(r["output_bytes"] for r in rows)
        optimized = sum(1 for r in rows if r["disposition"] == "Optimized")
        seconds = sum(r["milliseconds"] for r in rows) / 1000
        kept = [r for r in rows if r["disposition"] == "Optimized"]
        rate = megabytes_per_second(sum(r["input_bytes"] for r in kept), sum(r["milliseconds"] for r in kept))
        peak = max(r["peak_working_set_bytes"] for r in rows)
        lines.append(
            f"| {profile} | {len(rows)} | {optimized} | {len(rows) - optimized} | {human(before)} | {human(after)} | "
            f"{100 * (before - after) / before:.1f}% | {seconds:.1f} s | {rate:.2f} MB/s | {human(peak)} |")
    failures = [(d["id"], p) for d in docs for p in d["failures"]]
    warnings = [(d["id"], w) for d in docs for w in d["warnings"]]
    lines += ["", f"**{len(failures)} failure(s), {len(warnings)} warning(s)** across {len(docs)} documents.", ""]

    lines += [
        "## Documents",
        "",
        "| Document | Pages | Size | Lossless | Lossy | Lossless time | Peak memory | Text | Images decoded | Render |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|",
    ]
    for d in docs:
        o = d.get("original", {})
        cells = [d["id"], str(o.get("pages", "")), human(d["bytes"])]
        for profile in PROFILES:
            r = d.get("profiles", {}).get(profile, {})
            if "disposition" not in r:
                cells.append("error" if r else "")
            elif r["disposition"] == "Optimized":
                cells.append(f"{100 * (r['input_bytes'] - r['output_bytes']) / r['input_bytes']:.1f}%")
            else:
                cells.append("kept: " + short_reason(r.get("reason", "")))
        lossless = d.get("profiles", {}).get("lossless", {})
        cells.append(f"{lossless['milliseconds'] / 1000:.2f} s" if "milliseconds" in lossless else "")
        cells.append(human(lossless["peak_working_set_bytes"]) if "peak_working_set_bytes" in lossless else "")
        if "error" in o:
            cells += [o["error"][:40], ""]
        else:
            cells.append(f"{o.get('text_characters', 0):,} chars in {o.get('text_ms', 0) / 1000:.2f} s")
            cells.append(f"{o.get('images_decoded', 0)}/{o.get('images', 0)}")
        render = d.get("render", {})
        verdicts = []
        for profile in PROFILES:
            r = render.get(profile)
            if r is None:
                continue
            if r["problems"]:
                verdicts.append(f"{profile}: differs")
            elif r["identical_pages"] == r["pages_compared"]:
                verdicts.append(f"{profile}: identical")
            else:
                verdicts.append(f"{profile}: min PSNR {r['min_psnr']:.1f} dB")
        if "original_error" in render:
            verdicts.append("PDFium cannot open")
        cells.append(", ".join(verdicts))
        lines.append("| " + " | ".join(c.replace("|", "/") for c in cells) + " |")

    lines += ["", "## Failures", ""]
    lines += [f"- `{i}`: {p}" for i, p in failures] or ["None."]
    lines += ["", "## Warnings", ""]
    lines += [f"- `{i}`: {w}" for i, w in warnings] or ["None."]

    coverage = {}
    for d in docs:
        for tag in d["exercises"]:
            coverage[tag] = coverage.get(tag, 0) + 1
    lines += ["", "## Coverage", "", "| Exercises | Documents |", "|---|---:|"]
    lines += [f"| {tag} | {count} |" for tag, count in sorted(coverage.items())]
    lines += ["", "Sources and licenses: `eng/corpus/manifest.json`.", ""]
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))


def short_reason(reason):
    reason = reason.split(".")[0]
    return reason[:40]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", default=fetch_corpus.DEFAULT_MANIFEST)
    parser.add_argument("--cache", default=fetch_corpus.DEFAULT_CACHE)
    parser.add_argument("--out", default=DEFAULT_OUT)
    parser.add_argument("--runner", default=DEFAULT_RUNNER, help="the built PdfNetStack.CorpusRunner.dll")
    parser.add_argument("--only", help="comma separated ids")
    parser.add_argument("--profiles", help="comma separated profiles, instead of " + ",".join(PROFILES))
    parser.add_argument("--dpi", type=int, default=100)
    parser.add_argument("--pages", type=int, default=6, help="pages rendered per document: the first ones and the last")
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4, help="parallel render processes")
    parser.add_argument("--timeout", type=int, default=900, help="seconds per runner call")
    args = parser.parse_args()

    if args.profiles:
        PROFILES[:] = args.profiles.split(",")
    if not os.path.exists(args.runner):
        print("Build the runner first: dotnet build tools/PdfNetStack.CorpusRunner -c Release")
        return 2
    entries = fetch_corpus.load_manifest(args.manifest)["files"]
    if args.only:
        wanted = set(args.only.split(","))
        entries = [e for e in entries if e["id"] in wanted]
    rewrites_dir = os.path.join(args.out, "rewrites")
    os.makedirs(args.out, exist_ok=True)

    docs = []
    for n, entry in enumerate(entries, 1):
        print(f"[{n}/{len(entries)}] {entry['id']}", flush=True)
        d = {k: entry[k] for k in ("id", "title", "sha256", "bytes", "license", "exercises")}
        d["failures"], d["warnings"] = [], []
        source = fetch_corpus.cached_path(args.cache, entry)
        if not fetch_corpus.is_valid(source, entry):
            d["failures"].append("not in the cache or does not match the manifest; run tools/fetch_corpus.py")
            docs.append(d)
            continue
        original = run_runner(args.runner, ["extract", source], args.timeout)
        d["original"] = summarize_extract(original)
        if "error" in original:
            note = "the library cannot open it: " + original["error"]
            (d["warnings"] if original["error"] == "password required" or original["error"].startswith("not readable") else d["failures"]).append(note)
        else:
            undecodable = original.get("undecodable_encodings", {})
            if undecodable:
                d["warnings"].append("images not decoded: " + ", ".join(f"{k} x{v}" for k, v in undecodable.items()))
            for e in original.get("errors", [])[:3]:
                d["warnings"].append("extraction: " + e)
        d["profiles"] = optimize_and_check(entry, source, rewrites_dir, args.runner, args.timeout, original)
        for profile, r in d["profiles"].items():
            d["failures"] += [f"{profile}: {p}" for p in r.pop("problems")]
        docs.append(d)

    jobs = []
    for d in docs:
        rewrites = [(p, r["output"], p == "lossless") for p, r in d.get("profiles", {}).items() if "output" in r]
        if rewrites:
            jobs.append((fetch_corpus.cached_path(args.cache, next(e for e in entries if e["id"] == d["id"])), rewrites, args.dpi, args.pages))
    by_source = {}
    if jobs:
        print(f"Rendering {len(jobs)} document(s) with PDFium", flush=True)
        with Pool(max(1, args.jobs)) as pool:
            for source, result in pool.imap_unordered(render_job, jobs):
                by_source[source] = result
    for d in docs:
        entry = next(e for e in entries if e["id"] == d["id"])
        result = by_source.get(fetch_corpus.cached_path(args.cache, entry))
        if result is None:
            continue
        d["render"] = result
        if "original_error" in result:
            d["warnings"].append(result["original_error"])
        for profile in PROFILES:
            r = result.get(profile)
            if r is None:
                continue
            d["failures"] += [f"{profile} render: {p}" for p in r["problems"]]
            if profile != "lossless" and r["min_psnr"] is not None and r["min_psnr"] < VISIBLE_PSNR:
                d["warnings"].append(f"{profile} render: lowest page PSNR {r['min_psnr']:.1f} dB")
        for profile, r in d.get("profiles", {}).items():
            r.pop("output", None)

    report = {"environment": environment(args.runner), "profiles": PROFILES, "documents": docs}
    with open(os.path.join(args.out, "report.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(report, f, ensure_ascii=False, indent=1)
    write_markdown(os.path.join(args.out, "report.md"), report)
    failures = sum(len(d["failures"]) for d in docs)
    print(f"Report written to {args.out}: {failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
