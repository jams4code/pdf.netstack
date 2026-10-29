"""Measures what each structural optimizer pass saves, and checks linearized output with qpdf.

Development tool only. Nothing in src/ depends on it. It runs the pdfnetstack command line
tool over a folder of PDFs, best made with tools/make_optimizer_samples.py, once per pass
configuration, and prints the output size of each. With --linearize it also writes linearized
copies and checks each with "qpdf --check-linearization" when qpdf is on the PATH, or with the
same qpdf check through pikepdf (which bundles libqpdf) when that is installed. When neither is
available the check is skipped and says so.

Usage:
  dotnet build src/PdfNetStack.Cli -c Release
  python tools/make_optimizer_samples.py samples
  python tools/measure_optimizer.py samples [--linearize] [--tool path/to/PdfNetStack.Tool.dll]
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile

CONFIGURATIONS = [
    ("before", ["--no-prune", "--no-compact", "--no-font-merge"]),
    ("+prune", ["--no-compact", "--no-font-merge"]),
    ("+compact", ["--no-prune", "--no-font-merge"]),
    ("+fonts", ["--no-prune", "--no-compact"]),
    ("all", []),
]

DEFAULT_TOOL = os.path.join(os.path.dirname(__file__), "..", "src", "PdfNetStack.Cli", "bin", "Release", "net10.0", "PdfNetStack.Tool.dll")


def optimize(tool, source, target, flags):
    command = ["dotnet", tool, "optimize", source, target, "--min-saving", "0"] + flags
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(" ".join(command) + "\n" + result.stdout + result.stderr)
    first = result.stdout.splitlines()[0] if result.stdout else ""
    if not first.startswith("Optimized"):
        return os.path.getsize(source), first
    return os.path.getsize(target), first


def qpdf_checker():
    """A function checking one file's linearization, and the name of what it uses; None when nothing can."""
    qpdf = shutil.which("qpdf")
    if qpdf:
        def run(path):
            result = subprocess.run([qpdf, "--check-linearization", path], capture_output=True, text=True)
            return result.returncode == 0, (result.stdout + result.stderr).strip()
        return run, qpdf
    try:
        import io
        import pikepdf
    except ImportError:
        return None, None

    def check(path):
        with pikepdf.open(path) as pdf:
            report = io.StringIO()
            ok = pdf.check_linearization(stream=report)
            return ok, report.getvalue().strip()
    return check, "pikepdf %s (libqpdf %s)" % (pikepdf.__version__, pikepdf.__libqpdf_version__)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("folder")
    parser.add_argument("--linearize", action="store_true", help="also write linearized output and check it with qpdf")
    parser.add_argument("--tool", default=DEFAULT_TOOL, help="the built command line tool")
    args = parser.parse_args()

    if not os.path.exists(args.tool):
        print("Build the command line tool first: dotnet build src/PdfNetStack.Cli -c Release")
        return 2

    files = sorted(f for f in os.listdir(args.folder) if f.lower().endswith(".pdf"))
    header = ["file", "input"] + [name for name, _ in CONFIGURATIONS] + (["linearized"] if args.linearize else [])
    print(" | ".join(header))
    print(" | ".join("---" for _ in header))
    failures = 0
    checker, checker_name = qpdf_checker() if args.linearize else (None, None)
    with tempfile.TemporaryDirectory() as work:
        for name in files:
            source = os.path.join(args.folder, name)
            size = os.path.getsize(source)
            row = [name, str(size)]
            for config, flags in CONFIGURATIONS:
                out, _ = optimize(args.tool, source, os.path.join(work, config + "-" + name), flags)
                row.append("%d (%.1f%%)" % (out, 100.0 * (size - out) / size))
            if args.linearize:
                target = os.path.join(work, "linear-" + name)
                out, status = optimize(args.tool, source, target, ["--linearize"])
                verdict = "not written: " + status
                if os.path.exists(target):
                    if checker is None:
                        verdict = "qpdf check skipped"
                    else:
                        ok, report = checker(target)
                        verdict = "qpdf ok" if ok else "qpdf FAILED"
                        if not ok:
                            failures += 1
                            print(report, file=sys.stderr)
                row.append("%d, %s" % (out, verdict))
            print(" | ".join(row))

    if args.linearize:
        print()
        print("Linearization checked with " + checker_name if checker else "Neither qpdf nor pikepdf was found; linearization was not checked.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
