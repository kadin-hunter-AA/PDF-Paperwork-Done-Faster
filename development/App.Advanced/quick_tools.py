"""Quick-tools companion module: compress, protect, watermark, headers, Bates, crop, flatten,
properties, bookmarks, image extraction and link management.

Contract (see worker.py): Refused messages are shown to the user and never contain document
content, paths or passwords; handle(operation, request) returns a JSON-safe dict. Edits and
exports write request["Output"] (which must not exist); queries write nothing.
"""
from __future__ import annotations

import json
import math
import os
import re
import shutil

import pymupdf
from pypdf import PdfReader, PdfWriter
from pypdf.constants import UserAccessPermissions

pymupdf.TOOLS.mupdf_display_errors(False)
pymupdf.TOOLS.mupdf_display_warnings(False)

POSITIONS = ("TopLeft", "TopCenter", "TopRight", "BottomLeft", "BottomCenter", "BottomRight")
MAX_PAGES = 200_000
MAX_BOOKMARKS = 5000
MAX_LINKS = 500
MAX_IMAGES = 2000
MAX_IMAGE_NAMES = 200
MAX_IMAGE_BYTES = 1024 * 1024 * 1024
RESPONSE_BUDGET = 48_000  # stdout limit is 64 KiB; keep headroom for the envelope
CONTROL = re.compile(r"[\x00-\x1f\x7f-\x9f]")
PLACEHOLDER = re.compile(r"\{(n|total|date|filename)\}")


class Refused(ValueError):
    """A plain-language refusal that is safe to show to the user."""


# --------------------------------------------------------------------------- validation

def _bool(request, name, default=None):
    value = request.get(name, default)
    if not isinstance(value, bool):
        raise Refused(f"{name} must be true or false.")
    return value


def _int(request, name, low, high, optional=False):
    value = request.get(name)
    if value is None and optional:
        return None
    if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
        raise Refused(f"{name} must be a whole number from {low} to {high}.")
    return value


def _number(request, name, low, high):
    value = request.get(name)
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not low <= value <= high:
        raise Refused(f"{name} must be a number from {low} to {high}.")
    return float(value)


def _text(request, name, low, high, optional=False):
    value = request.get(name)
    if value is None and optional:
        return None
    if not isinstance(value, str) or not low <= len(value) <= high:
        raise Refused(f"{name} must be {low} to {high} characters." if low else f"{name} must be at most {high} characters.")
    if CONTROL.search(value):
        raise Refused(f"{name} contains control characters.")
    return value


def _latin1(value, label):
    try:
        value.encode("latin-1")
    except UnicodeEncodeError:
        raise Refused(f"{label} can only use Western European (Latin-1) characters.") from None
    return value


def _position(request):
    value = request.get("Position")
    if value not in POSITIONS:
        raise Refused("Position must be one of " + ", ".join(POSITIONS) + ".")
    return value


def _pages(request, count, name="Pages"):
    value = request.get(name)
    if value is None:
        return list(range(count))
    if not isinstance(value, list) or not value or len(value) > count:
        raise Refused("Pages must be empty for all pages or a list of distinct page numbers.")
    if any(isinstance(p, bool) or not isinstance(p, int) or not 0 <= p < count for p in value) or len(set(value)) != len(value):
        raise Refused("Pages must be distinct page numbers inside the document.")
    return sorted(value)


def _page_index(request, count, name="Page"):
    return _int(request, name, 0, count - 1)


def _rect(request, names=("Left", "Bottom", "Right", "Top")):
    left, bottom, right, top = (_number(request, n, -1e6, 1e6) for n in names)
    if right <= left or top <= bottom:
        raise Refused("The rectangle must have a positive width and height.")
    return left, bottom, right, top


def _check_output(request):
    output = request.get("Output")
    if not isinstance(output, str) or not output:
        raise Refused("The operation did not provide an output file.")
    if os.path.exists(output):
        raise Refused("The output file already exists.")
    return output


def _read(source):
    # MuPDF cannot open Windows long-path (\\?\) names, so bytes go through Python's own file API.
    try:
        with open(source, "rb") as handle:
            return pymupdf.open(stream=handle.read(), filetype="pdf")
    except Exception:
        raise Refused("The PDF could not be opened.") from None


def _write(doc, output, **options):
    data = doc.tobytes(**options)
    with open(output, "xb") as target:
        target.write(data)


def _read(source):
    # MuPDF cannot open Windows long (\\?\) paths, so the bytes go through Python's own file API.
    try:
        with open(source, "rb") as handle:
            return pymupdf.open(stream=handle.read(), filetype="pdf")
    except Exception:
        raise Refused("The PDF could not be opened.") from None


def _write(doc, output, **options):
    data = doc.tobytes(**options)
    with open(output, "xb") as target:
        target.write(data)


def _open(request, allow_password=True):
    doc = _read(request["Input"])
    if doc.needs_pass:
        password = request.get("Password")
        if not allow_password or not isinstance(password, str) or not doc.authenticate(password):
            doc.close()
            raise Refused("The PDF is password-protected and could not be unlocked.")
    if doc.page_count < 1 or doc.page_count > MAX_PAGES:
        doc.close()
        raise Refused("The PDF has no pages or too many pages.")
    return doc


def _save_edit(doc, output):
    _write(doc, output, garbage=1, deflate=True)


def _boxes(source, count):
    try:
        with open(source, "rb") as handle:
            reader = PdfReader(handle)
            if reader.is_encrypted:
                raise Refused("The PDF is encrypted.")
            result = []
            for index in range(count):
                page = reader.pages[index]
                media, crop = page.mediabox, page.cropbox
                result.append(([float(media.left), float(media.bottom), float(media.right), float(media.top)],
                               [float(crop.left), float(crop.bottom), float(crop.right), float(crop.top)]))
            return result
    except Refused:
        raise
    except Exception:
        raise Refused("The page boxes of this PDF could not be read.") from None


# --------------------------------------------------------------------------- text drawing

def _place(page, text, fontsize, anchor, angle, color, opacity):
    """Draws Helvetica text so it appears upright (plus `angle` degrees counter-clockwise) on the
    displayed page. `anchor` is a displayed-space (y down) point: the text centre."""
    width = pymupdf.get_text_length(text, "helv", fontsize)
    radians = math.radians(angle)
    along = pymupdf.Point(math.cos(radians), -math.sin(radians))
    down = pymupdf.Point(math.sin(radians), math.cos(radians))
    start_d = anchor - along * (width / 2) + down * (0.35 * fontsize)
    _draw(page, text, fontsize, start_d, along, color, opacity)


def _draw(page, text, fontsize, start_d, along, color, opacity):
    inverse = page.derotation_matrix
    start = start_d * inverse
    vector = (start_d + along) * inverse - start
    theta = math.degrees(math.atan2(vector.y, vector.x))
    # insert_text morphs in PDF space (y up), so the sign is opposite to the y-down vector.
    page.insert_text(start, text, fontsize=fontsize, fontname="helv", color=color,
                     fill_opacity=opacity, morph=(start, pymupdf.Matrix(-theta)) if abs(theta) > 1e-9 else None)


def _edge_text(page, text, fontsize, margin, position):
    area = page.rect
    width = pymupdf.get_text_length(text, "helv", fontsize)
    if width > area.width - 2 * margin + 0.01:
        raise Refused("The text is too wide for the page at this size and margin.")
    if position.endswith("Left"):
        x = area.x0 + margin
    elif position.endswith("Center"):
        x = area.x0 + (area.width - width) / 2
    else:
        x = area.x1 - margin - width
    y = area.y0 + margin + fontsize * 0.8 if position.startswith("Top") else area.y1 - margin
    if y - fontsize * 0.8 < area.y0 or y + fontsize * 0.25 > area.y1:
        raise Refused("The text does not fit on the page at this size and margin.")
    _draw(page, text, fontsize, pymupdf.Point(x, y), pymupdf.Point(1, 0), (0, 0, 0), 1)


# --------------------------------------------------------------------------- operations

def _downsample(doc, target):
    """Re-encodes opaque images placed above `target` dpi at the target resolution (JPEG quality 80, or PNG when smaller).
    MuPDF's own rewriter ignores the placed size, so the effective dpi is measured here."""
    best = {}
    for page in doc:
        try:
            infos = page.get_image_info(xrefs=True)
        except Exception:
            continue
        for info in infos:
            xref, width, box = info.get("xref", 0), info.get("width", 0), pymupdf.Rect(info["bbox"])
            if xref <= 0 or box.width < 1 or width < 1 or info.get("has-mask"):
                continue
            dpi = width / box.width * 72
            if xref not in best or dpi > best[xref][0]:
                best[xref] = (dpi, page)
    for xref, (dpi, page) in best.items():
        if dpi <= target * 1.05:
            continue
        try:
            pix = pymupdf.Pixmap(doc, xref)
            if pix.alpha or pix.colorspace is None:
                continue
            if pix.colorspace.n not in (1, 3):
                pix = pymupdf.Pixmap(pymupdf.csRGB, pix)
            scale = target / dpi
            small = pymupdf.Pixmap(pix, max(1, round(pix.width * scale)), max(1, round(pix.height * scale)))
            data = small.tobytes("jpeg", jpg_quality=80)
            lossless = small.tobytes("png")
            # The resolution cap is honoured; flat graphics stay lossless when that is smaller than JPEG.
            page.replace_image(xref, stream=min(data, lossless, key=len))
        except Exception:
            continue


def _compress(request):
    dpi = _int(request, "ImageDpi", 72, 300, optional=True)
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be compressed.")
        if dpi is not None:
            _downsample(doc, dpi)
        _write(doc, output, garbage=4, deflate=True, clean=True, use_objstms=1)
    finally:
        doc.close()
    return {"Applied": True, "InputBytes": os.path.getsize(request["Input"]), "OutputBytes": os.path.getsize(output)}


def _protect(request):
    user = _text(request, "UserPassword", 1, 128)
    owner = _text(request, "OwnerPassword", 1, 128)
    allow_print, allow_copy, allow_edit = (_bool(request, n) for n in ("AllowPrint", "AllowCopy", "AllowEdit"))
    output = _check_output(request)
    try:
        handle = open(request["Input"], "rb")
    except OSError:
        raise Refused("The PDF could not be opened.") from None
    with handle:
        reader = PdfReader(handle)
        if reader.is_encrypted:
            raise Refused("The PDF is already password-protected.")
        writer = PdfWriter(clone_from=reader)
        flags = UserAccessPermissions(0xFFFFF000) | UserAccessPermissions.R7 | UserAccessPermissions.R8
        if allow_print:
            flags |= UserAccessPermissions.PRINT | UserAccessPermissions.PRINT_TO_REPRESENTATION
        if allow_copy:
            flags |= UserAccessPermissions.EXTRACT | UserAccessPermissions.EXTRACT_TEXT_AND_GRAPHICS
        if allow_edit:
            flags |= (UserAccessPermissions.MODIFY | UserAccessPermissions.ADD_OR_MODIFY |
                      UserAccessPermissions.FILL_FORM_FIELDS | UserAccessPermissions.ASSEMBLE_DOC)
        writer.encrypt(user, owner, permissions_flag=flags, algorithm="AES-256")
        with open(output, "xb") as target:
            writer.write(target)
    return {"Applied": True}


def _unprotect(request):
    password = request.get("Password")
    if not isinstance(password, str):
        raise Refused("Open the PDF with its password first; protection can only be removed from an unlocked document.")
    output = _check_output(request)
    doc = _read(request["Input"])
    try:
        if not doc.is_encrypted:
            raise Refused("The PDF is not password-protected.")
        result = doc.authenticate(password) if doc.needs_pass else 1
        if not result:
            raise Refused("The password is incorrect.")
        everything = 4 | 8 | 16 | 32
        if not result & 4 and (doc.permissions & everything) != everything:
            raise Refused("This PDF has usage restrictions; the owner password is needed to remove them.")
        _write(doc, output, encryption=pymupdf.PDF_ENCRYPT_NONE, garbage=1, deflate=True)
    finally:
        doc.close()
    return {"Applied": True}


def _watermark(request):
    text = _latin1(_text(request, "Text", 1, 200), "The watermark text")
    fontsize = _number(request, "FontSize", 8, 200)
    color = tuple(_int(request, n, 0, 255) / 255 for n in ("Red", "Green", "Blue"))
    opacity = _number(request, "Opacity", 0.05, 1)
    angle = request.get("Rotation")
    if isinstance(angle, bool) or angle not in (0, 45, 90, -45):
        raise Refused("Rotation must be 0, 45, 90 or -45 degrees.")
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        pages = _pages(request, doc.page_count)
        for index in pages:
            page = doc[index]
            area = page.rect
            _place(page, text, fontsize, pymupdf.Point(area.x0 + area.width / 2, area.y0 + area.height / 2),
                   angle, color, opacity)
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True, "Pages": len(pages)}


def _header_footer(request):
    template = _text(request, "Template", 1, 200)
    name = _text(request, "FileName", 0, 260)
    date = _text(request, "Date", 0, 260)
    position = _position(request)
    fontsize = _number(request, "FontSize", 6, 72)
    margin = _number(request, "Margin", 9, 144)
    start = _int(request, "StartNumber", 1, 10_000_000)
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        pages = _pages(request, doc.page_count)
        total = doc.page_count
        for index in pages:
            values = {"n": str(start + index), "total": str(total), "date": date, "filename": name}
            line = PLACEHOLDER.sub(lambda m: values[m.group(1)], template)
            if len(line) > 600:
                raise Refused("The expanded text is too long.")
            _latin1(line, "The header or footer text")
            if CONTROL.search(line):
                raise Refused("The header or footer text contains control characters.")
            _edge_text(doc[index], line, fontsize, margin, position)
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True, "Pages": len(pages)}


def _bates(request):
    prefix = _text(request, "Prefix", 0, 40)
    suffix = _text(request, "Suffix", 0, 40)
    for label, value in (("Prefix", prefix), ("Suffix", suffix)):
        if not all(0x20 <= ord(c) < 0x7F for c in value):
            raise Refused(f"{label} can only use printable ASCII characters.")
    digits = _int(request, "Digits", 1, 12)
    first = _int(request, "Start", 0, 10 ** digits - 1)
    position = _position(request)
    fontsize = _number(request, "FontSize", 6, 72)
    margin = _number(request, "Margin", 9, 144)
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        pages = _pages(request, doc.page_count)
        last = first + len(pages) - 1
        if last >= 10 ** digits:
            raise Refused("The numbers need more digits than selected.")
        for ordinal, index in enumerate(pages):
            _edge_text(doc[index], f"{prefix}{first + ordinal:0{digits}d}{suffix}", fontsize, margin, position)
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True, "Pages": len(pages), "FirstNumber": first, "LastNumber": last,
            "FirstLabel": f"{prefix}{first:0{digits}d}{suffix}", "LastLabel": f"{prefix}{last:0{digits}d}{suffix}"}


def _crop(request):
    left, bottom, right, top = _rect(request)
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        pages = _pages(request, doc.page_count)
        if right - left < 36 or top - bottom < 36:
            raise Refused("The cropped page must be at least 36 by 36 points.")
        boxes = _boxes(request["Input"], doc.page_count)
        for index in pages:
            media = boxes[index][0]
            if left < media[0] - 0.01 or bottom < media[1] - 0.01 or right > media[2] + 0.01 or top > media[3] + 0.01:
                raise Refused("The crop area extends outside the page.")
        for index in pages:
            doc.xref_set_key(doc[index].xref, "CropBox", f"[{left:.4f} {bottom:.4f} {right:.4f} {top:.4f}]")
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True, "Pages": len(pages)}


def _annotation_subtypes(doc, page):
    refs = _annot_refs(doc, page)
    result = []
    for xref in refs:
        kind, value = doc.xref_get_key(xref, "Subtype")
        result.append(value.lstrip("/") if kind == "name" else "")
    return result


def _flatten(request):
    annotations = _bool(request, "Annotations")
    forms = _bool(request, "Forms")
    if not annotations and not forms:
        raise Refused("Choose annotations, form fields or both to flatten.")
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        annots = widgets = 0
        for page in doc:
            for subtype in _annotation_subtypes(doc, page):
                if subtype == "Widget":
                    widgets += 1
                elif subtype not in ("Link", "Popup", ""):
                    annots += 1
        doc.bake(annots=annotations, widgets=forms)
        _write(doc, output, garbage=3, deflate=True)
    finally:
        doc.close()
    return {"Applied": True, "AnnotationsFlattened": annots if annotations else 0, "FormFieldsFlattened": widgets if forms else 0}


def _properties(request):
    doc = _open(request)
    try:
        meta = doc.metadata or {}
        clip = lambda key: (meta.get(key) or "")[:1000]
        version = (meta.get("format") or "").replace("PDF", "").strip()
        try:
            has_xmp = bool(doc.xref_xml_metadata())
        except Exception:
            has_xmp = False
        return {"Title": clip("title"), "Author": clip("author"), "Subject": clip("subject"), "Keywords": clip("keywords"),
                "Creator": clip("creator"), "Producer": clip("producer"), "CreationDate": clip("creationDate"),
                "ModDate": clip("modDate"), "PdfVersion": version[:16], "PageCount": doc.page_count,
                "Encrypted": bool(doc.is_encrypted), "HasXmp": has_xmp}
    finally:
        doc.close()


def _set_properties(request):
    strip = _bool(request, "StripMetadata", False)
    values = {} if strip else {n: _text(request, n, 0, 1000, optional=True) for n in ("Title", "Author", "Subject", "Keywords")}
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        if strip:
            doc.set_metadata({})
            try:
                doc.del_xml_metadata()
            except Exception:
                pass
            kind, value = doc.xref_get_key(-1, "Info")
            if kind != "null":
                doc.xref_set_key(-1, "Info", "null")
        else:
            meta = dict(doc.metadata or {})
            meta.pop("format", None)
            meta.pop("encryption", None)
            for name, value in values.items():
                if value is not None:
                    meta[name.lower()] = value
            meta["modDate"] = pymupdf.get_pdf_now()
            doc.set_metadata(meta)
        _write(doc, output, garbage=1, deflate=True)
    finally:
        doc.close()
    return {"Applied": True, "Stripped": strip}


def _bounded(items):
    kept, used = [], 0
    for item in items:
        used += len(json.dumps(item, ensure_ascii=True)) + 2
        if used > RESPONSE_BUDGET:
            return kept, True
        kept.append(item)
    return kept, False


def _get_bookmarks(request):
    doc = _open(request)
    try:
        toc = doc.get_toc(simple=True)
        items = []
        for level, title, page in toc[:MAX_BOOKMARKS]:
            items.append({"Title": CONTROL.sub(" ", title)[:500], "Page": page - 1 if page >= 1 else -1, "Level": level})
        kept, cut = _bounded(items)
        return {"Bookmarks": kept, "Count": len(toc), "Truncated": cut or len(toc) > MAX_BOOKMARKS}
    finally:
        doc.close()


def _set_bookmarks(request):
    items = request.get("Bookmarks")
    if not isinstance(items, list) or len(items) > MAX_BOOKMARKS:
        raise Refused(f"The outline can hold at most {MAX_BOOKMARKS} bookmarks.")
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        toc, previous = [], 0
        for item in items:
            if not isinstance(item, dict):
                raise Refused("A bookmark is malformed.")
            title = _text(item, "Title", 1, 500)
            level = _int(item, "Level", 1, 32)
            page = _int(item, "Page", -1, doc.page_count - 1)
            if level > previous + 1:
                raise Refused("A bookmark level can be at most one deeper than the bookmark before it, starting at level 1.")
            previous = level
            toc.append([level, title, page + 1 if page >= 0 else -1])
        doc.set_toc(toc, collapse=0)
        _write(doc, output, garbage=1, deflate=True)
    finally:
        doc.close()
    return {"Applied": True, "Count": len(toc)}


def _extract_images(request):
    directory = request.get("OutputDirectory")
    if not isinstance(directory, str) or not directory:
        raise Refused("Choose a new folder for the images.")
    if os.path.exists(directory):
        raise Refused("The image folder already exists; choose a new folder.")
    if not os.path.isdir(os.path.dirname(os.path.abspath(directory))):
        raise Refused("The parent of the image folder does not exist.")
    doc = _open(request)
    created = False
    try:
        pages = _pages(request, doc.page_count)
        os.mkdir(directory)
        created = True
        seen, names, count, total, truncated = set(), [], 0, 0, False
        for index in pages:
            for image in doc[index].get_images(full=True):
                xref = image[0]
                if xref in seen:
                    continue
                seen.add(xref)
                if count >= MAX_IMAGES:
                    truncated = True
                    break
                try:
                    data = doc.extract_image(xref)
                except Exception:
                    continue
                if not data or not data.get("image"):
                    continue
                total += len(data["image"])
                if total > MAX_IMAGE_BYTES:
                    truncated = True
                    break
                extension = {"jpeg": "jpg"}.get(data.get("ext", "png"), data.get("ext", "png"))
                if not re.fullmatch(r"[a-z0-9]{1,5}", extension):
                    extension = "png"
                name = f"image-{count + 1:04d}-page{index + 1}.{extension}"
                with open(os.path.join(directory, name), "xb") as handle:
                    handle.write(data["image"])
                count += 1
                if len(names) < MAX_IMAGE_NAMES:
                    names.append(name)
            if truncated:
                break
        if count == 0:
            shutil.rmtree(directory, ignore_errors=True)
            created = False
            raise Refused("The selected pages contain no extractable images.")
        created = False
        return {"Count": count, "Names": names, "Truncated": truncated}
    except BaseException:
        if created:
            shutil.rmtree(directory, ignore_errors=True)
        raise
    finally:
        doc.close()


# --------------------------------------------------------------------------- links

def _annot_refs(doc, page):
    kind, value = doc.xref_get_key(page.xref, "Annots")
    if kind == "null":
        return []
    if kind == "xref":
        container = int(value.split()[0])
        text = doc.xref_object(container, compressed=True)
        direct = False
    elif kind == "array":
        text, container, direct = value, None, True
    else:
        raise Refused("The annotations of this page are not in a supported layout.")
    if "<<" in text:
        raise Refused("The annotations of this page are not in a supported layout.")
    return [int(m) for m in re.findall(r"(\d+)\s+0\s+R", text)]


def _write_refs(doc, page, refs):
    text = "[" + " ".join(f"{x} 0 R" for x in refs) + "]"
    kind, value = doc.xref_get_key(page.xref, "Annots")
    if kind == "xref":
        doc.update_object(int(value.split()[0]), text)
    else:
        doc.xref_set_key(page.xref, "Annots", text)


def _link_refs(doc, page):
    result = []
    for xref in _annot_refs(doc, page):
        kind, value = doc.xref_get_key(xref, "Subtype")
        if kind == "name" and value == "/Link":
            result.append(xref)
    return result


def _link_rect(doc, xref):
    kind, value = doc.xref_get_key(xref, "Rect")
    numbers = [float(v) for v in re.findall(r"-?\d+(?:\.\d+)?", value)] if kind == "array" else []
    if len(numbers) != 4:
        return None
    return (min(numbers[0], numbers[2]), min(numbers[1], numbers[3]), max(numbers[0], numbers[2]), max(numbers[1], numbers[3]))


def _link_target(doc, xref, page_xrefs, names):
    action = doc.xref_get_key(xref, "A/S")
    if action[0] == "name" and action[1] == "/URI":
        kind, value = doc.xref_get_key(xref, "A/URI")
        if kind == "string":
            return "Uri", value[:2000], -1
        return "Other", "", -1
    if action[0] == "name" and action[1] == "/GoTo":
        destination = doc.xref_get_key(xref, "A/D")
    elif action[0] == "null":
        destination = doc.xref_get_key(xref, "Dest")
    else:
        return "Other", "", -1
    kind, value = destination
    if kind == "array":
        match = re.match(r"\[\s*(\d+)\s+0\s+R", value)
        if match and int(match.group(1)) in page_xrefs:
            return "Page", "", page_xrefs[int(match.group(1))]
    elif kind in ("string", "name"):
        key = value.lstrip("/")
        target = names().get(key)
        if target and isinstance(target.get("page"), int) and 0 <= target["page"] < len(page_xrefs):
            return "Page", "", target["page"]
    return "Other", "", -1


def _list_links(request):
    doc = _open(request)
    try:
        page = doc[_page_index(request, doc.page_count)]
        page_xrefs = {doc.page_xref(i): i for i in range(doc.page_count)}
        cache = {}
        def names():
            if "n" not in cache:
                try:
                    cache["n"] = doc.resolve_names() or {}
                except Exception:
                    cache["n"] = {}
            return cache["n"]
        items, total = [], 0
        for index, xref in enumerate(_link_refs(doc, page)):
            total += 1
            rect = _link_rect(doc, xref)
            if rect is None or len(items) >= MAX_LINKS:
                continue
            kind, uri, target = _link_target(doc, xref, page_xrefs, names)
            items.append({"Index": index, "Left": rect[0], "Bottom": rect[1], "Right": rect[2], "Top": rect[3],
                          "Kind": kind, "Uri": uri, "TargetPage": target})
        kept, cut = _bounded(items)
        return {"Links": kept, "Count": total, "Truncated": cut or total > MAX_LINKS}
    finally:
        doc.close()


def _add_link(request):
    left, bottom, right, top = _rect(request)
    if right - left < 1 or top - bottom < 1:
        raise Refused("The link area must be at least 1 by 1 point.")
    uri = request.get("Uri")
    target = request.get("TargetPage")
    if (uri is None) == (target is None):
        raise Refused("Provide either a web address or a target page.")
    if uri is not None:
        if not isinstance(uri, str) or not 1 <= len(uri) <= 2000 or not all(0x21 <= ord(c) < 0x7F for c in uri):
            raise Refused("The link address must be plain ASCII text of at most 2000 characters.")
        if not re.match(r"(?i)(https?://|mailto:)\S", uri):
            raise Refused("Only http, https and mailto links are allowed.")
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        page = doc[_page_index(request, doc.page_count)]
        if uri is None:
            target = _int(request, "TargetPage", 0, doc.page_count - 1)
            action = f"/Dest [{doc.page_xref(target)} 0 R /Fit]"
        else:
            action = "/A << /S /URI /URI <" + uri.encode("ascii").hex() + "> >>"
        refs = _annot_refs(doc, page)
        xref = doc.get_new_xref()
        doc.update_object(xref, f"<< /Type /Annot /Subtype /Link /Rect [{left:.4f} {bottom:.4f} {right:.4f} {top:.4f}] "
                                f"/Border [0 0 0] /F 4 {action} >>")
        _write_refs(doc, page, refs + [xref])
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True}


def _remove_link(request):
    left, bottom, right, top = _rect(request)
    output = _check_output(request)
    doc = _open(request, allow_password=False)
    try:
        if doc.is_encrypted:
            raise Refused("Encrypted PDFs cannot be edited.")
        page = doc[_page_index(request, doc.page_count)]
        index = _int(request, "Index", 0, 100_000)
        links = _link_refs(doc, page)
        rect = _link_rect(doc, links[index]) if index < len(links) else None
        if rect is None or any(abs(a - b) > 0.5 for a, b in zip(rect, (left, bottom, right, top))):
            raise Refused("The link changed since it was listed. Refresh and try again.")
        _write_refs(doc, page, [x for x in _annot_refs(doc, page) if x != links[index]])
        _save_edit(doc, output)
    finally:
        doc.close()
    return {"Applied": True}


HANDLERS = {
    "Compress": _compress, "Protect": _protect, "Unprotect": _unprotect, "Watermark": _watermark,
    "HeaderFooter": _header_footer, "Bates": _bates, "Crop": _crop, "Flatten": _flatten,
    "GetProperties": _properties, "SetProperties": _set_properties, "GetBookmarks": _get_bookmarks,
    "SetBookmarks": _set_bookmarks, "ExtractImages": _extract_images, "ListLinks": _list_links,
    "AddLink": _add_link, "RemoveLink": _remove_link,
}


def handle(operation: str, request: dict) -> dict:
    handler = HANDLERS.get(operation)
    if handler is None:
        raise Refused("Unsupported PDF operation.")
    try:
        return handler(request)
    except Refused:
        raise
    except (FileExistsError, FileNotFoundError, PermissionError, OSError):
        raise Refused("The file or folder could not be created or read.") from None
