"""Bounded synthetic English OCR proof. Requires a separately supplied Tesseract 5 x64 runtime."""
from __future__ import annotations

import argparse
import csv
import ctypes
import hashlib
import io
import json
import os
import shutil
import subprocess
import sys
import uuid
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "python-packages"))

from PIL import Image, ImageDraw, ImageFont  # noqa: E402
from pypdf import PdfReader, PdfWriter  # noqa: E402
from reportlab.lib.utils import ImageReader  # noqa: E402
from reportlab.pdfbase.pdfmetrics import stringWidth  # noqa: E402
from reportlab.pdfgen import canvas  # noqa: E402


PAGE_W, PAGE_H = 612.0, 792.0
PIX_W, PIX_H = 1275, 1650
EXPECTED = [
    "ENGLISH OCR PROOF",
    "The quick brown fox jumps over the lazy dog.",
    "Invoice number: 48271",
    "Total due: $125.40",
]


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_source(image_path: Path, pdf_path: Path) -> None:
    image = Image.new("RGB", (PIX_W, PIX_H), "white")
    draw = ImageDraw.Draw(image)
    font_path = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / "arial.ttf"
    if not font_path.is_file():
        raise RuntimeError(f"The synthetic fixture needs a local Arial font at {font_path}.")
    title_font = ImageFont.truetype(str(font_path), 70)
    body_font = ImageFont.truetype(str(font_path), 46)
    draw.text((125, 215), EXPECTED[0], font=title_font, fill="black")
    draw.text((125, 430), EXPECTED[1], font=body_font, fill="black")
    draw.text((125, 600), EXPECTED[2], font=body_font, fill="black")
    draw.text((125, 770), EXPECTED[3], font=body_font, fill="black")
    image.save(image_path, format="PNG", optimize=False)

    output = canvas.Canvas(str(pdf_path), pagesize=(PAGE_W, PAGE_H), pageCompression=0)
    output.drawImage(ImageReader(image_path), 0, 0, PAGE_W, PAGE_H, preserveAspectRatio=False)
    output.showPage()
    output.save()


def pdfium_read(path: Path, dll_path: Path, width: int = 1275, height: int = 1650) -> tuple[bytes, str]:
    """Use the pinned product PDFium binary independently to compare pixels and extract hidden text."""
    native = ctypes.CDLL(str(dll_path))
    native.FPDF_InitLibrary.restype = None
    native.FPDF_DestroyLibrary.restype = None
    native.FPDF_LoadDocument.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    native.FPDF_LoadDocument.restype = ctypes.c_void_p
    native.FPDF_CloseDocument.argtypes = [ctypes.c_void_p]
    native.FPDF_GetPageCount.argtypes = [ctypes.c_void_p]
    native.FPDF_GetPageCount.restype = ctypes.c_int
    native.FPDF_LoadPage.argtypes = [ctypes.c_void_p, ctypes.c_int]
    native.FPDF_LoadPage.restype = ctypes.c_void_p
    native.FPDF_ClosePage.argtypes = [ctypes.c_void_p]
    native.FPDFBitmap_Create.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int]
    native.FPDFBitmap_Create.restype = ctypes.c_void_p
    native.FPDFBitmap_Destroy.argtypes = [ctypes.c_void_p]
    native.FPDFBitmap_FillRect.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                                          ctypes.c_int, ctypes.c_int, ctypes.c_uint32]
    native.FPDF_RenderPageBitmap.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int,
                                             ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                             ctypes.c_int, ctypes.c_int]
    native.FPDFBitmap_GetStride.argtypes = [ctypes.c_void_p]
    native.FPDFBitmap_GetStride.restype = ctypes.c_int
    native.FPDFBitmap_GetBuffer.argtypes = [ctypes.c_void_p]
    native.FPDFBitmap_GetBuffer.restype = ctypes.c_void_p
    native.FPDFText_LoadPage.argtypes = [ctypes.c_void_p]
    native.FPDFText_LoadPage.restype = ctypes.c_void_p
    native.FPDFText_ClosePage.argtypes = [ctypes.c_void_p]
    native.FPDFText_CountChars.argtypes = [ctypes.c_void_p]
    native.FPDFText_CountChars.restype = ctypes.c_int
    native.FPDFText_GetText.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                                        ctypes.POINTER(ctypes.c_uint16)]
    native.FPDFText_GetText.restype = ctypes.c_int

    native.FPDF_InitLibrary()
    document = native.FPDF_LoadDocument(str(path).encode("utf-8"), None)
    if not document:
        native.FPDF_DestroyLibrary()
        raise RuntimeError(f"Pinned PDFium could not open {path.name}.")
    page = bitmap = text_page = None
    try:
        if native.FPDF_GetPageCount(document) != 1:
            raise RuntimeError("The OCR proof expects exactly one page.")
        page = native.FPDF_LoadPage(document, 0)
        if not page:
            raise RuntimeError("Pinned PDFium could not load proof page 1.")
        bitmap = native.FPDFBitmap_Create(width, height, 0)
        if not bitmap:
            raise RuntimeError("Pinned PDFium could not create the proof bitmap.")
        native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF)
        native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0)
        stride = native.FPDFBitmap_GetStride(bitmap)
        raw = ctypes.string_at(native.FPDFBitmap_GetBuffer(bitmap), stride * height)

        text_page = native.FPDFText_LoadPage(page)
        count = native.FPDFText_CountChars(text_page)
        if count < 0 or count > 100_000:
            raise RuntimeError("PDF text count exceeded the proof bound.")
        chars = (ctypes.c_uint16 * (count + 1))()
        copied = native.FPDFText_GetText(text_page, 0, count, chars)
        text = "".join(chr(value) for value in chars[:max(0, copied - 1)])
        return raw, text
    finally:
        if text_page:
            native.FPDFText_ClosePage(text_page)
        if bitmap:
            native.FPDFBitmap_Destroy(bitmap)
        if page:
            native.FPDF_ClosePage(page)
        native.FPDF_CloseDocument(document)
        native.FPDF_DestroyLibrary()


def run_tesseract(command: Path, image: Path, timeout: int) -> tuple[str, list[dict]]:
    if not command.is_file():
        raise FileNotFoundError(f"Tesseract runtime not found: {command}")
    version = subprocess.run([str(command), "--version"], check=True, capture_output=True,
                             text=True, timeout=10, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if not version.stdout.startswith("tesseract 5."):
        raise RuntimeError("Only a pinned Tesseract 5.x proof runtime is accepted.")
    languages = subprocess.run([str(command), "--list-langs"], check=True, capture_output=True,
                               text=True, timeout=10, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if "eng" not in languages.stdout.splitlines():
        raise RuntimeError("The local runtime does not include English traineddata (eng).")
    result = subprocess.run([str(command), str(image), "stdout", "-l", "eng", "--psm", "6", "tsv"],
                            check=True, capture_output=True, text=True, timeout=timeout,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    words: list[dict] = []
    for row in csv.DictReader(io.StringIO(result.stdout), delimiter="\t"):
        if row.get("level") != "5" or not row.get("text", "").strip():
            continue
        confidence = float(row.get("conf", "-1"))
        if confidence < 0:
            continue
        x, y, w, h = (int(row[k]) for k in ("left", "top", "width", "height"))
        if w <= 0 or h <= 0 or x < 0 or y < 0 or x + w > PIX_W or y + h > PIX_H:
            raise RuntimeError("Tesseract returned an out-of-bounds word rectangle.")
        words.append({"text": row["text"], "confidence": confidence, "x": x, "y": y, "w": w, "h": h})
    if not words:
        raise RuntimeError("Tesseract returned no bounded word boxes.")
    return version.stdout.splitlines()[0], words


def write_candidate(source_pdf: Path, candidate: Path, words: list[dict]) -> None:
    overlay_bytes = io.BytesIO()
    scale_x, scale_y = PAGE_W / PIX_W, PAGE_H / PIX_H
    page_canvas = canvas.Canvas(overlay_bytes, pagesize=(PAGE_W, PAGE_H), pageCompression=0)
    page_canvas.setFillColorRGB(0, 0, 0)
    for word in words:
        value = word["text"]
        font_size = max(4.0, min(24.0, word["h"] * scale_y * 0.8))
        width = stringWidth(value, "Helvetica", font_size)
        target_width = max(1.0, word["w"] * scale_x)
        horizontal = max(25.0, min(400.0, 100.0 * target_width / max(width, 0.1)))
        text = page_canvas.beginText()
        text.setTextRenderMode(3)  # Invisible searchable OCR layer.
        text.setFont("Helvetica", font_size)
        text.setHorizScale(horizontal)
        x = word["x"] * scale_x
        y = PAGE_H - (word["y"] + word["h"] * 0.8) * scale_y
        text.setTextOrigin(x, y)
        text.textOut(value)
        page_canvas.drawText(text)
    page_canvas.showPage()
    page_canvas.save()
    overlay_bytes.seek(0)

    writer = PdfWriter(clone_from=str(source_pdf))
    if len(writer.pages) != 1:
        raise RuntimeError("The OCR proof is limited to one source page.")
    writer.pages[0].merge_page(PdfReader(overlay_bytes).pages[0], over=True)
    with candidate.open("xb") as output:
        writer.write(output)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tesseract", type=Path, help="Path to a pinned local tesseract.exe; no PATH lookup is used.")
    parser.add_argument("--timeout", type=int, default=60)
    parser.add_argument("--artifact-root", type=Path, default=ROOT / "artifacts")
    args = parser.parse_args()
    if not 1 <= args.timeout <= 300:
        parser.error("--timeout must be between 1 and 300 seconds.")

    artifact = args.artifact_root / f"ocr-proof-{uuid.uuid4().hex}"
    artifact.mkdir(parents=True, exist_ok=False)
    image_path, source_pdf = artifact / "synthetic-scan.png", artifact / "source-scan.pdf"
    candidate = artifact / "searchable-candidate.pdf"
    result_file = artifact / "result.json"
    report: dict = {
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "status": "prepared",
        "scope": "one generated English raster page; no user document or network OCR",
        "limits": {"pixels": PIX_W * PIX_H, "pages": 1, "timeoutSeconds": args.timeout,
                   "maxWordBoxes": 10000},
        "expectedText": EXPECTED,
        "runtime": None,
        "source": None,
        "candidate": None,
        "words": [],
        "checks": {},
        "error": None,
    }
    try:
        create_source(image_path, source_pdf)
        report["source"] = {"path": source_pdf.name, "sha256": sha256(source_pdf),
                            "bytes": source_pdf.stat().st_size,
                            "pages": len(PdfReader(source_pdf).pages),
                            "text": "".join(p.extract_text() or "" for p in PdfReader(source_pdf).pages)}
        command = args.tesseract or Path(os.environ.get("OCR_TESSERACT", ""))
        if not command:
            command = ROOT / "tools" / "ocr-runtime" / "tesseract.exe"
        version, words = run_tesseract(command, image_path, args.timeout)
        if len(words) > 10_000:
            raise RuntimeError("Tesseract word count exceeded the proof bound.")
        report["runtime"] = {"path": str(command), "version": version}
        report["words"] = words
        (artifact / "recognized-words.json").write_text(json.dumps(words, indent=2), encoding="utf-8")
        write_candidate(source_pdf, candidate, words)
        before_pixels, before_text = pdfium_read(source_pdf, ROOT / "native" / "bin" / "pdfium.dll")
        after_pixels, after_text = pdfium_read(candidate, ROOT / "native" / "bin" / "pdfium.dll")
        extracted = "".join(p.extract_text() or "" for p in PdfReader(candidate).pages)
        normalized = " ".join(extracted.split()).lower()
        expected_checks = {value: value.lower() in normalized for value in EXPECTED}
        report["candidate"] = {"path": candidate.name, "sha256": sha256(candidate),
                               "bytes": candidate.stat().st_size}
        report["checks"] = {
            "sourceHasNoTextLayer": not report["source"]["text"].strip(),
            "expectedPhrasesExtracted": expected_checks,
            "pdfiumExtractsOcrText": bool(after_text.strip()),
            "visiblePixelsIdentical": hashlib.sha256(before_pixels).hexdigest() == hashlib.sha256(after_pixels).hexdigest(),
            "sourceHashUnchanged": sha256(source_pdf) == report["source"]["sha256"],
        }
        report["status"] = "passed" if all(report["checks"].values()) else "failed"
        report["pdfiumExtractedText"] = after_text
    except (FileNotFoundError, subprocess.TimeoutExpired) as exc:
        report["status"] = "blocked"
        report["error"] = str(exc)
    except Exception as exc:  # Preserve all diagnostics in the proof artifact.
        report["status"] = "failed"
        report["error"] = f"{type(exc).__name__}: {exc}"
    finally:
        result_file.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"status": report["status"], "artifact": str(artifact), "error": report["error"]}))
    return 0 if report["status"] == "passed" else (2 if report["status"] == "blocked" else 1)


if __name__ == "__main__":
    raise SystemExit(main())
