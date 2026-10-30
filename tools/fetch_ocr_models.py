"""Downloads the PP-OCRv5 ONNX models PdfNetStack.Ocr runs, checking each against its pinned SHA-256.

The models are PaddleOCR's (Apache-2.0), converted to ONNX and published by the RapidOCR project
(Apache-2.0) on modelscope.cn; the pins are the hashes in RapidOCR's default_models.yaml at
v3.10.0, and the same ones OnnxOcrEngine checks on load.

    python tools/fetch_ocr_models.py [folder]          (default: ./ocr-models)

Then point the engine (and the integration tests, through PDFNETSTACK_OCR_MODELS) at the folder.
To pack PdfNetStack.Ocr.Models, fetch into its models folder; the build then also copies the
models next to the tests:

    python tools/fetch_ocr_models.py src/PdfNetStack.Ocr.Models/models
"""

import hashlib
import sys
import urllib.request
from pathlib import Path

BASE = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5"

MODELS = {
    "det/ch_PP-OCRv5_det_mobile.onnx": "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae",
    "cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx": "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7",
    "rec/latin_PP-OCRv5_rec_mobile.onnx": "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a",
    "rec/arabic_PP-OCRv5_rec_mobile.onnx": "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> None:
    folder = Path(sys.argv[1] if len(sys.argv) > 1 else "ocr-models")
    folder.mkdir(parents=True, exist_ok=True)
    for remote, pin in MODELS.items():
        target = folder / Path(remote).name
        if target.exists() and sha256(target) == pin:
            print(f"ok       {target.name}")
            continue
        partial = target.with_suffix(".part")
        urllib.request.urlretrieve(f"{BASE}/{remote}", partial)
        actual = sha256(partial)
        if actual != pin:
            partial.unlink()
            sys.exit(f"{target.name}: SHA-256 {actual} does not match the pin {pin}")
        partial.replace(target)
        print(f"fetched  {target.name}")


if __name__ == "__main__":
    main()
