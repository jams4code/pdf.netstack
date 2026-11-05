"""Compares two corpus benchmark reports and writes the differences as Markdown.

Development tool only. Nothing in src/ depends on it.

Usage:
  python tools/corpus_compare.py previous/report.json current/report.json [--out comparison.md]
      [--previous-label v1.1.0] [--current-label v1.2.0]

The release workflow runs it on every version tag against the report attached to the previous
release, so a regression in size, time or memory shows next to the release it came with. Only
documents both reports hold, under the same SHA-256 when the reports record it, are compared.

A document regresses when, under one profile:
  - its rewrite grows by more than 1% of the original size;
  - it took more than 1.5 times as long, and at least a second longer;
  - its peak working set grew by more than 1.3 times, and by at least 50 MiB;
  - it was rewritten before and is now left unchanged, or it now fails a check.
Times and memory depend on the runner, so they are reported, never treated as failures. The exit
code is 0 unless a report cannot be read.
"""

import argparse
import json
import sys

SIZE_GROWTH = 0.01
TIME_FACTOR, TIME_MIN_MS = 1.5, 1000
MEMORY_FACTOR, MEMORY_MIN_BYTES = 1.3, 50 << 20


def load(path):
    with open(path, encoding="utf-8") as f:
        report = json.load(f)
    return report, {d["id"]: d for d in report.get("documents", [])}


def human(n):
    for unit in ("B", "KiB", "MiB", "GiB"):
        if abs(n) < 1024 or unit == "GiB":
            return f"{n:.1f} {unit}" if unit != "B" else f"{n} B"
        n /= 1024
    return f"{n:.1f} GiB"


def rewritten(r):
    return r is not None and "error" not in r and r.get("disposition") == "Optimized"


def totals(docs, ids, profile):
    original = output = ms = 0
    peak = 0
    for i in ids:
        d = docs[i]
        r = d.get("profiles", {}).get(profile)
        if r is None or "error" in r or "output_bytes" not in r:
            continue
        original += d["bytes"]
        output += r["output_bytes"]
        ms += r.get("milliseconds", 0)
        peak = max(peak, r.get("peak_working_set_bytes", 0))
    return original, output, ms, peak


def regressions(i, before, after, profile):
    found = []
    b = before.get("profiles", {}).get(profile)
    a = after.get("profiles", {}).get(profile)
    if b is None or a is None:
        return found
    if "error" in a and "error" not in b:
        found.append(f"now fails: {a['error'][:80]}")
        return found
    if rewritten(b) and not rewritten(a):
        found.append(f"was rewritten, now {a.get('disposition', 'not rewritten')}: {a.get('reason', '')[:80]}")
    if "output_bytes" in a and "output_bytes" in b and a["output_bytes"] - b["output_bytes"] > SIZE_GROWTH * after["bytes"]:
        found.append(f"output {human(b['output_bytes'])} to {human(a['output_bytes'])}")
    tb, ta = b.get("milliseconds"), a.get("milliseconds")
    if tb and ta and ta > TIME_FACTOR * tb and ta - tb >= TIME_MIN_MS:
        found.append(f"time {tb / 1000:.2f} s to {ta / 1000:.2f} s")
    mb, ma = b.get("peak_working_set_bytes"), a.get("peak_working_set_bytes")
    if mb and ma and ma > MEMORY_FACTOR * mb and ma - mb >= MEMORY_MIN_BYTES:
        found.append(f"peak memory {human(mb)} to {human(ma)}")
    return found


def compare(previous, current, previous_label, current_label):
    report_b, before = load(previous)
    report_a, after = load(current)
    common = sorted(i for i in before.keys() & after.keys()
                    if before[i].get("sha256") == after[i].get("sha256"))
    profiles = [p for p in report_a.get("profiles", []) if p in report_b.get("profiles", [])]

    lines = [f"# Corpus benchmark: {current_label} against {previous_label}", ""]
    lines.append(f"{len(common)} document(s) in both reports. Times and memory depend on the runner; "
                 "sizes and failures do not.")
    lines += ["", "| Profile | Original | Output before | Output now | Saving before | Saving now | Time before | Time now | Peak before | Peak now |",
              "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for profile in profiles:
        ob, outb, msb, pb = totals(before, common, profile)
        oa, outa, msa, pa = totals(after, common, profile)
        saving_b = f"{100 * (1 - outb / ob):.1f}%" if ob else ""
        saving_a = f"{100 * (1 - outa / oa):.1f}%" if oa else ""
        lines.append(f"| {profile} | {human(oa)} | {human(outb)} | {human(outa)} | {saving_b} | {saving_a} | "
                     f"{msb / 1000:.1f} s | {msa / 1000:.1f} s | {human(pb)} | {human(pa)} |")

    found = []
    for i in common:
        for profile in profiles:
            found += [(i, profile, r) for r in regressions(i, before[i], after[i], profile)]
    new_failures = [(i, f) for i in common for f in after[i].get("failures", []) if f not in before[i].get("failures", [])]
    fixed = [(i, f) for i in common for f in before[i].get("failures", []) if f not in after[i].get("failures", [])]

    lines += ["", "## Regressions", ""]
    lines += [f"- `{i}` ({p}): {r}" for i, p, r in found] or ["None."]
    lines += ["", "## New failures", ""]
    lines += [f"- `{i}`: {f}" for i, f in new_failures] or ["None."]
    lines += ["", "## Fixed failures", ""]
    lines += [f"- `{i}`: {f}" for i, f in fixed] or ["None."]
    only_b = sorted(before.keys() - after.keys())
    only_a = sorted(after.keys() - before.keys())
    if only_b or only_a:
        lines += ["", "## Not compared", ""]
        lines += [f"- `{i}`: only in {previous_label}" for i in only_b]
        lines += [f"- `{i}`: only in {current_label}" for i in only_a]
    lines.append("")
    return "\n".join(lines), len(found) + len(new_failures)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("previous")
    parser.add_argument("current")
    parser.add_argument("--out", help="Markdown file to write; standard output when omitted")
    parser.add_argument("--previous-label", default="previous")
    parser.add_argument("--current-label", default="current")
    args = parser.parse_args()
    try:
        text, count = compare(args.previous, args.current, args.previous_label, args.current_label)
    except (OSError, ValueError, KeyError) as e:
        print(f"Cannot compare the reports: {e}", file=sys.stderr)
        return 2
    if args.out:
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    else:
        sys.stdout.write(text)
    print(f"{count} regression(s) or new failure(s)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
