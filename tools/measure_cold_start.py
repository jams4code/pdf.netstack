"""Measures cold start: the first call of each common operation in a fresh process.

Development tool only. Nothing in src/ depends on it. It runs tests/PdfNetStack.AotSmoke once
per step and per repetition, each time in a new process, so every measurement pays for type
loading, static initialization and (under the JIT) compilation, as the first request of a new
serverless instance does. It prints the medians of the first call's wall time and allocations,
and of a second call in the same process: the difference is the cold start cost. Process start
up itself is not included: the clock starts when the inputs are in memory.

Steps: open-save (PdfDocument.Open and Save to a stream), optimize (PdfOptimizer.OptimizeAsync
with default options), text (open and extract the text of every page), html (HtmlConverter with
UseSystemFonts false), html-system-fonts (the same with installed fonts scanned, the default) and
font-text (a line of text in an embedded TrueType font: loading, shaping and subsetting, with the
synthetic font of the test fixtures).

Usage:
  dotnet build tests/PdfNetStack.AotSmoke -c Release -f net10.0
  python tools/measure_cold_start.py                      # JIT, the build above
  dotnet publish tests/PdfNetStack.AotSmoke -c Release -f net10.0 -r linux-x64 -o aot-smoke
  python tools/measure_cold_start.py --app aot-smoke/PdfNetStack.AotSmoke
"""

import argparse
import os
import shutil
import statistics
import subprocess
import sys
import tempfile

STEPS = ["open-save", "optimize", "text", "html", "html-system-fonts", "font-text"]

FONT = os.path.join(os.path.dirname(__file__), "..", "tests", "PdfNetStack.Tests", "Fixtures", "Fonts", "Test-Regular.ttf")

DEFAULT_APP = os.path.join(os.path.dirname(__file__), "..", "tests", "PdfNetStack.AotSmoke", "bin", "Release", "net10.0", "PdfNetStack.AotSmoke.dll")


def command(app):
    return ["dotnet", app] if app.endswith(".dll") else [app]


def run(app, arguments):
    result = subprocess.run(command(app) + arguments, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(" ".join(command(app) + arguments) + "\n" + result.stdout + result.stderr)
    return result.stdout.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default=DEFAULT_APP, help="the smoke app: a .dll run with dotnet, or a native executable")
    parser.add_argument("--runs", type=int, default=7, help="fresh processes per step (default 7)")
    parser.add_argument("--steps", default=",".join(STEPS), help="comma separated steps (default all)")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as inputs:
        run(args.app, ["--inputs", inputs])
        shutil.copyfile(FONT, os.path.join(inputs, "font.ttf"))
        print(f"{'step':<20}{'first ms':>10}{'allocated KB':>15}{'second ms':>11}")
        for step in args.steps.split(","):
            first, allocated, second = [], [], []
            for _ in range(args.runs):
                _, ms, size, again = run(args.app, ["--cold", step, inputs]).splitlines()[-1].split(",")
                first.append(float(ms))
                allocated.append(int(size))
                second.append(float(again))
            print(f"{step:<20}{statistics.median(first):>10.1f}{statistics.median(allocated) / 1024:>15.0f}{statistics.median(second):>11.1f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
