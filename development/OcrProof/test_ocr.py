from __future__ import annotations

import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "App.Advanced"))
sys.path.insert(0, str(ROOT / "tools" / "python-packages"))
sys.path.insert(0, str(ROOT / "tools" / "redaction-pymupdf"))
from ocr import MAX_INPUT_BYTES, OcrCanceled, OcrRefused, SourceChanged, ocr_scan as _ocr_scan
from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, BooleanObject, DictionaryObject, NameObject, NumberObject
import pymupdf


SOURCE = ROOT / "fixtures" / "corpus-v1" / "edge" / "scan-real-text.pdf"
PDFIUM = ROOT / "native" / "bin" / "pdfium.dll"
TRAINEDDATA = ROOT / "tools" / "ocr-runtime" / "eng.traineddata"


def ocr_scan(input_path, candidate_path, **kwargs):
    """Proof-only dependency shim; product callers supply these trusted paths."""
    return _ocr_scan(input_path, candidate_path, traineddata_path=TRAINEDDATA,
                     pdfium_library=PDFIUM, **kwargs)


def sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


class Pdfium:
    """Independent render check using the application's pinned PDFium build."""
    def __init__(self, library: Path):
        self.dll = ctypes.WinDLL(str(library))
        self._set("FPDF_InitLibrary", [], None)
        self._set("FPDF_LoadDocument", [ctypes.c_char_p, ctypes.c_char_p], ctypes.c_void_p)
        self._set("FPDF_GetPageCount", [ctypes.c_void_p], ctypes.c_int)
        self._set("FPDF_LoadPage", [ctypes.c_void_p, ctypes.c_int], ctypes.c_void_p)
        self._set("FPDF_GetPageWidthF", [ctypes.c_void_p], ctypes.c_float)
        self._set("FPDF_GetPageHeightF", [ctypes.c_void_p], ctypes.c_float)
        self._set("FPDF_RenderPageBitmap", [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int,
               ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int], None)
        self._set("FPDFBitmap_CreateEx", [ctypes.c_int, ctypes.c_int, ctypes.c_int,
               ctypes.c_void_p, ctypes.c_int], ctypes.c_void_p)
        self._set("FPDFBitmap_FillRect", [ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
               ctypes.c_int, ctypes.c_int, ctypes.c_uint32], ctypes.c_int)
        self._set("FPDFBitmap_GetBuffer", [ctypes.c_void_p], ctypes.c_void_p)
        self._set("FPDFBitmap_GetStride", [ctypes.c_void_p], ctypes.c_int)
        self._set("FPDFBitmap_Destroy", [ctypes.c_void_p], None)
        self._set("FPDF_ClosePage", [ctypes.c_void_p], None)
        self._set("FPDF_CloseDocument", [ctypes.c_void_p], None)
        self.dll.FPDF_InitLibrary()

    def _set(self, name, argtypes, restype):
        function = getattr(self.dll, name)
        function.argtypes = argtypes
        function.restype = restype

    def render(self, path: Path) -> tuple[tuple[float, float], bytes]:
        self.dll.FPDF_InitLibrary()
        document = self.dll.FPDF_LoadDocument(os.fsencode(path.resolve()), None)
        if not document:
            raise AssertionError(f"PDFium could not open {path.name}")
        try:
            if self.dll.FPDF_GetPageCount(document) != 1:
                raise AssertionError("PDFium page count mismatch")
            page = self.dll.FPDF_LoadPage(document, 0)
            if not page:
                raise AssertionError("PDFium could not load first page")
            try:
                size = (float(self.dll.FPDF_GetPageWidthF(page)), float(self.dll.FPDF_GetPageHeightF(page)))
                width, height = (round(v * 300 / 72) for v in size)
                if width * height > 12_000_000:
                    raise AssertionError("Test render exceeded its bounded raster size")
                bitmap = self.dll.FPDFBitmap_CreateEx(width, height, 4, None, 0)  # BGRA
                if not bitmap:
                    raise AssertionError("PDFium bitmap allocation failed")
                try:
                    self.dll.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF)
                    self.dll.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0)
                    stride = self.dll.FPDFBitmap_GetStride(bitmap)
                    raw = ctypes.string_at(self.dll.FPDFBitmap_GetBuffer(bitmap), stride * height)
                    # Strip row padding while keeping the BGRA channels for a stable renderer-to-renderer compare.
                    row_size = width * 4
                    pixels = b"".join(raw[y * stride:y * stride + row_size] for y in range(height))
                    return size, pixels
                finally:
                    self.dll.FPDFBitmap_Destroy(bitmap)
            finally:
                self.dll.FPDF_ClosePage(page)
        finally:
            self.dll.FPDF_CloseDocument(document)


class OcrProof(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.name != "nt" or not PDFIUM.is_file():
            raise unittest.SkipTest("The pinned Windows PDFium build is not available")
        cls.pdfium = Pdfium(PDFIUM)
        cls.work = ROOT / "artifacts" / f"ocr-validation-{uuid.uuid4()}"
        cls.work.mkdir(parents=True)
        cls.source_copy = cls.work / "source-copy.pdf"
        shutil.copyfile(SOURCE, cls.source_copy)
        cls.original_hash = sha(SOURCE)
        cls.copy_hash = sha(cls.source_copy)
        cls.candidate = cls.work / "candidate.pdf"
        cls.report = ocr_scan(cls.source_copy, cls.candidate)

    def test_candidate_is_searchable_and_preserves_metadata_geometry_and_source(self):
        self.assertEqual(sha(SOURCE), self.original_hash)
        self.assertEqual(sha(self.source_copy), self.copy_hash)
        self.assertEqual(self.report["sourceSha256"], self.copy_hash)
        with self.candidate.open("rb") as stream:
            reader = PdfReader(stream, strict=True)
            self.assertEqual(len(reader.pages), 1)
            text = reader.pages[0].extract_text() or ""
            self.assertIn("Synthetic scanned text", text)
            self.assertIn("Invoice number 12345", text)
            self.assertIn("Total amount 678.90", text)
            self.assertIn("No hidden searchable text", text)
            source_reader = PdfReader(str(SOURCE), strict=True)
            self.assertEqual(reader.metadata, source_reader.metadata)
            self.assertEqual(float(reader.pages[0].mediabox.width), 612)
            self.assertEqual(float(reader.pages[0].mediabox.height), 792)
        self.assertEqual(self.report["pageCount"], 1)
        self.assertTrue(self.report["visiblePixelsPreserved"])
        self.assertTrue(self.report["metadataPreserved"])
        self.assertEqual(sha(self.candidate), self.report["candidateSha256"])

    def test_independent_pdfium_render_is_pixel_identical_and_invisible_text_does_not_paint(self):
        source_size, source_pixels = self.pdfium.render(SOURCE)
        candidate_size, candidate_pixels = self.pdfium.render(self.candidate)
        self.assertEqual(source_size, candidate_size)
        self.assertEqual(source_pixels, candidate_pixels)
        with pymupdf.open(self.candidate) as doc:
            trace = doc[0].get_texttrace()
            self.assertTrue(trace)
            self.assertTrue(all(span["type"] == 3 for span in trace))

    def test_input_rejections(self):
        cases = {
            "multi-page": ROOT / "fixtures" / "corpus-v1" / "engine" / "original.pdf",
            "digital-text": ROOT / "fixtures" / "corpus-v1" / "engine" / "page-source.pdf",
            "signed": ROOT / "fixtures" / "corpus-v1" / "edge" / "signed.pdf",
            "encrypted": ROOT / "fixtures" / "corpus-v1" / "compatibility" / "password-aes256.pdf",
        }
        for label, path in cases.items():
            with self.subTest(label=label):
                dest = self.work / f"reject-{label}.pdf"
                with self.assertRaises(OcrRefused):
                    ocr_scan(path, dest)
                self.assertFalse(dest.exists())

    def test_candidate_is_create_only_and_input_is_required_unchanged(self):
        collision = self.work / "already-there.pdf"
        collision.write_bytes(b"keep-existing")
        with self.assertRaises(FileExistsError):
            ocr_scan(self.source_copy, collision)
        self.assertEqual(collision.read_bytes(), b"keep-existing")
        with self.assertRaises(OcrRefused):
            ocr_scan(self.source_copy, self.source_copy)
        self.assertEqual(sha(self.source_copy), self.copy_hash)

    def test_cancellation_before_and_after_candidate_validation_never_publishes(self):
        before = self.work / "cancel-before.pdf"
        with self.assertRaises(OcrCanceled):
            ocr_scan(self.source_copy, before, cancelled=lambda: True)
        self.assertFalse(before.exists())
        after = self.work / "cancel-after.pdf"
        state = {"cancel": False}
        def cancel_after_validation():
            state["cancel"] = True
        with self.assertRaises(OcrCanceled):
            ocr_scan(self.source_copy, after, cancelled=lambda: state["cancel"], before_publish=cancel_after_validation)
        self.assertFalse(after.exists())
        self.assertEqual(sha(self.source_copy), self.copy_hash)

    def test_input_change_before_publication_is_detected(self):
        changed = self.work / "mutable-copy.pdf"
        shutil.copyfile(SOURCE, changed)
        dest = self.work / "changed-source-candidate.pdf"
        def mutate_disposable_input():
            changed.write_bytes(b"mutated copy")
        with self.assertRaises(SourceChanged):
            ocr_scan(changed, dest, before_publish=mutate_disposable_input)
        self.assertFalse(dest.exists())

    def test_invalid_and_oversized_inputs_are_refused_without_output(self):
        invalid = self.work / "invalid.pdf"
        invalid.write_bytes(b"not a pdf")
        with self.assertRaises(OcrRefused):
            ocr_scan(invalid, self.work / "invalid-output.pdf")
        oversized = self.work / "oversized.pdf"
        with oversized.open("wb") as stream:
            stream.truncate(MAX_INPUT_BYTES + 1)
        with self.assertRaises(OcrRefused):
            ocr_scan(oversized, self.work / "oversized-output.pdf")

    def test_unsupported_catalog_structures_and_annotation_are_refused(self):
        for label, key in (("form", "/AcroForm"), ("layer", "/OCProperties"),
                           ("tags", "/StructTreeRoot"), ("attachment", "/Names"),
                           ("action", "/OpenAction")):
            with self.subTest(label=label):
                path = self.work / f"{label}.pdf"
                reader = PdfReader(str(SOURCE), strict=True)
                writer = PdfWriter(clone_from=reader)
                writer.root_object[NameObject(key)] = BooleanObject(True)
                with path.open("wb") as stream:
                    writer.write(stream)
                with self.assertRaises(OcrRefused):
                    ocr_scan(path, self.work / f"{label}-output.pdf")
        annot = self.work / "annotated.pdf"
        reader = PdfReader(str(SOURCE), strict=True)
        writer = PdfWriter(clone_from=reader)
        annotation = DictionaryObject({
            NameObject("/Type"): NameObject("/Annot"),
            NameObject("/Subtype"): NameObject("/Text"),
            NameObject("/Rect"): ArrayObject([NumberObject(0), NumberObject(0), NumberObject(20), NumberObject(20)]),
        })
        writer.pages[0][NameObject("/Annots")] = ArrayObject([writer._add_object(annotation)])
        with annot.open("wb") as stream:
            writer.write(stream)
        with self.assertRaises(OcrRefused):
            ocr_scan(annot, self.work / "annotated-output.pdf")

    def test_non_image_page_content_and_raster_cap_are_refused(self):
        text_path = self.work / "with-text.pdf"
        with pymupdf.open(SOURCE) as doc:
            doc[0].insert_text((30, 30), "Not a scan-only page")
            doc.save(text_path)
        with self.assertRaises(OcrRefused):
            ocr_scan(text_path, self.work / "text-output.pdf")
        large_path = self.work / "large-raster.pdf"
        with pymupdf.open(SOURCE) as source_doc:
            source_pix = pymupdf.Pixmap(source_doc, source_doc[0].get_images(full=True)[0][0])
            image_path = self.work / "scan.png"
            source_pix.save(image_path)
        with pymupdf.open() as doc:
            page = doc.new_page(width=734.4, height=950.4)
            page.insert_image(page.rect, filename=str(image_path))
            doc.save(large_path)
        with self.assertRaises(OcrRefused):
            ocr_scan(large_path, self.work / "large-output.pdf")

    @classmethod
    def tearDownClass(cls):
        if hasattr(cls, "candidate") and cls.candidate.exists():
            with pymupdf.open(cls.candidate) as doc:
                (cls.work / "recognized.txt").write_text(doc[0].get_text(sort=True), encoding="utf-8")
                words = doc[0].get_text("words", sort=True)
                word_boxes = [
                    {"x0": w[0], "y0": w[1], "x1": w[2], "y1": w[3], "text": w[4],
                     "block": w[5], "line": w[6], "word": w[7]}
                    for w in words
                ]
                (cls.work / "word-boxes.json").write_text(json.dumps(word_boxes, indent=2), encoding="utf-8")
            cls.report["wordBoxCount"] = len(word_boxes)
            cls.report["evidenceDirectory"] = str(cls.work)
            (cls.work / "result.json").write_text(json.dumps(cls.report, indent=2), encoding="utf-8")
            print(f"OCR proof evidence: {cls.work}")


if __name__ == "__main__":
    unittest.main(verbosity=2)
