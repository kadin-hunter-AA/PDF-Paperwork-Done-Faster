"""Narrow local OCR transform for small image-only PDF scan documents.

This module writes only a private candidate path supplied by the session. It
never chooses or publishes a user-facing output path. Product use remains gated
on review of the staged PyMuPDF licensing and package lifecycle.
"""
from __future__ import annotations

import hashlib
import ctypes
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from typing import Callable


import pymupdf  # noqa: E402
from pypdf import PdfReader, PdfWriter  # noqa: E402
from pypdf.generic import ContentStream  # noqa: E402


MAX_INPUT_BYTES = 128 * 1024 * 1024
MAX_OUTPUT_BYTES = 160 * 1024 * 1024
MAX_RASTER_PIXELS = 12_000_000
MAX_DOCUMENT_RASTER_PIXELS = 24_000_000
MAX_PAGES = 50
OCR_DPI = 300
EXPECTED_PYMUPDF = "1.28.2"
EXPECTED_ENG_SHA256 = "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"
ENG_SOURCE_COMMIT = "87416418657359cb625c412a48b6e1d6d41c29bd"
FORBIDDEN_CATALOG_KEYS = {
    "/AcroForm", "/AF", "/Collection", "/Metadata", "/Names",
    "/Lang", "/MarkInfo", "/OCProperties", "/OpenAction", "/Outlines",
    "/PageLabels", "/PageLayout", "/Perms", "/DSS", "/StructTreeRoot",
    "/Threads", "/ViewerPreferences",
}


class OcrRefused(ValueError):
    """The input is outside this narrow, safe OCR proof subset."""


class OcrCanceled(RuntimeError):
    """The transform was canceled before candidate acceptance."""


class SourceChanged(RuntimeError):
    """The disposable input changed while OCR was running."""


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _check_cancel(cancelled: Callable[[], bool]) -> None:
    if cancelled():
        raise OcrCanceled("OCR was canceled; no candidate was accepted")


def _same_size(left: tuple[float, float], right: tuple[float, float]) -> bool:
    return abs(left[0] - right[0]) <= 0.01 and abs(left[1] - right[1]) <= 0.01


def _has_forbidden_catalog(reader: PdfReader) -> bool:
    root = reader.trailer.get("/Root")
    return root is None or any(key in root for key in FORBIDDEN_CATALOG_KEYS)


def _validate_input(path: Path):
    if pymupdf.__version__ != EXPECTED_PYMUPDF:
        raise OcrRefused("The local OCR engine version is not the proof-pinned version")
    if path.is_symlink() or not path.is_file():
        raise OcrRefused("OCR requires a regular PDF file")
    if path.stat().st_size <= 0 or path.stat().st_size > MAX_INPUT_BYTES:
        raise OcrRefused("The PDF is empty or exceeds the OCR input size limit")
    try:
        reader = PdfReader(str(path), strict=True)
        if reader.is_encrypted:
            raise OcrRefused("Password-protected PDFs are not supported by this OCR operation")
        if not 1 <= len(reader.pages) <= MAX_PAGES:
            raise OcrRefused(f"This OCR operation supports between one and {MAX_PAGES} pages")
        if _has_forbidden_catalog(reader):
            raise OcrRefused("The PDF contains forms, attachments, layers, tags, actions, or other unsupported document structures")
        root = reader.trailer["/Root"]
        if root.get("/PageMode") not in (None, "/UseNone"):
            raise OcrRefused("PDFs with a non-default page mode are not supported by this OCR operation")
        page_extras = ("/Annots", "/AA", "/StructParents", "/Metadata", "/PieceInfo", "/Group",
                       "/Thumb", "/AF", "/Dur", "/Trans", "/PresSteps", "/LastModified")
        for page_index, page_obj in enumerate(reader.pages):
            label = f"Page {page_index + 1}: "
            if any(page_obj.get(key) is not None and not (key == "/Trans" and not page_obj.get(key))
                   for key in page_extras):
                raise OcrRefused(label + "annotations, actions, or tagged content are not supported")
            if page_obj.get("/UserUnit") not in (None, 1, 1.0):
                raise OcrRefused(label + "non-default user units are not supported")
            for box_name in ("/TrimBox", "/BleedBox", "/ArtBox"):
                box = page_obj.get(box_name)
                if box is not None and list(box) != list(page_obj.mediabox):
                    raise OcrRefused(label + "specialized print boxes are not supported")
            resources = page_obj.get("/Resources", {})
            xobjects = resources.get("/XObject", {})
            if len(xobjects) != 1 or next(iter(xobjects.values())).get_object().get("/Subtype") != "/Image":
                raise OcrRefused(label + "must contain exactly one scan image and no other XObjects")
            image_object = next(iter(xobjects.values())).get_object()
            if any(image_object.get(key) is not None for key in
                   ("/Mask", "/SMask", "/SMaskInData", "/Decode", "/Matte", "/OC", "/Alternates")):
                raise OcrRefused(label + "masked, layered, or specially decoded scan images are unsupported")
            contents = page_obj.get("/Contents")
            if contents is None:
                raise OcrRefused(label + "has no scan image content")
            content = ContentStream(contents, reader)
            try:
                operators = [operator.decode("ascii") for _, operator in content.operations]
            except UnicodeDecodeError as exc:
                raise OcrRefused(label + "contains malformed content operators") from exc
            if any(operator not in {"q", "Q", "cm", "Do", "BT", "ET", "Tf", "TL"} for operator in operators):
                raise OcrRefused(label + "contains unsupported drawing, text, clipping, or graphics-state operators")
            if operators.count("Do") != 1:
                raise OcrRefused(label + "must paint exactly one scan image")
        if reader.get_fields():
            raise OcrRefused("Interactive forms are not supported by this OCR operation")
        if getattr(reader, "embedded_signatures", []):
            raise OcrRefused("Signed PDFs are not supported by this OCR operation")
        with pymupdf.open(path) as doc:
            if doc.is_encrypted or not 1 <= doc.page_count <= MAX_PAGES or doc.page_count != len(reader.pages):
                raise OcrRefused("The encrypted or oversized document has an unsupported page count")
            if doc.embfile_count() != 0 or doc.get_toc() or doc.get_ocgs():
                raise OcrRefused("PDFs with attachments, outlines, or optional-content layers are not supported")
            if doc.get_xml_metadata():
                raise OcrRefused("PDFs with XMP metadata are not supported by this OCR operation")
            page_data = []
            total_raster_pixels = 0
            for page_index, page in enumerate(doc):
                label = f"Page {page_index + 1}: "
                if page.rotation != 0 or page.rect != page.mediabox or page.rect != page.cropbox:
                    raise OcrRefused(label + "rotated or cropped pages are unsupported")
                if (page.get_text().strip() or page.get_texttrace() or page.get_drawings()
                        or page.get_links() or list(page.annots() or [])):
                    raise OcrRefused(label + "must contain only a scan image, with no text, drawings, links, or annotations")
                images = page.get_images(full=True)
                if len(images) != 1:
                    raise OcrRefused(label + "must contain exactly one full-page scan image")
                image = images[0]
                if (image[1] != 0 or image[4] != 8 or image[5] not in {"DeviceRGB", "DeviceGray"}
                        or image[2] <= 0 or image[3] <= 0 or image[2] * image[3] > MAX_RASTER_PIXELS):
                    raise OcrRefused(label + "scan must be an opaque RGB or grayscale image with 8 bits per channel")
                image_rects = page.get_image_rects(image[0], transform=True)
                if len(image_rects) != 1 or image_rects[0][0].irect != page.rect.irect:
                    raise OcrRefused(label + "scan image must cover the full page without clipping")
                transform = image_rects[0][1]
                if abs(transform.b) > 0.001 or abs(transform.c) > 0.001 or transform.a <= 0 or transform.d <= 0:
                    raise OcrRefused(label + "rotated, reflected, or skewed scan placement is unsupported")
                image_pix = pymupdf.Pixmap(doc, image[0])
                if (image_pix.alpha or image_pix.colorspace is None
                        or image_pix.colorspace.name not in {"DeviceRGB", "DeviceGray"}):
                    raise OcrRefused(label + "scan must not have transparency or an unsupported color model")
                page_size = (float(page.rect.width), float(page.rect.height))
                native_dpi_x = image_pix.width * 72.0 / page_size[0]
                native_dpi_y = image_pix.height * 72.0 / page_size[1]
                if (native_dpi_x < 36 or native_dpi_x > 600 or native_dpi_y < 36 or native_dpi_y > 600
                        or abs(native_dpi_x - round(native_dpi_x)) > 0.01
                        or abs(native_dpi_y - round(native_dpi_y)) > 0.01
                        or abs(native_dpi_x - native_dpi_y) > 0.01):
                    raise OcrRefused(label + "scan resolution does not map cleanly to page geometry")
                image_pix.set_dpi(round(native_dpi_x), round(native_dpi_y))
                raster_width = round(page_size[0] * OCR_DPI / 72)
                raster_height = round(page_size[1] * OCR_DPI / 72)
                if raster_width <= 0 or raster_height <= 0 or raster_width * raster_height > MAX_RASTER_PIXELS:
                    raise OcrRefused(label + "300 dpi page raster exceeds the pixel limit")
                total_raster_pixels += image_pix.width * image_pix.height
                if total_raster_pixels > MAX_DOCUMENT_RASTER_PIXELS:
                    raise OcrRefused(f"The document exceeds the {MAX_DOCUMENT_RASTER_PIXELS:,}-pixel OCR processing limit")
                page_data.append((page_size, image_pix, native_dpi_x))
            source_info = reader.trailer.raw_get("/Info")
        return page_data, source_info
    except OcrRefused:
        raise
    except Exception as exc:
        raise OcrRefused("The PDF could not be safely inspected for OCR") from exc


def _verify_pdfium_pixels(source: Path, candidate: Path,
                          expected_pages: list[tuple[int, int, tuple[float, float]]], library: Path) -> None:
    """Compare both files with the independent PDFium renderer shipped by the app."""
    if os.name != "nt":
        raise OcrRefused("Independent Windows PDFium validation is unavailable")
    library = Path(library).resolve(strict=True)
    if not library.is_file() or library.suffix.lower() != ".dll":
        raise OcrRefused("Independent PDFium validation is unavailable")
    try:
        dll_directory = os.add_dll_directory(str(library.parent))
    except (AttributeError, OSError):
        dll_directory = None
    try:
        pdfium = ctypes.WinDLL(str(library))

        def api(name: str, args: list, result):
            fn = getattr(pdfium, name)
            fn.argtypes, fn.restype = args, result
            return fn

        init = api("FPDF_InitLibrary", [], None)
        load = api("FPDF_LoadDocument", [ctypes.c_char_p, ctypes.c_char_p], ctypes.c_void_p)
        count = api("FPDF_GetPageCount", [ctypes.c_void_p], ctypes.c_int)
        load_page = api("FPDF_LoadPage", [ctypes.c_void_p, ctypes.c_int], ctypes.c_void_p)
        page_width = api("FPDF_GetPageWidthF", [ctypes.c_void_p], ctypes.c_float)
        page_height = api("FPDF_GetPageHeightF", [ctypes.c_void_p], ctypes.c_float)
        create = api("FPDFBitmap_CreateEx", [ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_int], ctypes.c_void_p)
        fill = api("FPDFBitmap_FillRect", [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_uint32], ctypes.c_int)
        render = api("FPDF_RenderPageBitmap", [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int], None)
        buffer = api("FPDFBitmap_GetBuffer", [ctypes.c_void_p], ctypes.c_void_p)
        stride = api("FPDFBitmap_GetStride", [ctypes.c_void_p], ctypes.c_int)
        destroy_bitmap = api("FPDFBitmap_Destroy", [ctypes.c_void_p], None)
        close_page = api("FPDF_ClosePage", [ctypes.c_void_p], None)
        close_doc = api("FPDF_CloseDocument", [ctypes.c_void_p], None)
        destroy = api("FPDF_DestroyLibrary", [], None)
        init()

        def render_pdf(path: Path) -> list[tuple[tuple[float, float], bytes]]:
            doc = load(os.fsencode(path.resolve()), None)
            if not doc:
                raise OcrRefused("PDFium could not independently inspect the OCR candidate")
            try:
                if count(doc) != len(expected_pages):
                    raise OcrRefused("PDFium reported an unexpected OCR page count")
                rendered_pages = []
                for page_index, (width, height, page_size) in enumerate(expected_pages):
                    page = load_page(doc, page_index)
                    if not page:
                        raise OcrRefused(f"PDFium could not load OCR page {page_index + 1}")
                    try:
                        size = (float(page_width(page)), float(page_height(page)))
                        if not _same_size(size, page_size):
                            raise OcrRefused(f"PDFium reported changed geometry on OCR page {page_index + 1}")
                        bitmap = create(width, height, 4, None, 0)  # BGRA
                        if not bitmap:
                            raise OcrRefused("PDFium could not allocate the bounded OCR validation bitmap")
                        try:
                            fill(bitmap, 0, 0, width, height, 0xFFFFFFFF)
                            render(bitmap, page, 0, 0, width, height, 0, 0)
                            row_bytes, line_bytes = width * 4, stride(bitmap)
                            raw = ctypes.string_at(buffer(bitmap), line_bytes * height)
                            pixels = b"".join(raw[y * line_bytes:y * line_bytes + row_bytes] for y in range(height))
                            rendered_pages.append((size, pixels))
                        finally:
                            destroy_bitmap(bitmap)
                    finally:
                        close_page(page)
                return rendered_pages
            finally:
                close_doc(doc)

        try:
            if render_pdf(source) != render_pdf(candidate):
                raise OcrRefused("The OCR candidate changed visible pixels in independent PDFium rendering")
        finally:
            destroy()
    except OcrRefused:
        raise
    except Exception as exc:
        raise OcrRefused("Independent PDFium validation failed") from exc
    finally:
        if dll_directory is not None:
            dll_directory.close()


def ocr_scan(input_path: str | os.PathLike[str], candidate_path: str | os.PathLike[str], *,
             traineddata_path: str | os.PathLike[str],
             pdfium_library: str | os.PathLike[str],
             cancelled: Callable[[], bool] = lambda: False,
             before_publish: Callable[[], None] = lambda: None) -> dict:
    """Create a searchable private candidate from strictly image-only scan pages.

    `candidate_path` must be a new path reserved by the calling document session.
    This function never replaces an existing path or edits `input_path`.
    """
    supplied_source = Path(input_path).absolute()
    try:
        attributes = supplied_source.lstat().st_file_attributes
    except AttributeError:
        attributes = 0
    if supplied_source.is_symlink() or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise OcrRefused("OCR requires a regular, non-reparse input file")
    source = supplied_source.resolve(strict=True)
    destination = Path(candidate_path).absolute()
    resolved_destination = destination.resolve(strict=False)
    if (source == resolved_destination
            or os.path.normcase(str(source)) == os.path.normcase(str(resolved_destination))):
        raise OcrRefused("The OCR candidate must be separate from its input")
    if destination.exists():
        raise FileExistsError("The OCR candidate path already exists")
    if not destination.parent.is_dir():
        raise FileNotFoundError("The OCR candidate folder does not exist")
    try:
        destination_attributes = destination.lstat().st_file_attributes
    except (AttributeError, FileNotFoundError):
        destination_attributes = 0
    if destination.is_symlink() or destination_attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400):
        raise OcrRefused("The OCR candidate cannot be a symbolic link or reparse point")
    original_hash = sha256(source)
    pages, source_info = _validate_input(source)
    traineddata = Path(traineddata_path).resolve(strict=True)
    if not traineddata.is_file() or sha256(traineddata) != EXPECTED_ENG_SHA256:
        raise OcrRefused("The pinned local English OCR model is missing or failed its integrity check")
    # Tesseract appends a forward slash to this folder. Windows extended-path
    # syntax cannot accept that mixed separator, unlike Python's file APIs.
    data_folder = str(traineddata.parent)
    if os.name == "nt":
        if data_folder.startswith("\\\\?\\UNC\\"):
            data_folder = "\\\\" + data_folder[8:]
        elif data_folder.startswith("\\\\?\\"):
            data_folder = data_folder[4:]
        if len(data_folder) + len("/eng.traineddata") >= 260:
            raise OcrRefused("The installed OCR model path is too long for the local recognition engine")
    _check_cancel(cancelled)

    temp_name: str | None = None
    try:
        expected_pages = [(pix.width, pix.height, page_size) for page_size, pix, _ in pages]
        # OCR each decoded scan independently, then append its one-page result in
        # source order. The preflight has already rejected mixed-content pages.
        with pymupdf.open() as combined:
            for _, pix, _ in pages:
                if pix.width * pix.height > MAX_RASTER_PIXELS:
                    raise OcrRefused("A source image exceeds the OCR pixel limit")
                _check_cancel(cancelled)
                data = pix.pdfocr_tobytes(language="eng", tessdata=data_folder)
                _check_cancel(cancelled)
                with pymupdf.open(stream=data, filetype="pdf") as page_candidate:
                    if page_candidate.page_count != 1:
                        raise OcrRefused("The OCR engine returned an unexpected page count")
                    combined.insert_pdf(page_candidate)
            fd, temp_name = tempfile.mkstemp(prefix=".ocr-candidate-", suffix=".pdf", dir=destination.parent)
            os.close(fd)
            combined.save(temp_name, garbage=4, deflate=True)

        if source_info is not None:
            candidate_reader = PdfReader(temp_name, strict=True)
            writer = PdfWriter(clone_from=candidate_reader)
            writer._info = source_info.clone(writer)
            fd, exact_info_name = tempfile.mkstemp(prefix=".ocr-info-", suffix=".pdf", dir=destination.parent)
            os.close(fd)
            try:
                with open(exact_info_name, "wb") as stream:
                    writer.write(stream)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.replace(exact_info_name, temp_name)
            finally:
                Path(exact_info_name).unlink(missing_ok=True)

        temp = Path(temp_name)
        if temp.stat().st_size <= 0 or temp.stat().st_size > MAX_OUTPUT_BYTES:
            raise OcrRefused("The OCR candidate is empty or exceeds the output size limit")
        with temp.open("r+b") as stream:
            os.fsync(stream.fileno())
        _check_cancel(cancelled)
        if sha256(source) != original_hash:
            raise SourceChanged("The disposable OCR input changed during processing")

        # Independent logical inspection uses pypdf; visual validation compares
        # the source and candidate renders through MuPDF. A second renderer is
        # exercised by the proof harness before this transform can be accepted.
        check_reader = PdfReader(str(temp), strict=True)
        if check_reader.is_encrypted or len(check_reader.pages) != len(pages) or _has_forbidden_catalog(check_reader):
            raise OcrRefused("The generated OCR candidate failed structural validation")
        if any(not (page.extract_text() or "").strip() for page in check_reader.pages):
            raise OcrRefused("The independent text extractor found no OCR text on every page")
        with pymupdf.open(temp) as check_doc:
            source_metadata = PdfReader(str(source), strict=True).metadata
            if check_reader.metadata != source_metadata:
                raise OcrRefused("The OCR candidate did not preserve source document metadata")
            for page_index, (page_size, pix, native_dpi) in enumerate(pages):
                check_page = check_doc[page_index]
                if not _same_size((check_page.rect.width, check_page.rect.height), page_size):
                    raise OcrRefused(f"OCR changed page {page_index + 1} geometry")
                if not check_page.get_text().strip():
                    raise OcrRefused(f"OCR recognized no text on page {page_index + 1}")
                text_trace = check_page.get_texttrace()
                if not text_trace or any(span.get("type") != 3 for span in text_trace):
                    raise OcrRefused(f"OCR text on page {page_index + 1} is not fully invisible")
                if check_page.get_drawings() or check_page.get_links() or list(check_page.annots() or []):
                    raise OcrRefused(f"OCR page {page_index + 1} contains unexpected visible or interactive content")
                images = check_page.get_images(full=True)
                if len(images) != 1:
                    raise OcrRefused(f"OCR candidate did not preserve the image on page {page_index + 1}")
                candidate_image = pymupdf.Pixmap(check_doc, images[0][0])
                if ((candidate_image.width, candidate_image.height, candidate_image.n, candidate_image.alpha)
                        != (pix.width, pix.height, pix.n, pix.alpha)
                        or candidate_image.samples != pix.samples):
                    raise OcrRefused(f"OCR changed decoded scan pixels on page {page_index + 1}")
                candidate_rects = check_page.get_image_rects(images[0][0])
                if len(candidate_rects) != 1 or candidate_rects[0].irect != check_page.rect.irect:
                    raise OcrRefused(f"OCR changed scan placement on page {page_index + 1}")
                rendered = check_page.get_pixmap(dpi=round(native_dpi), alpha=False)
                if (rendered.width, rendered.height, rendered.samples) != (pix.width, pix.height, pix.samples):
                    raise OcrRefused(f"OCR changed visible source-image pixels on page {page_index + 1}")

        # PDFium is the application's independent renderer. Require exact
        # same-resolution output before the document session sees this candidate.
        _verify_pdfium_pixels(source, temp, expected_pages, Path(pdfium_library))

        _check_cancel(cancelled)
        if sha256(source) != original_hash:
            raise SourceChanged("The disposable OCR input changed before candidate acceptance")
        before_publish()
        _check_cancel(cancelled)
        if sha256(source) != original_hash:
            raise SourceChanged("The disposable OCR input changed before candidate acceptance")
        if destination.exists():
            raise FileExistsError("The OCR candidate path already exists")
        # Create-only publication. The outer session owns commit, recovery, and undo.
        os.link(temp, destination)
        return {
            "sourceSha256": original_hash,
            "candidateSha256": sha256(destination),
            "pageCount": len(pages),
            "pages": [{"page": index + 1, "widthPoints": page_size[0], "heightPoints": page_size[1],
                       "dpi": dpi, "rasterPixels": pix.width * pix.height}
                      for index, (page_size, pix, dpi) in enumerate(pages)],
            "language": "eng",
            "traineddataSha256": EXPECTED_ENG_SHA256,
            "traineddataSourceCommit": ENG_SOURCE_COMMIT,
            "visiblePixelsPreserved": True,
            "metadataPreserved": True,
        }
    finally:
        if temp_name is not None:
            try:
                Path(temp_name).unlink(missing_ok=True)
            except OSError:
                pass


def _main() -> int:
    try:
        line = sys.stdin.readline(262_145)
        if len(line) > 262_144 or sys.stdin.read(1):
            raise OcrRefused("The OCR request is too large")
        request = json.loads(line)
        if not isinstance(request, dict) or set(request) != {
                "InputPath", "CandidatePath", "TrainedDataPath", "PdfiumPath"}:
            raise OcrRefused("The OCR request is invalid")
        result = ocr_scan(request["InputPath"], request["CandidatePath"],
                          traineddata_path=request["TrainedDataPath"],
                          pdfium_library=request["PdfiumPath"])
        print(json.dumps({"Success": True, "Data": result}, ensure_ascii=False))
        return 0
    except Exception as exc:
        message = str(exc) if isinstance(exc, (OcrRefused, OcrCanceled, SourceChanged, FileExistsError, FileNotFoundError)) else "The local OCR operation failed safely"
        print(json.dumps({"Success": False, "Error": message}, ensure_ascii=False))
        return 1


if __name__ == "__main__":
    raise SystemExit(_main())
