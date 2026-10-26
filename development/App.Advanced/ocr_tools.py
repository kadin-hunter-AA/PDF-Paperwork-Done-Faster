"""Broader OCR operations: per-page inspection, language listing and mixed-document recognition.

Unlike ocr.py (pure-scan documents only), OcrPages works on any document: pages that
already carry text are left untouched, and only the chosen scanned pages receive an
invisible text layer that is appended to the page (original content is wrapped, never
rewritten). Output is written as an incremental update of a copy of the input, so every
other byte of the document is preserved. Page numbers in this module are zero-based.
"""
from __future__ import annotations

import hashlib
import math
import os
from pathlib import Path
import re
import shutil
import tempfile

import pymupdf

MAX_INPUT_BYTES = 256 * 1024 * 1024
MAX_INSPECT_PAGES = 5000
INSPECT_LIST_LIMIT = 300
IMAGE_ONLY_LIST_LIMIT = 2000
MAX_PAGES_PER_CALL = 25
MAX_LANGUAGES = 4
MAX_TESSDATA_DIRS = 4
MAX_RASTER_PIXELS = 40_000_000
OCR_DPI = 300
TRIAL_DPI = 150
MIN_DPI = 100
SKEW_LIMIT_DEGREES = 5.0
SKEW_MIN_DEGREES = 0.3
IMAGE_ONLY_COVERAGE = 0.5

LANGUAGE_NAMES = {
    "eng": "English", "deu": "German", "fra": "French", "spa": "Spanish", "ita": "Italian",
    "por": "Portuguese", "nld": "Dutch",
}
_LANG_RE = re.compile(r"^[A-Za-z0-9_]{2,24}$")
_WORD_RE = re.compile(r"[^\W\d_]{3,}")
_REF_RE = re.compile(r"(\d+) 0 R")


class Refused(ValueError):
    """Safe-to-display refusal; never contains document content or paths."""


# ---------------------------------------------------------------- validation helpers

def _path(request: dict, key: str, required: bool = True) -> Path | None:
    value = request.get(key)
    if value is None and not required:
        return None
    if not isinstance(value, str) or not value or "\x00" in value:
        raise Refused("The OCR request is malformed.")
    return Path(value)


def _tessdata_dirs(request: dict) -> list[Path]:
    dirs = request.get("TessdataDirectories")
    if not isinstance(dirs, list) or not 1 <= len(dirs) <= MAX_TESSDATA_DIRS:
        raise Refused("The OCR request must name between one and four language folders.")
    out = []
    for item in dirs:
        if not isinstance(item, str) or not item or "\x00" in item:
            raise Refused("The OCR request is malformed.")
        out.append(Path(item))
    return out


def _open_source(path: Path, password) -> pymupdf.Document:
    if path.is_symlink() or not path.is_file():
        raise Refused("OCR requires a regular PDF file.")
    if not 0 < path.stat().st_size <= MAX_INPUT_BYTES:
        raise Refused("The PDF is empty or exceeds the OCR input size limit.")
    try:
        doc = pymupdf.open(str(path))
    except Exception as exc:
        raise Refused("The PDF could not be opened for OCR.") from exc
    if doc.needs_pass or doc.is_encrypted:
        doc.close()
        raise Refused("Password-protected PDFs are not supported by this OCR operation.")
    if doc.page_count < 1:
        doc.close()
        raise Refused("The PDF has no pages.")
    return doc


# ---------------------------------------------------------------- inspection

def _coverage(page, infos) -> float:
    rect = page.rect
    if rect.is_empty or not infos:
        return 0.0
    n = 24
    cw, ch = rect.width / n, rect.height / n
    boxes = [pymupdf.Rect(i["bbox"]) & rect for i in infos]
    boxes = [b for b in boxes if not b.is_empty][:64]
    covered = 0
    for gy in range(n):
        cy = rect.y0 + (gy + 0.5) * ch
        for gx in range(n):
            cx = rect.x0 + (gx + 0.5) * cw
            if any(b.x0 <= cx <= b.x1 and b.y0 <= cy <= b.y1 for b in boxes):
                covered += 1
    return covered / (n * n)


def _classify(page) -> dict:
    text = page.get_text("text")
    chars = sum(1 for c in text if not c.isspace())
    infos = page.get_image_info()
    coverage = _coverage(page, infos)
    pixels = 0
    for info in infos[:256]:
        pixels += max(0, int(info.get("width", 0))) * max(0, int(info.get("height", 0)))
    if chars > 0:
        kind = "HasText"
    elif not infos:
        kind = "Empty"
    elif coverage >= IMAGE_ONLY_COVERAGE:
        kind = "ImageOnly"
    else:
        kind = "Other"
    return {"Kind": kind, "TextChars": chars, "Images": len(infos), "Coverage": round(coverage, 2),
            "Pixels": min(pixels, 2_000_000_000)}


def inspect(request: dict) -> dict:
    source = _path(request, "Input")
    start = request.get("Start", 0)
    if isinstance(start, bool) or not isinstance(start, int) or start < 0:
        raise Refused("The OCR inspection request is malformed.")
    doc = _open_source(source, request.get("Password"))
    try:
        count = doc.page_count
        scanned = min(count, MAX_INSPECT_PAGES)
        totals = {"HasText": 0, "ImageOnly": 0, "Empty": 0, "Other": 0}
        image_only_pages: list[int] = []
        listed = []
        image_only_pixels = 0
        for index in range(scanned):
            page = doc[index]
            info = _classify(page)
            totals[info["Kind"]] += 1
            if info["Kind"] == "ImageOnly":
                image_only_pixels += info["Pixels"]
                if len(image_only_pages) < IMAGE_ONLY_LIST_LIMIT:
                    image_only_pages.append(index)
            if start <= index < start + INSPECT_LIST_LIMIT:
                listed.append({"Page": index, "WidthPoints": round(page.rect.width, 1),
                               "HeightPoints": round(page.rect.height, 1), **info})
        return {"PageCount": count, "PagesScanned": scanned, "Start": start,
                "Truncated": scanned < count, "ListLimit": INSPECT_LIST_LIMIT,
                "Totals": {"HasText": totals["HasText"], "ImageOnly": totals["ImageOnly"],
                           "Empty": totals["Empty"], "Other": totals["Other"],
                           "ImageOnlyPixels": image_only_pixels},
                "ImageOnlyPages": image_only_pages, "ImageOnlyListTruncated": totals["ImageOnly"] > len(image_only_pages),
                "Pages": listed}
    finally:
        doc.close()


# ---------------------------------------------------------------- languages

def list_languages(request: dict) -> dict:
    found: dict[str, dict] = {}
    for index, directory in enumerate(_tessdata_dirs(request)):
        try:
            names = sorted(os.listdir(directory)) if directory.is_dir() else []
        except OSError:
            names = []
        for name in names:
            if not name.endswith(".traineddata"):
                continue
            code = name[: -len(".traineddata")]
            if code in ("osd", "equ") or not _LANG_RE.match(code) or code in found:
                continue
            entry = directory / name
            if entry.is_symlink() or not entry.is_file():
                continue
            found[code] = {"Code": code, "Name": LANGUAGE_NAMES.get(code, code), "Source": index,
                           "SizeBytes": entry.stat().st_size}
            if len(found) >= 200:
                break
    osd = any((d / "osd.traineddata").is_file() for d in _tessdata_dirs(request))
    return {"Languages": list(found.values()), "OsdAvailable": osd}


def _stage_tessdata(dirs: list[Path], languages: list[str]) -> Path:
    stage = Path(tempfile.mkdtemp(prefix="hpocr-"))
    try:
        for code in languages:
            for directory in dirs:
                candidate = directory / (code + ".traineddata")
                if candidate.is_file() and not candidate.is_symlink():
                    target = stage / (code + ".traineddata")
                    try:
                        os.link(candidate, target)
                    except OSError:
                        shutil.copyfile(candidate, target)
                    break
            else:
                raise Refused("A requested OCR language is not installed.")
        if len(str(stage)) > 200:
            raise Refused("The temporary OCR folder path is too long.")
        return stage
    except BaseException:
        shutil.rmtree(stage, ignore_errors=True)
        raise


# ---------------------------------------------------------------- recognition helpers

def _render(page, dpi: float, theta: float, gray: bool = False):
    z = dpi / 72.0
    matrix = pymupdf.Matrix(z, z).prerotate(theta)
    pix = page.get_pixmap(matrix=matrix, colorspace=pymupdf.csGRAY if gray else pymupdf.csRGB, alpha=False)
    pix.set_dpi(round(dpi), round(dpi))
    return matrix, pix


def _ocr_pdf(pix, language: str, tessdata: Path) -> bytes:
    return pix.pdfocr_tobytes(compress=False, language=language, tessdata=str(tessdata))


def _text_score(data: bytes) -> int:
    with pymupdf.open(stream=data, filetype="pdf") as doc:
        text = doc[0].get_text()
    return sum(len(token) for token in _WORD_RE.findall(text))


def _choose_quarter(page, language: str, tessdata: Path, dpi: float) -> int:
    """Trial recognition at 0/90/180/270 degrees; keeps 0 unless another turn is clearly better."""
    trial_dpi = min(TRIAL_DPI, dpi)
    scores = {}
    for quarter in (0, 1, 3, 2):
        _, pix = _render(page, trial_dpi, quarter * 90.0)
        score = _text_score(_ocr_pdf(pix, language, tessdata))
        scores[quarter] = score
        if quarter == 0 and score >= 60:
            return 0
    best = max(scores, key=lambda q: scores[q])
    if best != 0 and scores[best] >= max(25, 2 * scores[0]):
        return best
    return 0


def _detect_skew(page, quarter: int) -> float:
    """Projection-profile skew estimate in degrees (render rotation to apply), 0 when unsure."""
    _, pix = _render(page, 100, quarter * 90.0, gray=True)
    w, h = pix.width, pix.height
    samples = pix.samples
    points = []
    for y in range(0, h, 2):
        row = samples[y * w:(y + 1) * w]
        for x in range(0, w, 2):
            if row[x] < 128:
                points.append((x, y))
    if len(points) < 200:
        return 0.0
    if len(points) > 20000:
        step = len(points) / 20000.0
        points = [points[int(i * step)] for i in range(20000)]

    def score(angle: float) -> float:
        s, c = math.sin(math.radians(angle)), math.cos(math.radians(angle))
        bins: dict[int, int] = {}
        for x, y in points:
            key = int((x * s + y * c) / 2)
            bins[key] = bins.get(key, 0) + 1
        return float(sum(v * v for v in bins.values()))

    base = score(0.0)
    best_angle, best = 0.0, base
    angle = -SKEW_LIMIT_DEGREES
    while angle <= SKEW_LIMIT_DEGREES + 1e-9:
        value = score(angle)
        if value > best:
            best, best_angle = value, angle
        angle += 0.5
    for fine in range(-4, 5):
        angle = best_angle + fine * 0.1
        if abs(angle) > SKEW_LIMIT_DEGREES:
            continue
        value = score(angle)
        if value > best:
            best, best_angle = value, angle
    if abs(best_angle) < SKEW_MIN_DEGREES or best < base * 1.04:
        return 0.0
    return round(best_angle, 1)


def _copy_object(src, dst, xref: int, memo: dict) -> int:
    if xref in memo:
        return memo[xref]
    new = dst.get_new_xref()
    memo[xref] = new
    text = src.xref_object(xref, compressed=True)
    text = _REF_RE.sub(lambda m: f"{_copy_object(src, dst, int(m.group(1)), memo)} 0 R", text)
    dst.update_object(new, text)
    if src.xref_is_stream(xref):
        dst.xref_set_key(new, "Filter", "null")
        dst.xref_set_key(new, "DecodeParms", "null")
        dst.update_stream(new, src.xref_stream(xref), compress=True)
    return new


def _font_signature(src, xref: int) -> str:
    digest = hashlib.sha256()
    seen: set[int] = set()

    def visit(x: int) -> None:
        if x in seen:
            return
        seen.add(x)
        text = src.xref_object(x, compressed=True)
        digest.update(_REF_RE.sub("R", text).encode("utf-8", "replace"))
        if src.xref_is_stream(x):
            digest.update(src.xref_stream(x) or b"")
        for m in _REF_RE.finditer(text):
            visit(int(m.group(1)))

    visit(xref)
    return digest.hexdigest()


def _page_resource_object(doc, page) -> int:
    """Returns an indirect, page-private copy of the page's effective resources."""
    kind, value = doc.xref_get_key(page.xref, "Resources")
    if kind == "xref":
        text = doc.xref_object(int(value.split()[0]), compressed=True)
    elif kind == "dict":
        text = value
    else:
        text, parent = None, page.xref
        for _ in range(32):
            kind, value = doc.xref_get_key(parent, "Parent")
            if kind != "xref":
                break
            parent = int(value.split()[0])
            kind, value = doc.xref_get_key(parent, "Resources")
            if kind == "xref":
                text = doc.xref_object(int(value.split()[0]), compressed=True)
                break
            if kind == "dict":
                text = value
                break
        if text is None:
            text = "<< >>"
    new = doc.get_new_xref()
    doc.update_object(new, text)
    doc.xref_set_key(page.xref, "Resources", f"{new} 0 R")
    return new


def _register_font(doc, page, font_xref: int) -> str:
    res = _page_resource_object(doc, page)
    kind, value = doc.xref_get_key(res, "Font")
    index = 0
    if kind == "xref":
        font_dict_text = doc.xref_object(int(value.split()[0]), compressed=True)
    elif kind == "dict":
        font_dict_text = value
    else:
        font_dict_text = "<< >>"
    while f"/HPOcr{index} " in font_dict_text or f"/HPOcr{index}/" in font_dict_text:
        index += 1
    name = f"HPOcr{index}"
    body = font_dict_text.rstrip()
    if not body.endswith(">>"):
        raise Refused("A page font table could not be extended safely.")
    new_dict = body[:-2] + f" /{name} {font_xref} 0 R >>"
    if kind == "xref":
        fx = doc.get_new_xref()
        doc.update_object(fx, new_dict)
        doc.xref_set_key(res, "Font", f"{fx} 0 R")
    else:
        doc.xref_set_key(res, "Font", new_dict)
    return name


def _graft_layer(doc, page, ocr_bytes: bytes, dpi: float, theta: float, matrix, pix, font_cache: dict) -> tuple[int, int]:
    """Appends the recognized invisible text to `page`; returns (words, characters)."""
    with pymupdf.open(stream=ocr_bytes, filetype="pdf") as ocr:
        ocr_page = ocr[0]
        contents = ocr_page.read_contents()
        marker = contents.find(b"\nBT\n")
        if marker < 0:
            return 0, 0
        text_ops = contents[marker + 1:]
        words = text_ops.count(b"Tj")
        chars = sum(1 for c in ocr_page.get_text() if not c.isspace())
        if words == 0 or chars < 3:
            return 0, 0
        fonts = ocr_page.get_fonts()
        if len(fonts) != 1:
            raise Refused("The OCR engine returned an unexpected text layer.")
        signature = _font_signature(ocr, fonts[0][0])
        if signature not in font_cache:
            font_cache[signature] = _copy_object(ocr, doc, fonts[0][0], {})
        font_xref = font_cache[signature]
        ocr_height = ocr_page.rect.height
        old_name = fonts[0][4] if len(fonts[0]) > 4 else "F0"
    z = dpi / 72.0
    flip = pymupdf.Matrix(1, 0, 0, -1, 0, ocr_height)
    to_view = pymupdf.Matrix(z, z) * pymupdf.Matrix(1, 0, 0, 1, pix.x, pix.y) * ~matrix
    to_native = ~(page.transformation_matrix * page.rotation_matrix)
    cm = flip * to_view * to_native
    name = _register_font(doc, page, font_xref)
    text_ops = re.sub(rb"/" + re.escape(old_name.encode()) + rb"(?=\s+[\d.]+\s+Tf)", b"/" + name.encode(), text_ops)
    numbers = " ".join(f"{v:.6f}" for v in (cm.a, cm.b, cm.c, cm.d, cm.e, cm.f))
    stream = b"q\n" + numbers.encode() + b" cm\n" + text_ops + b"\nQ\n"
    page.wrap_contents()
    contents_refs = page.get_contents()
    layer = doc.get_new_xref()
    doc.update_object(layer, "<<>>")
    doc.update_stream(layer, stream, new=True)
    refs = " ".join(f"{x} 0 R" for x in contents_refs + [layer])
    doc.xref_set_key(page.xref, "Contents", f"[ {refs} ]")
    return words, chars


def _verify(source: Path, output: Path, processed: dict[int, tuple[int, int]]) -> None:
    with pymupdf.open(str(source)) as before, pymupdf.open(str(output)) as after:
        if before.page_count != after.page_count:
            raise Refused("The OCR result changed the page count; no changes were made.")
        for index in range(before.page_count):
            a, b = before[index], after[index]
            if (a.rect != b.rect or a.rotation != b.rotation or a.cropbox != b.cropbox or a.mediabox != b.mediabox):
                raise Refused("The OCR result changed page geometry; no changes were made.")
            if index not in processed:
                if a.read_contents() != b.read_contents():
                    raise Refused("The OCR result changed an unrelated page; no changes were made.")
                continue
            pa = a.get_pixmap(dpi=72, alpha=False)
            pb = b.get_pixmap(dpi=72, alpha=False)
            if (pa.width, pa.height, pa.samples) != (pb.width, pb.height, pb.samples):
                raise Refused("The OCR result changed the visible page; no changes were made.")
            trace = b.get_texttrace()
            if not trace or any(span.get("type") != 3 for span in trace):
                raise Refused("The recognized text is not fully invisible; no changes were made.")
            if not b.get_text().strip():
                raise Refused("The recognized text could not be read back; no changes were made.")


def _save(doc, output: Path) -> None:
    if doc.can_save_incrementally():
        doc.saveIncr()
        return
    temp = output.with_name("." + output.name + ".full")
    try:
        doc.save(str(temp), garbage=0, deflate=True)
        doc.close()
        os.replace(temp, output)
    finally:
        temp.unlink(missing_ok=True)


def ocr_pages(request: dict) -> dict:
    source = _path(request, "Input")
    output = _path(request, "Output")
    pages = request.get("Pages")
    languages = request.get("Languages")
    deskew = request.get("Deskew", False)
    auto_rotate = request.get("AutoRotate", True)
    if not isinstance(deskew, bool) or not isinstance(auto_rotate, bool):
        raise Refused("The OCR request is malformed.")
    if (not isinstance(languages, list) or not 1 <= len(languages) <= MAX_LANGUAGES
            or any(not isinstance(l, str) or not _LANG_RE.match(l) or l in ("osd", "equ") for l in languages)
            or len(set(languages)) != len(languages)):
        raise Refused("Choose between one and four installed OCR languages.")
    dirs = _tessdata_dirs(request)
    if output.exists():
        raise Refused("The OCR candidate path already exists.")
    if not output.parent.is_dir():
        raise Refused("The OCR candidate folder does not exist.")
    doc = _open_source(source, request.get("Password"))
    stage = None
    try:
        count = doc.page_count
        if pages is None:
            pages = [i for i in range(min(count, MAX_INSPECT_PAGES)) if _classify(doc[i])["Kind"] == "ImageOnly"]
        if (not isinstance(pages, list) or any(isinstance(p, bool) or not isinstance(p, int) for p in pages)):
            raise Refused("The OCR page list is malformed.")
        if any(p < 0 or p >= count for p in pages):
            raise Refused("An OCR page number is outside the document.")
        pages = list(dict.fromkeys(pages))
        if len(pages) > MAX_PAGES_PER_CALL:
            raise Refused(f"At most {MAX_PAGES_PER_CALL} pages can be recognized per engine call.")
        stage = _stage_tessdata(dirs, languages)
        language = "+".join(languages)
        decisions: dict[int, dict] = {}
        skipped_text, skipped_image, too_large, no_text = [], [], [], []
        todo = []
        for index in pages:
            info = _classify(doc[index])
            if info["TextChars"] > 0:
                skipped_text.append(index)
            elif info["Images"] == 0:
                skipped_image.append(index)
            else:
                todo.append(index)
        doc.close()
        doc = None
        shutil.copyfile(source, output)
        work = pymupdf.open(str(output))
        recognized = []
        processed: dict[int, tuple[int, int]] = {}
        font_cache: dict = {}
        try:
            for index in todo:
                page = work[index]
                area = page.rect.width * page.rect.height
                dpi = float(OCR_DPI)
                if area * (dpi / 72.0) ** 2 * 1.5 > MAX_RASTER_PIXELS:
                    dpi = math.sqrt(MAX_RASTER_PIXELS / 1.5 / area) * 72.0
                if dpi < MIN_DPI:
                    too_large.append(index)
                    continue
                dpi = float(int(dpi))
                quarter = _choose_quarter(page, language, stage, dpi) if auto_rotate else 0
                skew = _detect_skew(page, quarter) if deskew else 0.0
                theta = quarter * 90.0 + skew
                matrix, pix = _render(page, dpi, theta)
                data = _ocr_pdf(pix, language, stage)
                words, chars = _graft_layer(work, page, data, dpi, theta, matrix, pix, font_cache)
                del pix, data
                if words == 0:
                    no_text.append(index)
                    continue
                processed[index] = (words, chars)
                recognized.append({"Page": index, "Words": words, "Characters": chars,
                                   "RotationDegrees": quarter * 90, "SkewDegrees": skew, "Dpi": int(dpi)})
            if processed:
                _save(work, output)
            else:
                work.close()
        except BaseException:
            try:
                work.close()
            except Exception:
                pass
            output.unlink(missing_ok=True)
            raise
        try:
            _verify(source, output, processed)
        except BaseException:
            output.unlink(missing_ok=True)
            raise
        osd = any((d / "osd.traineddata").is_file() for d in dirs)
        return {"Applied": True, "Recognized": recognized, "SkippedHasText": skipped_text,
                "SkippedNoImage": skipped_image, "SkippedTooLarge": too_large, "NoTextFound": no_text,
                "OsdAvailable": osd,
                "OrientationMethod": "trial" if auto_rotate else "off",
                "Languages": languages}
    finally:
        if doc is not None:
            doc.close()
        if stage is not None:
            shutil.rmtree(stage, ignore_errors=True)


def handle(operation: str, request: dict) -> dict:
    if operation == "OcrInspect":
        return inspect(request)
    if operation == "OcrLanguages":
        return list_languages(request)
    if operation == "OcrPages":
        return ocr_pages(request)
    raise Refused("Unsupported OCR operation.")
