"""Downloads the public benchmark corpus listed in eng/corpus/manifest.json and verifies it.

Development tool only. Nothing in src/ depends on it, and no file it downloads is ever committed
(the repository ignores *.pdf). Every entry names a public URL, the SHA-256 and size of the file
it served when the entry was added, and the license under which it is published.

Usage:
  python tools/fetch_corpus.py [--cache artifacts/corpus/files] [--only id,id] [--jobs 4]

A file already in the cache with the right hash is not downloaded again. A download is refused
as soon as it grows past the size the manifest records, and kept only when its hash matches. The
exit code is 1 when any file could not be fetched or no longer matches (an upstream change), so
that the entry can be reviewed; the other files are still fetched.
"""

import argparse
import concurrent.futures
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
DEFAULT_MANIFEST = os.path.join(ROOT, "eng", "corpus", "manifest.json")
DEFAULT_CACHE = os.path.join(ROOT, "artifacts", "corpus", "files")
USER_AGENT = "PdfNetStack-corpus/1.0 (+https://github.com/jams4code/pdf.netstack)"
ATTEMPTS = 3


def load_manifest(path):
    with open(path, encoding="utf-8") as f:
        manifest = json.load(f)
    entries = manifest["files"]
    ids = [e["id"] for e in entries]
    if len(ids) != len(set(ids)):
        raise ValueError("duplicate ids in " + path)
    for e in entries:
        if not e["url"].startswith("https://"):
            raise ValueError(e["id"] + ": only https URLs are allowed")
        if os.path.isabs(e["file"]) or ".." in e["file"].replace("\\", "/").split("/"):
            raise ValueError(e["id"] + ": file must be a relative path inside the cache")
    return manifest


def cached_path(cache, entry):
    return os.path.join(cache, *entry["file"].split("/"))


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def is_valid(path, entry):
    return os.path.isfile(path) and os.path.getsize(path) == entry["bytes"] and sha256_of(path) == entry["sha256"]


def download(entry, target):
    """Downloads one entry to target. Returns None on success, else what went wrong."""
    partial = target + ".part"
    os.makedirs(os.path.dirname(target), exist_ok=True)
    error = None
    for attempt in range(ATTEMPTS):
        if attempt:
            time.sleep(5 * attempt)
        try:
            request = urllib.request.Request(entry["url"], headers={"User-Agent": USER_AGENT})
            h = hashlib.sha256()
            size = 0
            with urllib.request.urlopen(request, timeout=120) as response, open(partial, "wb") as out:
                while True:
                    chunk = response.read(1 << 20)
                    if not chunk:
                        break
                    size += len(chunk)
                    if size > entry["bytes"]:
                        raise ValueError(f"larger than the {entry['bytes']} bytes the manifest records")
                    h.update(chunk)
                    out.write(chunk)
            if size != entry["bytes"] or h.hexdigest() != entry["sha256"]:
                os.remove(partial)
                return f"upstream file changed: {size} bytes, sha256 {h.hexdigest()}"
            os.replace(partial, target)
            return None
        except ValueError as ex:
            error = str(ex)
            break
        except (urllib.error.URLError, OSError, TimeoutError) as ex:
            error = f"{type(ex).__name__}: {ex}"
    if os.path.exists(partial):
        os.remove(partial)
    return error


def fetch(entry, cache):
    target = cached_path(cache, entry)
    if is_valid(target, entry):
        return entry["id"], "cached", None
    if os.path.exists(target):
        os.remove(target)
    problem = download(entry, target)
    return entry["id"], "downloaded" if problem is None else "failed", problem


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", default=DEFAULT_MANIFEST)
    parser.add_argument("--cache", default=DEFAULT_CACHE)
    parser.add_argument("--only", help="comma separated ids to fetch")
    parser.add_argument("--jobs", type=int, default=4, help="parallel downloads")
    args = parser.parse_args()

    entries = load_manifest(args.manifest)["files"]
    if args.only:
        wanted = set(args.only.split(","))
        entries = [e for e in entries if e["id"] in wanted]
    os.makedirs(args.cache, exist_ok=True)

    failures = 0
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        for identifier, state, problem in pool.map(lambda e: fetch(e, args.cache), entries):
            if problem:
                failures += 1
                print(f"FAILED {identifier}: {problem}")
            else:
                print(f"{state:10} {identifier}")
    total = sum(e["bytes"] for e in entries)
    print(f"{len(entries) - failures} of {len(entries)} files ready in {args.cache} ({total / 1048576:.1f} MiB listed)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
