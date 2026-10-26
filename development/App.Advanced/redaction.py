"""Restricted structural redaction; writes a fresh private PDF candidate.

Unrelated text and vectors remain native PDF objects. Complex inputs are refused,
never flattened. The caller owns preview, final publication and original files.
"""
from __future__ import annotations
import hashlib
import math
import os
from pathlib import Path
import uuid
import pymupdf
from pypdf import PdfReader
from pypdf.generic import ContentStream, NullObject

MAX_BYTES = 128 * 1024 * 1024
BASE_FONTS = {"Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
              "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
              "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique"}

class RedactionRefused(ValueError):
    pass

def _resolve(value):
    return value.get_object() if hasattr(value, "get_object") else value

def _plain_image_colorspace(value):
    value = _resolve(value)
    if value in ("/DeviceRGB", "/DeviceGray"):
        return True
    if isinstance(value, list) and len(value) == 2 and value[0] == "/ICCBased":
        profile = _resolve(value[1])
        # Exact standard RGB profile emitted by the pinned MuPDF build.
        return (set(profile).issubset({"/N", "/Alternate", "/Filter", "/Length"})
            and profile.get("/N") == 3 and profile.get("/Alternate") == "/DeviceRGB"
            and hashlib.sha256(profile.get_data()).hexdigest() == "eddaf344b5edea13269e0d20055f335610e5e0b6e33e6e536f2701bc18c5f7d5")
    return False

def fingerprint(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def _preflight(path):
    if Path(path).stat().st_size > MAX_BYTES:
        raise RedactionRefused("Redaction input exceeds 128 MiB.")
    reader = PdfReader(path, strict=True)
    if reader.is_encrypted or not 1 <= len(reader.pages) <= 1000:
        raise RedactionRefused("Redaction requires an unencrypted PDF of 1–1000 pages.")
    catalog = reader.trailer["/Root"]
    if any(key not in ("/Type", "/Pages", "/Version", "/Metadata", "/Info") for key in catalog):
        raise RedactionRefused("Complex document catalog entries are unsupported for redaction.")
    catalog_info = _resolve(catalog.get("/Info"))
    if catalog_info is not None and not isinstance(catalog_info, NullObject) and catalog_info != {"/Producer": "MuPDF 1.28.2"}:
        raise RedactionRefused("Auxiliary catalog metadata is unsupported for redaction.")
    if any(key in catalog for key in ("/StructTreeRoot", "/MarkInfo", "/OCProperties", "/Perms", "/AcroForm",
        "/Names", "/AF", "/Collection", "/OpenAction", "/AA", "/DSS", "/Threads", "/Outlines")):
        raise RedactionRefused("Forms, signatures, tags, layers, attachments, actions and bookmarks are unsupported for redaction.")
    for page in reader.pages:
        if any(key in page for key in ("/Annots", "/AA", "/Metadata", "/PieceInfo", "/Thumb", "/PresSteps", "/B")):
            raise RedactionRefused("Annotations, links, actions and auxiliary page content are unsupported for redaction.")
        if page.rotation != 0 or list(page.cropbox) != list(page.mediabox) or list(page.mediabox)[:2] != [0, 0]:
            raise RedactionRefused("Redaction currently requires unrotated pages with matching crop and media boxes at the origin.")
        resources = _resolve(page.get("/Resources", {}))
        if any(key not in ("/Font", "/XObject", "/ProcSet") for key in resources):
            raise RedactionRefused("Complex page resources are unsupported for redaction.")
        for font_ref in _resolve(resources.get("/Font", {})).values():
            font = font_ref.get_object()
            if (font.get("/Subtype") != "/Type1" or str(font.get("/BaseFont", ""))[1:] not in BASE_FONTS
                or font.get("/Encoding", "/StandardEncoding") not in ("/StandardEncoding", "/WinAnsiEncoding")
                or any(key not in ("/Type", "/Subtype", "/BaseFont", "/Encoding", "/Name") for key in font)):
                raise RedactionRefused("This redaction version supports standard Latin PDF fonts only.")
        for image_ref in _resolve(resources.get("/XObject", {})).values():
            image = image_ref.get_object()
            if (image.get("/Subtype") != "/Image" or not _plain_image_colorspace(image.get("/ColorSpace"))
                or image.get("/BitsPerComponent") != 8 or image.get("/Filter") not in ("/FlateDecode", "/DCTDecode")
                or any(key not in ("/Type", "/Subtype", "/Filter", "/Width", "/Height", "/BitsPerComponent", "/ColorSpace", "/Length", "/DecodeParms", "/Interpolate") for key in image)):
                raise RedactionRefused("Form objects and complex images are unsupported for redaction.")
            parameters = _resolve(image.get("/DecodeParms"))
            if parameters is not None and (not isinstance(parameters, dict) or any(key not in
                ("/Predictor", "/Colors", "/Columns", "/BitsPerComponent") for key in parameters)):
                raise RedactionRefused("Complex image decode parameters are unsupported for redaction.")
        if page.get_contents() is not None:
            for _, operator in ContentStream(page.get_contents(), reader).operations:
                if operator in (b"BMC", b"BDC", b"EMC", b"MP", b"DP", b"INLINE IMAGE"):
                    raise RedactionRefused("Marked content and inline images are unsupported for redaction.")
    with pymupdf.open(path) as doc:
        image_uses = {}
        for page in doc:
            if page.get_xobjects() or page.get_links() or list(page.widgets() or []) or list(page.annots() or []):
                raise RedactionRefused("Interactive structures and Form objects are unsupported for redaction.")
            for item in page.get_image_info(xrefs=True):
                xref = item["xref"]
                image_uses[xref] = image_uses.get(xref, 0) + 1
        if any(count != 1 for count in image_uses.values()):
            raise RedactionRefused("Shared or repeatedly drawn images are unsupported for redaction.")
    return len(reader.pages)

def redact_candidate(source_path, output_path, regions, *, cancelled=lambda: False, before_publish=lambda: None):
    source = Path(source_path).resolve(strict=True)
    output = Path(output_path).absolute()
    if source == output.resolve() or output.exists() or not output.parent.is_dir():
        raise RedactionRefused("Choose a separate new redaction candidate path.")
    if not isinstance(regions, list) or not 1 <= len(regions) <= 128:
        raise RedactionRefused("Provide 1–128 redaction rectangles.")
    source_hash = fingerprint(source)
    page_count = _preflight(source)
    selections = {}
    total_area = 0
    with pymupdf.open(source) as doc:
        for region in regions:
            if not isinstance(region, dict) or set(region) != {"Page", "Left", "Top", "Right", "Bottom"}:
                raise RedactionRefused("A redaction rectangle is malformed.")
            page = region["Page"]
            coords = [region[key] for key in ("Left", "Top", "Right", "Bottom")]
            if (not isinstance(page, int) or isinstance(page, bool) or not 0 <= page < page_count
                or any(not isinstance(v, (int, float)) or isinstance(v, bool) or not math.isfinite(v) for v in coords)):
                raise RedactionRefused("A redaction rectangle is outside the supported bounds.")
            rect = pymupdf.Rect(coords)
            if rect.is_empty or not doc[page].rect.contains(rect):
                raise RedactionRefused("Redaction rectangles must be nonempty and inside the page.")
            total_area += rect.get_area()
            if total_area > 8_000_000:
                raise RedactionRefused("Combined redaction area exceeds the supported limit.")
            selections.setdefault(page, []).append(rect)
    temporary = output.parent / (".redaction-" + uuid.uuid4().hex + ".pdf")
    try:
        if cancelled(): raise InterruptedError("Redaction cancelled.")
        with pymupdf.open(source) as doc:
            for number, rects in selections.items():
                page = doc[number]
                for rect in rects: page.add_redact_annot(rect, fill=(1, 1, 1), cross_out=False)
                page.apply_redactions(images=pymupdf.PDF_REDACT_IMAGE_PIXELS,
                    graphics=pymupdf.PDF_REDACT_LINE_ART_REMOVE_IF_TOUCHED, text=pymupdf.PDF_REDACT_TEXT_REMOVE)
            doc.set_metadata({}); doc.set_xml_metadata("")
            doc.xref_set_key(doc.pdf_catalog(), "Metadata", "null")
            doc.xref_set_key(doc.pdf_catalog(), "Info", "null")
            doc.xref_set_key(-1, "ID", "null")
            doc.save(temporary, garbage=4, deflate=True, clean=True, incremental=False)
        # Require a fresh full rewrite, independently parsed pages and stripped metadata.
        candidate = PdfReader(temporary, strict=True)
        root = candidate.trailer["/Root"]
        metadata = _resolve(root.get("/Metadata"))
        catalog_info = _resolve(root.get("/Info"))
        if (candidate.is_encrypted or len(candidate.pages) != page_count or "/Prev" in candidate.trailer
            or metadata is not None and not isinstance(metadata, NullObject)
            or catalog_info is not None and not isinstance(catalog_info, NullObject)
            or candidate.metadata and any(candidate.metadata.values())):
            raise RedactionRefused("Redaction output failed structural or metadata validation.")
        if temporary.stat().st_size > MAX_BYTES * 2:
            raise RedactionRefused("Redaction output exceeds the size limit.")
        with pymupdf.open(temporary) as check:
            for number, rects in selections.items():
                for span in check[number].get_texttrace():
                    for char in span["chars"]:
                        bounds = pymupdf.Rect(char[3])
                        if any(not (bounds & rect).is_empty for rect in rects):
                            raise RedactionRefused("Text remains inside a redaction region; no output accepted.")
        with temporary.open("r+b") as stream: stream.flush(); os.fsync(stream.fileno())
        before_publish()
        if cancelled(): raise InterruptedError("Redaction cancelled.")
        if fingerprint(source) != source_hash:
            raise RedactionRefused("The source changed during redaction processing.")
        result = {"Applied": True, "OutputSHA256": fingerprint(temporary), "PageCount": page_count,
                  "MetadataRemoved": True, "TextAndVectorsPreserved": True}
        os.link(temporary, output)
        return result
    finally:
        try: temporary.unlink(missing_ok=True)
        except OSError: pass
