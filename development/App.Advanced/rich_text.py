"""Editable FreeText boxes (base-14 fonts, WinAnsi) and embedded Unicode page text (TrueType/OpenType subset)."""
from __future__ import annotations

import hashlib
import json
import math
import os
import re
import struct
import uuid
import zlib

import pymupdf

pymupdf.TOOLS.mupdf_display_errors(False)
pymupdf.TOOLS.mupdf_display_warnings(False)

MAX_FONT_BYTES = 25 * 1024 * 1024
MAX_LIST_ITEMS = 1000
MAX_LIST_BYTES = 56 * 1024
FLAG_PRINT = 4
LOCK_FLAGS = 64 | 128 | 512  # ReadOnly, Locked, LockedContents
TOL = 0.01
PAD = 2.0
LEAD = 1.2
BASELINE = 0.95


class Refused(ValueError):
    pass


# ---------------------------------------------------------------- validation helpers
def _num(value, name, low, high, integer=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise Refused(f"{name} must be a number.")
    value = float(value)
    if not math.isfinite(value) or value < low or value > high:
        raise Refused(f"{name} is outside the supported range.")
    if integer and value != int(value):
        raise Refused(f"{name} must be a whole number.")
    return int(value) if integer else value


def _bool(value, name):
    if not isinstance(value, bool):
        raise Refused(f"{name} must be true or false.")
    return value


def _color(request, prefix, default=None):
    keys = [prefix + part for part in ("Red", "Green", "Blue")]
    present = [key for key in keys if request.get(key) is not None]
    if not present:
        return default
    if len(present) != 3:
        raise Refused(f"{prefix} color needs red, green and blue together.")
    return tuple(_num(request[key], key, 0, 255, True) for key in keys)


def _color_ops(color, stroke=False):
    return " ".join(f"{channel / 255:.4f}".rstrip("0").rstrip(".") or "0" for channel in color) + (" RG" if stroke else " rg")


def _page_index(doc, value):
    page = _num(value, "Page", 0, 10_000_000, True)
    if page >= doc.page_count:
        raise Refused("The selected page is outside the document.")
    return page


def _open(request):
    path = request.get("Input")
    if not isinstance(path, str):
        raise Refused("The PDF operation request is malformed.")
    try:
        doc = pymupdf.open(path)
    except Exception as exc:
        raise Refused("The PDF could not be opened by the advanced engine.") from exc
    if doc.is_encrypted or doc.needs_pass:
        doc.close()
        raise Refused("Encrypted PDFs are not supported by this operation.")
    if not doc.is_pdf:
        doc.close()
        raise Refused("The document is not a PDF.")
    return doc


def _save(doc, request):
    out = request.get("Output")
    if not isinstance(out, str):
        raise Refused("The PDF operation request is malformed.")
    if os.path.exists(out):
        raise Refused("The output file already exists.")
    doc.save(out, garbage=0, deflate=False)


# ---------------------------------------------------------------- page geometry
def _inherited(doc, page_xref, key):
    xref, seen = page_xref, set()
    while xref and xref not in seen and len(seen) < 64:
        seen.add(xref)
        kind, value = doc.xref_get_key(xref, key)
        if kind != "null":
            return xref, kind, value
        kind, value = doc.xref_get_key(xref, "Parent")
        xref = int(value.split()[0]) if kind == "xref" else 0
    return 0, "null", ""


def _box(doc, page_xref, key):
    _, kind, value = _inherited(doc, page_xref, key)
    if kind != "array":
        return None
    try:
        a = [float(item) for item in value.strip("[]").split()]
    except ValueError:
        raise Refused("The page has a malformed page box.")
    if len(a) != 4 or not all(math.isfinite(x) for x in a):
        raise Refused("The page has a malformed page box.")
    return (min(a[0], a[2]), min(a[1], a[3]), max(a[0], a[2]), max(a[1], a[3]))


class Geometry:
    """Visible page box (CropBox within MediaBox) and the display transform for /Rotate."""

    def __init__(self, doc, page_xref):
        media = _box(doc, page_xref, "MediaBox") or (0.0, 0.0, 612.0, 792.0)
        crop = _box(doc, page_xref, "CropBox")
        if crop is not None:
            crop = (max(crop[0], media[0]), max(crop[1], media[1]), min(crop[2], media[2]), min(crop[3], media[3]))
            if crop[2] - crop[0] < 1 or crop[3] - crop[1] < 1:
                crop = media
        self.x0, self.y0, self.x1, self.y1 = crop or media
        _, kind, value = _inherited(doc, page_xref, "Rotate")
        try:
            rotation = int(float(value)) if kind in ("int", "real", "float") else 0
        except ValueError:
            rotation = 0
        if rotation % 90:
            raise Refused("The page has an unsupported rotation.")
        self.rotation = rotation % 360
        if self.x1 - self.x0 < 20 or self.y1 - self.y0 < 20:
            raise Refused("The page is too small for this operation.")
        w, h = self.x1 - self.x0, self.y1 - self.y0
        self.disp_w, self.disp_h = (w, h) if self.rotation in (0, 180) else (h, w)

    def to_display(self, x, y):
        r = self.rotation
        if r == 0: return x - self.x0, self.y1 - y
        if r == 90: return y - self.y0, x - self.x0
        if r == 180: return self.x1 - x, y - self.y0
        return self.y1 - y, self.x1 - x

    def to_user(self, dx, dy):
        r = self.rotation
        if r == 0: return self.x0 + dx, self.y1 - dy
        if r == 90: return self.x0 + dy, self.y0 + dx
        if r == 180: return self.x1 - dx, self.y0 + dy
        return self.x1 - dy, self.y1 - dx

    @property
    def matrix(self):
        c, s = {0: (1, 0), 90: (0, 1), 180: (-1, 0), 270: (0, -1)}[self.rotation]
        return c, s, -s, c

    def display_rect(self, left, bottom, right, top):
        points = [self.to_display(x, y) for x, y in ((left, bottom), (right, top))]
        return (min(p[0] for p in points), min(p[1] for p in points), max(p[0] for p in points), max(p[1] for p in points))

    def user_rect(self, dl, dt, dr, db):
        points = [self.to_user(x, y) for x, y in ((dl, dt), (dr, db))]
        return (min(p[0] for p in points), min(p[1] for p in points), max(p[0] for p in points), max(p[1] for p in points))

    def contains_user(self, left, bottom, right, top):
        return left >= self.x0 - TOL and bottom >= self.y0 - TOL and right <= self.x1 + TOL and top <= self.y1 + TOL


# ---------------------------------------------------------------- FreeText fonts and layout
FAMILIES = {"Helvetica": ("helv", "hebo", "heit", "hebi"), "Times": ("tiro", "tibo", "tiit", "tibi"), "Courier": ("cour", "cobo", "coit", "cobi")}
PDF_NAMES = {"helv": "Helvetica", "hebo": "Helvetica-Bold", "heit": "Helvetica-Oblique", "hebi": "Helvetica-BoldOblique",
             "tiro": "Times-Roman", "tibo": "Times-Bold", "tiit": "Times-Italic", "tibi": "Times-BoldItalic",
             "cour": "Courier", "cobo": "Courier-Bold", "coit": "Courier-Oblique", "cobi": "Courier-BoldOblique"}
DA_NAMES = {"helv": "Helv", "hebo": "HeBo", "heit": "HeOb", "hebi": "HeBO", "tiro": "TiRo", "tibo": "TiBo", "tiit": "TiIt",
            "tibi": "TiBI", "cour": "Cour", "cobo": "CoBo", "coit": "CoOb", "cobi": "CoBO"}
DA_LOOKUP = {name.lower(): key for key, name in DA_NAMES.items()}
_fonts: dict[str, pymupdf.Font] = {}


def _font_key(family, bold, italic):
    if family not in FAMILIES:
        raise Refused("The font must be Helvetica, Times or Courier.")
    return FAMILIES[family][(1 if bold else 0) + (2 if italic else 0)]


def _text_width(key, text, size):
    font = _fonts.get(key)
    if font is None:
        font = _fonts[key] = pymupdf.Font(key)
    return font.text_length(text, size)


def _winansi(text):
    out = bytearray()
    for ch in text:
        code = ord(ch)
        if code < 32 or code == 127:
            raise Refused("The text contains a control character that cannot be placed in a text box.")
        try:
            byte = ch.encode("cp1252")
        except UnicodeEncodeError:
            raise Refused("A text box supports Latin and Western European characters only; "
                          "use Add Unicode text for other scripts.") from None
        if byte[0] in (0x81, 0x8D, 0x8F, 0x90, 0x9D):
            raise Refused("The text contains a character that cannot be placed in a text box.")
        out += byte
    return bytes(out)


def _pdf_string(raw):
    parts = []
    for byte in raw:
        if byte in (0x28, 0x29, 0x5C):
            parts.append("\\" + chr(byte))
        elif 32 <= byte < 127:
            parts.append(chr(byte))
        else:
            parts.append(f"\\{byte:03o}")
    return "(" + "".join(parts) + ")"


def _normalise_newlines(text):
    return text.replace("\r\n", "\n").replace("\r", "\n")


def _wrap(text, key, size, width):
    lines = []
    for paragraph in _normalise_newlines(text).split("\n"):
        current = ""
        for word in re.split(r"( )", paragraph):
            if word == "":
                continue
            trial = current + word
            if _text_width(key, trial.rstrip(" "), size) <= width + 1e-6:
                current = trial
                continue
            if word == " ":
                lines.append(current.rstrip(" "))
                current = ""
                continue
            if current.strip(" "):
                lines.append(current.rstrip(" "))
                current = ""
            while _text_width(key, word, size) > width + 1e-6:
                cut = len(word) - 1
                while cut > 0 and _text_width(key, word[:cut], size) > width + 1e-6:
                    cut -= 1
                if cut == 0:
                    raise Refused("The box is too narrow for the text at this size.")
                lines.append(word[:cut])
                word = word[cut:]
            current = word
        lines.append(current.rstrip(" "))
    return lines


class Spec:
    """Everything needed to render one FreeText annotation."""

    def __init__(self):
        self.text = ""
        self.family, self.bold, self.italic = "Helvetica", False, False
        self.size = 12.0
        self.color = (0, 0, 0)
        self.align = 0
        self.border = 0.0
        self.border_color = None
        self.fill = None
        self.rect = None  # user space (left, bottom, right, top)


def _spec_from_request(request, base: Spec | None, creating: bool):
    spec = Spec()
    if base is not None:
        spec.__dict__.update(base.__dict__)
    if "Text" in request and request["Text"] is not None or creating:
        text = request.get("Text")
        if not isinstance(text, str):
            raise Refused("The text must be a string.")
        text = _normalise_newlines(text)
        if not (1 <= len(text) <= 4000) or not text.strip():
            raise Refused("The text must be between 1 and 4000 characters.")
        spec.text = text
    if request.get("Font") is not None:
        if request["Font"] not in FAMILIES:
            raise Refused("The font must be Helvetica, Times or Courier.")
        spec.family = request["Font"]
    elif creating:
        spec.family = "Helvetica"
    if request.get("Bold") is not None: spec.bold = _bool(request["Bold"], "Bold")
    if request.get("Italic") is not None: spec.italic = _bool(request["Italic"], "Italic")
    if request.get("FontSize") is not None: spec.size = _num(request["FontSize"], "FontSize", 6, 144)
    spec.color = _color(request, "", spec.color) if creating or any(request.get(k) is not None for k in ("Red", "Green", "Blue")) else spec.color
    if request.get("Align") is not None:
        if request["Align"] not in ("Left", "Center", "Right"):
            raise Refused("Alignment must be Left, Center or Right.")
        spec.align = ("Left", "Center", "Right").index(request["Align"])
    if request.get("BorderWidth") is not None: spec.border = _num(request["BorderWidth"], "BorderWidth", 0, 12)
    border_color = _color(request, "Border")
    if border_color is not None: spec.border_color = border_color
    if request.get("ClearBorderColor") is True: spec.border_color = None
    fill = _color(request, "Fill")
    if fill is not None: spec.fill = fill
    if request.get("ClearFill") is True: spec.fill = None
    if spec.border > 0 and spec.border_color is None: spec.border_color = (0, 0, 0)
    _winansi(spec.text.replace("\n", ""))
    return spec


def _fit(spec, geometry):
    """Wrap text for spec.rect; grow the box downward on the page if needed. Returns (lines, display rect, grown)."""
    left, bottom, right, top = spec.rect
    dl, dt, dr, db = geometry.display_rect(left, bottom, right, top)
    pad = PAD + spec.border
    key = _font_key(spec.family, spec.bold, spec.italic)
    inner = (dr - dl) - 2 * pad
    if inner < spec.size * 0.6:
        raise Refused("The box is too narrow for the text at this size.")
    lines = _wrap(spec.text, key, spec.size, inner)
    needed = 2 * pad + len(lines) * spec.size * LEAD
    grown = False
    if db - dt < needed - 1e-6:
        if dt + needed > geometry.disp_h + TOL:
            raise Refused("The text does not fit in the box or on the page; enlarge the box, shorten the text or use a smaller font size.")
        db = dt + needed
        grown = True
    return lines, (dl, dt, dr, db), grown


def _appearance(spec, lines, drect, geometry):
    dl, dt, dr, db = drect
    w, h = dr - dl, db - dt
    key = _font_key(spec.family, spec.bold, spec.italic)
    pad = PAD + spec.border
    ops = ["q"]
    if spec.fill is not None:
        ops.append(f"{_color_ops(spec.fill)} 0 0 {w:.4f} {h:.4f} re f")
    if spec.border > 0:
        half = spec.border / 2
        ops.append(f"{spec.border:.4f} w {_color_ops(spec.border_color, True)} {half:.4f} {half:.4f} {w - spec.border:.4f} {h - spec.border:.4f} re S")
    ops.append(f"BT /F1 {spec.size:.4f} Tf {_color_ops(spec.color)}")
    previous = (0.0, 0.0)
    for index, line in enumerate(lines):
        if not line:
            continue
        width = _text_width(key, line, spec.size)
        x = pad if spec.align == 0 else (w - pad - width if spec.align == 2 else (w - width) / 2)
        y = h - pad - BASELINE * spec.size - index * spec.size * LEAD
        ops.append(f"{x - previous[0]:.4f} {y - previous[1]:.4f} Td {_pdf_string(_winansi(line))} Tj")
        previous = (x, y)
    ops.append("ET Q")
    a, b, c, d = geometry.matrix
    dictionary = (f"<</Type/XObject/Subtype/Form/BBox[0 0 {w:.4f} {h:.4f}]/Matrix[{a} {b} {c} {d} 0 0]"
                  f"/Resources<</Font<</F1<</Type/Font/Subtype/Type1/BaseFont/{PDF_NAMES[key]}/Encoding/WinAnsiEncoding>>>>>>>>")
    return dictionary, "\n".join(ops).encode("latin-1")


def _hex_text(text):
    return "<FEFF" + text.replace("\n", "\r").encode("utf-16-be").hex().upper() + ">"


def _da(spec):
    key = _font_key(spec.family, spec.bold, spec.italic)
    return f"/{DA_NAMES[key]} {spec.size:g} Tf {_color_ops(spec.color)}"


def _rect_str(rect):
    return "[" + " ".join(f"{v:.4f}".rstrip("0").rstrip(".") or "0" for v in rect) + "]"


def _write_appearance(doc, annot_xref, dictionary, stream):
    kind, value = doc.xref_get_key(annot_xref, "AP/N")
    if kind == "xref":
        ap = int(value.split()[0])
        kind2, _ = doc.xref_get_key(ap, "Subtype")
        if kind2 == "name" and doc.xref_is_stream(ap):
            doc.update_object(ap, dictionary)
            doc.update_stream(ap, stream)
            return ap
    ap = doc.get_new_xref()
    doc.update_object(ap, dictionary)
    doc.update_stream(ap, stream)
    doc.xref_set_key(annot_xref, "AP", f"<</N {ap} 0 R>>")
    return ap


# ---------------------------------------------------------------- reading FreeText annotations
def _freetext_xrefs(doc, page):
    out = []
    try:
        for annot in page.annots(types=[pymupdf.PDF_ANNOT_FREE_TEXT]):
            out.append(annot.xref)
    except Exception:
        pass
    return out


def _parse_da(doc, xref):
    kind, da = doc.xref_get_key(xref, "DA")
    if kind != "string":
        da = ""
    family, bold, italic, size = "Helvetica", False, False, 12.0
    color = (0, 0, 0)
    match = re.search(r"/([^\s/\[\]()<>]+)\s+(-?[\d.]+)\s+Tf", da)
    if match:
        name = match.group(1)
        try:
            size = float(match.group(2))
        except ValueError:
            size = 12.0
        base = None
        if name.lower() in DA_LOOKUP:
            base = PDF_NAMES[DA_LOOKUP[name.lower()]]
        else:
            for path in (f"AcroForm/DR/Font/{name}",):
                try:
                    cat = doc.pdf_catalog()
                    k, v = doc.xref_get_key(cat, path)
                    if k == "xref":
                        k2, v2 = doc.xref_get_key(int(v.split()[0]), "BaseFont")
                        if k2 == "name":
                            base = v2.lstrip("/")
                    elif k == "dict":
                        pass
                except Exception:
                    pass
        label = (base or name).lower()
        if "cour" in label or "mono" in label or "consolas" in label:
            family = "Courier"
        elif "tim" in label or "serif" in label and "sans" not in label or "roman" in label or "georgia" in label:
            family = "Times"
        bold = "bold" in label or label.endswith("bo") or "-bo" in label or "heb" in label
        italic = any(token in label for token in ("italic", "oblique", "ital"))
    if not (0 < size <= 1000):
        size = 12.0
    rg = re.search(r"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+rg", da)
    g = re.search(r"([\d.]+)\s+g(?![a-zA-Z])", da)
    k = re.search(r"([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+k(?![a-zA-Z])", da)
    try:
        if rg:
            color = tuple(round(min(1.0, max(0.0, float(rg.group(i)))) * 255) for i in (1, 2, 3))
        elif k:
            c, m, y, kk = (float(k.group(i)) for i in (1, 2, 3, 4))
            color = tuple(round(255 * (1 - min(1.0, v + kk))) for v in (c, m, y))
        elif g:
            v = round(min(1.0, max(0.0, float(g.group(1)))) * 255)
            color = (v, v, v)
    except ValueError:
        color = (0, 0, 0)
    return family, bold, italic, size, color


def _color_array(doc, xref, key):
    kind, value = doc.xref_get_key(xref, key)
    if kind != "array":
        return None
    try:
        parts = [float(x) for x in value.strip("[]").split()]
    except ValueError:
        return None
    if len(parts) == 1: parts = parts * 3
    if len(parts) == 4: parts = [1 - min(1.0, parts[i] + parts[3]) for i in range(3)]
    if len(parts) != 3: return None
    return tuple(round(min(1.0, max(0.0, p)) * 255) for p in parts)


def _info(doc, xref, page_index):
    kind, value = doc.xref_get_key(xref, "Rect")
    if kind != "array":
        return None
    try:
        r = [float(x) for x in value.strip("[]").split()]
    except ValueError:
        return None
    if len(r) != 4 or not all(math.isfinite(x) for x in r):
        return None
    kind, text = doc.xref_get_key(xref, "Contents")
    text = _normalise_newlines(text) if kind == "string" else ""
    family, bold, italic, size, color = _parse_da(doc, xref)
    kind, q = doc.xref_get_key(xref, "Q")
    align = ("Left", "Center", "Right")[int(q)] if kind == "int" and q in ("0", "1", "2") else "Left"
    kind, f = doc.xref_get_key(xref, "F")
    flags = int(f) if kind == "int" else 0
    border = 0.0
    kind, w = doc.xref_get_key(xref, "BS/W")
    if kind in ("int", "real", "float"):
        border = float(w)
    else:
        kind, arr = doc.xref_get_key(xref, "Border")
        if kind == "array":
            try:
                border = float(arr.strip("[]").split()[2])
            except (ValueError, IndexError):
                border = 0.0
    border_color = _color_array(doc, xref, "C") if border > 0 else None
    fill = _color_array(doc, xref, "IC")
    return {"Id": xref, "Page": page_index, "Left": min(r[0], r[2]), "Bottom": min(r[1], r[3]), "Right": max(r[0], r[2]),
            "Top": max(r[1], r[3]), "Text": text, "Font": family, "Bold": bold, "Italic": italic, "FontSize": size,
            "Red": color[0], "Green": color[1], "Blue": color[2], "Align": align, "Locked": bool(flags & LOCK_FLAGS),
            "BorderWidth": border,
            "BorderRed": None if border_color is None else border_color[0], "BorderGreen": None if border_color is None else border_color[1],
            "BorderBlue": None if border_color is None else border_color[2],
            "FillRed": None if fill is None else fill[0], "FillGreen": None if fill is None else fill[1],
            "FillBlue": None if fill is None else fill[2]}


def _find(doc, request):
    """Resolve Id (+ optional Page hint) to (page, info). Guards are enforced here."""
    xref = _num(request.get("Id"), "Id", 1, 1 << 31, True)
    hint = request.get("Page")
    pages = [_page_index(doc, hint)] if hint is not None else range(doc.page_count)
    for index in pages:
        page = doc[index]
        if xref in _freetext_xrefs(doc, page):
            info = _info(doc, xref, index)
            if info is None:
                raise Refused("The text box is malformed.")
            return page, info
    raise Refused("The text box no longer exists. Refresh the list and try again.")


def _guard(request, info):
    expected = request.get("ExpectedText")
    if not isinstance(expected, str) or _normalise_newlines(expected) != info["Text"]:
        raise Refused("The text box changed since it was listed. Refresh and try again.")
    for name, key in (("ExpectedLeft", "Left"), ("ExpectedBottom", "Bottom"), ("ExpectedRight", "Right"), ("ExpectedTop", "Top")):
        value = _num(request.get(name), name, -1e7, 1e7)
        if abs(value - info[key]) > 0.05:
            raise Refused("The text box moved since it was listed. Refresh and try again.")


def _rect_from(request, prefix=""):
    names = [prefix + n for n in ("Left", "Bottom", "Right", "Top")]
    present = [n for n in names if request.get(n) is not None]
    if not present:
        return None
    if len(present) != 4:
        raise Refused("The box needs left, bottom, right and top together.")
    left, bottom, right, top = (_num(request[n], n, -1e7, 1e7) for n in names)
    if right - left < 12 or top - bottom < 8:
        raise Refused("The text box is too small.")
    return (left, bottom, right, top)


def _spec_from_info(info):
    spec = Spec()
    spec.text, spec.family, spec.bold, spec.italic, spec.size = info["Text"], info["Font"], info["Bold"], info["Italic"], info["FontSize"]
    spec.color = (info["Red"], info["Green"], info["Blue"])
    spec.align = ("Left", "Center", "Right").index(info["Align"])
    spec.border = info["BorderWidth"]
    if info["BorderRed"] is not None: spec.border_color = (info["BorderRed"], info["BorderGreen"], info["BorderBlue"])
    if info["FillRed"] is not None: spec.fill = (info["FillRed"], info["FillGreen"], info["FillBlue"])
    spec.rect = (info["Left"], info["Bottom"], info["Right"], info["Top"])
    return spec


def _result(xref, page_index, rect, grown):
    return {"Applied": True, "Id": xref, "Page": page_index, "Left": rect[0], "Bottom": rect[1], "Right": rect[2], "Top": rect[3], "Grown": grown}


def _check_text_fits_page(spec, geometry):
    if not geometry.contains_user(*spec.rect):
        raise Refused("The box must lie inside the visible page.")


# ---------------------------------------------------------------- operations: FreeText
def add_free_text(request):
    doc = _open(request)
    try:
        index = _page_index(doc, request.get("Page"))
        page = doc[index]
        geometry = Geometry(doc, page.xref)
        spec = _spec_from_request(request, None, True)
        spec.rect = _rect_from(request)
        if spec.rect is None:
            raise Refused("The box needs left, bottom, right and top.")
        _check_text_fits_page(spec, geometry)
        lines, drect, grown = _fit(spec, geometry)
        rect = geometry.user_rect(*drect)
        rect = (max(rect[0], geometry.x0), max(rect[1], geometry.y0), min(rect[2], geometry.x1), min(rect[3], geometry.y1))
        annot = page.add_freetext_annot(pymupdf.Rect(10, 10, 100, 40), "x")
        xref = annot.xref
        annot = None
        entries = [f"/Type/Annot/Subtype/FreeText/Rect{_rect_str(rect)}/Contents{_hex_text(spec.text)}",
                   f"/DA({_da(spec)})/Q {spec.align}/F {FLAG_PRINT}/P {page.xref} 0 R/NM({_new_name()})",
                   f"/BS<</Type/Border/W {spec.border:g}/S/S>>"]
        if spec.border > 0: entries.append(f"/C[{_color_ops(spec.border_color)[:-3]}]")
        if spec.fill is not None: entries.append(f"/IC[{_color_ops(spec.fill)[:-3]}]")
        doc.update_object(xref, "<<" + "".join(entries) + ">>")
        dictionary, stream = _appearance(spec, lines, drect, geometry)
        doc.xref_set_key(xref, "AP", "null")
        _write_appearance(doc, xref, dictionary, stream)
        _save(doc, request)
        return _result(xref, index, rect, grown)
    finally:
        doc.close()


def _new_name():
    return "hpdf-" + uuid.uuid4().hex[:16]


def list_free_text(request):
    doc = _open(request)
    try:
        page = request.get("Page")
        indexes = [_page_index(doc, page)] if page is not None else range(doc.page_count)
        items, size, truncated = [], 2, False
        for index in indexes:
            for xref in _freetext_xrefs(doc, doc[index]):
                info = _info(doc, xref, index)
                if info is None:
                    continue
                cost = len(json.dumps(info, ensure_ascii=True)) + 2
                if len(items) >= MAX_LIST_ITEMS or size + cost > MAX_LIST_BYTES:
                    truncated = True
                    break
                items.append(info)
                size += cost
            if truncated:
                break
        return {"Items": items, "Truncated": truncated}
    finally:
        doc.close()


def update_free_text(request):
    doc = _open(request)
    try:
        page, info = _find(doc, request)
        _guard(request, info)
        if info["Locked"]:
            raise Refused("This text box is locked and cannot be changed.")
        xref = info["Id"]
        kind, intent = doc.xref_get_key(xref, "IT")
        if kind == "name" and intent == "/FreeTextCallout":
            raise Refused("Callout text boxes cannot be edited here.")
        geometry = Geometry(doc, page.xref)
        base = _spec_from_info(info)
        spec = _spec_from_request(request, base, False)
        new_rect = _rect_from(request)
        if new_rect is not None:
            spec.rect = new_rect
        _check_text_fits_page(spec, geometry)
        lines, drect, grown = _fit(spec, geometry)
        rect = geometry.user_rect(*drect)
        rect = (max(rect[0], geometry.x0), max(rect[1], geometry.y0), min(rect[2], geometry.x1), min(rect[3], geometry.y1))
        doc.xref_set_key(xref, "Rect", _rect_str(rect))
        doc.xref_set_key(xref, "Contents", _hex_text(spec.text))
        doc.xref_set_key(xref, "DA", f"({_da(spec)})")
        doc.xref_set_key(xref, "Q", str(spec.align))
        doc.xref_set_key(xref, "BS", f"<</Type/Border/W {spec.border:g}/S/S>>")
        doc.xref_set_key(xref, "Border", "null")
        doc.xref_set_key(xref, "C", f"[{_color_ops(spec.border_color)[:-3]}]" if spec.border > 0 else "null")
        doc.xref_set_key(xref, "IC", f"[{_color_ops(spec.fill)[:-3]}]" if spec.fill is not None else "null")
        for stale in ("RC", "DS"):
            doc.xref_set_key(xref, stale, "null")
        dictionary, stream = _appearance(spec, lines, drect, geometry)
        _write_appearance(doc, xref, dictionary, stream)
        _save(doc, request)
        return _result(xref, info["Page"], rect, grown)
    finally:
        doc.close()


def delete_free_text(request):
    doc = _open(request)
    try:
        page, info = _find(doc, request)
        _guard(request, info)
        if info["Locked"]:
            raise Refused("This text box is locked and cannot be deleted.")
        for annot in page.annots(types=[pymupdf.PDF_ANNOT_FREE_TEXT]):
            if annot.xref == info["Id"]:
                page.delete_annot(annot)
                break
        else:
            raise Refused("The text box no longer exists. Refresh the list and try again.")
        _save(doc, request)
        return {"Applied": True, "Id": info["Id"], "Page": info["Page"]}
    finally:
        doc.close()


# ---------------------------------------------------------------- sfnt parsing and subsetting
def _u16(data, offset): return struct.unpack_from(">H", data, offset)[0]
def _i16(data, offset): return struct.unpack_from(">h", data, offset)[0]
def _u32(data, offset): return struct.unpack_from(">I", data, offset)[0]


class Face:
    def __init__(self, data, index):
        self.data = data
        try:
            base = 0
            if data[:4] == b"ttcf":
                count = _u32(data, 8)
                if not 0 <= index < count:
                    raise Refused("The font face index is not valid for this file.")
                base = _u32(data, 12 + 4 * index)
            elif index != 0:
                raise Refused("The font face index is not valid for this file.")
            self.version = data[base:base + 4]
            if self.version not in (b"\x00\x01\x00\x00", b"OTTO", b"true"):
                raise Refused("The font file is not a supported TrueType or OpenType font.")
            self.tables = {}
            for i in range(_u16(data, base + 4)):
                tag, _, offset, length = struct.unpack_from(">4sIII", data, base + 12 + 16 * i)
                if offset + length > len(data):
                    raise Refused("The font file is damaged.")
                self.tables[tag.decode("latin-1")] = (offset, length)
            for needed in ("head", "hhea", "maxp", "hmtx", "cmap"):
                if needed not in self.tables:
                    raise Refused("The font file is missing required tables.")
            self.upem = _u16(data, self.table("head") + 18) or 1000
            self.loca_format = _i16(data, self.table("head") + 50)
            self.num_glyphs = _u16(data, self.table("maxp") + 4)
            self.num_hmetrics = _u16(data, self.table("hhea") + 34)
            if self.num_hmetrics < 1 or self.num_hmetrics > self.num_glyphs:
                raise Refused("The font file is damaged.")
            self.has_glyf = "glyf" in self.tables and "loca" in self.tables
            self.is_cff = "CFF " in self.tables
            if not self.has_glyf and not self.is_cff:
                raise Refused("The font has no scalable outlines and cannot be embedded.")
            self.fs_type = _u16(data, self.table("OS/2") + 8) if "OS/2" in self.tables and self.length("OS/2") >= 10 else 0
            self._cmaps = self._read_cmaps()
        except (struct.error, IndexError):
            raise Refused("The font file is damaged.") from None

    def table(self, tag): return self.tables[tag][0]
    def length(self, tag): return self.tables[tag][1]
    def bytes_of(self, tag):
        offset, length = self.tables[tag]
        return self.data[offset:offset + length]

    def _read_cmaps(self):
        data, base = self.data, self.table("cmap")
        subtables = []
        for i in range(_u16(data, base + 2)):
            platform, encoding, offset = struct.unpack_from(">HHI", data, base + 4 + 8 * i)
            fmt = _u16(data, base + offset)
            if fmt in (4, 12) and ((platform, encoding) in ((3, 10), (3, 1), (0, 3), (0, 4), (0, 6), (0, 2), (0, 1), (0, 0))):
                priority = 0 if (platform, encoding) in ((3, 10), (0, 4), (0, 6)) and fmt == 12 else (1 if fmt == 12 else 2)
                subtables.append((priority, base + offset, fmt))
        if not subtables:
            raise Refused("The font has no Unicode character map.")
        subtables.sort()
        return [(offset, fmt) for _, offset, fmt in subtables]

    def glyph_for(self, code):
        data = self.data
        for offset, fmt in self._cmaps:
            gid = 0
            if fmt == 12:
                for g in range(_u32(data, offset + 12)):
                    start, end, first = struct.unpack_from(">III", data, offset + 16 + 12 * g)
                    if start <= code <= end:
                        gid = first + code - start
                        break
                    if code < start:
                        break
            elif code <= 0xFFFF:
                seg = _u16(data, offset + 6) // 2
                ends, starts = offset + 14, offset + 16 + 2 * seg
                deltas, ranges = starts + 2 * seg, starts + 4 * seg
                for s in range(seg):
                    end = _u16(data, ends + 2 * s)
                    if code > end:
                        continue
                    start = _u16(data, starts + 2 * s)
                    if code < start:
                        break
                    delta, range_offset = _u16(data, deltas + 2 * s), _u16(data, ranges + 2 * s)
                    if range_offset == 0:
                        gid = (code + delta) & 0xFFFF
                    else:
                        position = ranges + 2 * s + range_offset + 2 * (code - start)
                        if position + 2 <= len(data):
                            gid = _u16(data, position)
                            if gid:
                                gid = (gid + delta) & 0xFFFF
                    break
            if gid and gid < self.num_glyphs:
                return gid
        return 0

    def advance(self, gid):
        base = self.table("hmtx")
        position = min(gid, self.num_hmetrics - 1)
        return _u16(self.data, base + 4 * position)

    def names(self):
        if "name" not in self.tables:
            return ""
        data, base = self.data, self.table("name")
        count, storage = _u16(data, base + 2), _u16(data, base + 4)
        found = {}
        for i in range(count):
            platform, encoding, language, name_id, length, offset = struct.unpack_from(">HHHHHH", data, base + 6 + 12 * i)
            if name_id not in (4, 6) or name_id in found:
                continue
            raw = data[base + storage + offset: base + storage + offset + length]
            if platform in (0, 3):
                found[name_id] = raw.decode("utf-16-be", "ignore")
            elif platform == 1:
                found[name_id] = raw.decode("mac_roman", "ignore")
        return found.get(6) or found.get(4) or ""

    def metrics(self):
        d, s = self.data, 1000 / self.upem
        head, hhea = self.table("head"), self.table("hhea")
        bbox = [round(_i16(d, head + 36 + 2 * i) * s) for i in range(4)]
        ascent, descent = round(_i16(d, hhea + 4) * s), round(_i16(d, hhea + 6) * s)
        cap = round(ascent * 0.7)
        if "OS/2" in self.tables and self.length("OS/2") >= 90 and _u16(d, self.table("OS/2")) >= 2:
            cap = round(_i16(d, self.table("OS/2") + 88) * s) or cap
        italic = 0.0
        if "post" in self.tables and self.length("post") >= 8:
            italic = _i16(d, self.table("post") + 4) + _u16(d, self.table("post") + 6) / 65536
        return bbox, ascent, descent, cap, italic

    def cff_is_cid_keyed(self):
        data = self.bytes_of("CFF ")

        def index(position):
            count = _u16(data, position)
            if count == 0:
                return [], position + 2
            size = data[position + 2]
            offsets = [int.from_bytes(data[position + 3 + size * i: position + 3 + size * (i + 1)], "big") for i in range(count + 1)]
            start = position + 3 + size * (count + 1) - 1
            return [data[start + offsets[i]: start + offsets[i + 1]] for i in range(count)], start + offsets[-1]

        _, position = index(data[2])
        dicts, _ = index(position)
        if not dicts:
            raise Refused("The font file is damaged.")
        top, i = dicts[0], 0
        while i < len(top):
            b = top[i]
            if b <= 21:
                if b == 12 and i + 1 < len(top) and top[i + 1] == 30:
                    return True
                i += 2 if b == 12 else 1
            elif b == 28: i += 3
            elif b == 29: i += 5
            elif b == 30:
                i += 1
                while i < len(top) and (top[i] & 0x0F) != 0x0F and (top[i] >> 4) != 0x0F:
                    i += 1
                i += 1
            elif b >= 247: i += 2
            else: i += 1
        return False

    # -- building a standalone sfnt
    def standalone(self, keep_tags=None, replace=None):
        tags = sorted(t for t in self.tables if keep_tags is None or t in keep_tags)
        blobs = {t: self.bytes_of(t) for t in tags}
        if replace:
            blobs.update(replace)
            tags = sorted(blobs)
        return _build_sfnt(self.version, blobs)

    def subset_glyf(self, used):
        """Gid-preserving TrueType subset: unused outlines become empty, unused tables are dropped."""
        d = self.data
        loca_off, glyf_off, glyf_len = self.table("loca"), self.table("glyf"), self.length("glyf")
        count = self.num_glyphs + 1
        if self.loca_format == 0:
            if self.length("loca") < 2 * count: raise Refused("The font file is damaged.")
            offsets = [2 * _u16(d, loca_off + 2 * i) for i in range(count)]
        else:
            if self.length("loca") < 4 * count: raise Refused("The font file is damaged.")
            offsets = [_u32(d, loca_off + 4 * i) for i in range(count)]
        if offsets[-1] > glyf_len or any(offsets[i] > offsets[i + 1] for i in range(count - 1)):
            raise Refused("The font file is damaged.")
        keep, pending = set(), [0] + sorted(used)
        while pending:
            gid = pending.pop()
            if gid in keep or gid >= self.num_glyphs:
                continue
            keep.add(gid)
            start, end = glyf_off + offsets[gid], glyf_off + offsets[gid + 1]
            if end - start >= 10 and _i16(d, start) < 0:
                p = start + 10
                while p + 4 <= end:
                    flags, component = struct.unpack_from(">HH", d, p)
                    pending.append(component)
                    p += 4 + (4 if flags & 1 else 2) + (2 if flags & 8 else 4 if flags & 0x40 else 8 if flags & 0x80 else 0)
                    if not flags & 0x20:
                        break
        out, new_offsets = bytearray(), []
        for gid in range(self.num_glyphs):
            new_offsets.append(len(out))
            if gid in keep:
                out += d[glyf_off + offsets[gid]: glyf_off + offsets[gid + 1]]
                out += b"\0" * (-len(out) % 4)
        new_offsets.append(len(out))
        head = bytearray(self.bytes_of("head"))
        struct.pack_into(">h", head, 50, 1)
        replace = {"glyf": bytes(out), "loca": b"".join(struct.pack(">I", o) for o in new_offsets), "head": bytes(head)}
        keep_tags = {"head", "hhea", "maxp", "hmtx", "loca", "glyf", "cvt ", "fpgm", "prep"}
        blobs = {t: self.bytes_of(t) for t in self.tables if t in keep_tags}
        blobs.update(replace)
        return _build_sfnt(self.version, blobs)


def _checksum(data):
    data += b"\0" * (-len(data) % 4)
    return sum(struct.unpack(">%dI" % (len(data) // 4), data)) & 0xFFFFFFFF


def _build_sfnt(version, blobs):
    tags = sorted(blobs)
    if "head" in blobs:
        head = bytearray(blobs["head"])
        struct.pack_into(">I", head, 8, 0)
        blobs = dict(blobs, head=bytes(head))
    count = len(tags)
    entry_selector = max(0, count.bit_length() - 1)
    search_range = (1 << entry_selector) * 16
    header = version + struct.pack(">HHHH", count, search_range, entry_selector, count * 16 - search_range)
    offset, directory, body = 12 + 16 * count, b"", b""
    for tag in tags:
        blob = blobs[tag]
        directory += struct.pack(">4sIII", tag.encode("latin-1"), _checksum(blob), offset + len(body), len(blob))
        body += blob + b"\0" * (-len(blob) % 4)
    font = bytearray(header + directory + body)
    if "head" in blobs:
        position = 12 + 16 * tags.index("head")
        head_offset = struct.unpack_from(">I", font, position + 8)[0]
        struct.pack_into(">I", font, head_offset + 8, (0xB1B0AFBA - _checksum(bytes(font))) & 0xFFFFFFFF)
    return bytes(font)


def _allowed_font_roots():
    roots = []
    windir = os.environ.get("SystemRoot") or os.environ.get("windir") or r"C:\Windows"
    roots.append(os.path.join(windir, "Fonts"))
    local = os.environ.get("LOCALAPPDATA")
    if local:
        roots.append(os.path.join(local, "Microsoft", "Windows", "Fonts"))
    return [os.path.normcase(os.path.realpath(root)) for root in roots]


def _validated_font_path(value):
    if not isinstance(value, str) or not value or len(value) > 520 or "\0" in value:
        raise Refused("The font file is not an installed Windows font.")
    if value.startswith("\\\\?\\"):
        value = value[4:]
    if not os.path.isabs(value) or value.startswith("\\\\"):
        raise Refused("The font file is not an installed Windows font.")
    if os.path.splitext(value)[1].lower() not in (".ttf", ".otf", ".ttc"):
        raise Refused("Only TrueType and OpenType font files can be embedded.")
    real = os.path.normcase(os.path.realpath(value))
    for root in _allowed_font_roots():
        try:
            if os.path.commonpath([root, real]) == root and real != root and os.path.isfile(real):
                return real
        except ValueError:
            continue
    raise Refused("The font file is not an installed Windows font.")


def _tounicode(pairs):
    lines = ["/CIDInit /ProcSet findresource begin 12 dict begin begincmap", "/CIDSystemInfo<</Registry(Adobe)/Ordering(UCS)/Supplement 0>> def",
             "/CMapName/Adobe-Identity-UCS def /CMapType 2 def", "1 begincodespacerange <0000> <FFFF> endcodespacerange"]
    items = sorted(pairs.items())
    for start in range(0, len(items), 100):
        chunk = items[start:start + 100]
        lines.append(f"{len(chunk)} beginbfchar")
        lines += [f"<{gid:04X}> <{chr(code).encode('utf-16-be').hex().upper()}>" for gid, code in chunk]
        lines.append("endbfchar")
    lines.append("endcmap CMapName currentdict /CMap defineresource pop end end")
    return "\n".join(lines).encode("ascii")


def _add_page_resource(doc, page, category, name, ref):
    holder, kind, value = _inherited(doc, page.xref, "Resources")
    if kind == "null":
        doc.xref_set_key(page.xref, "Resources", f"<</{category}<</{name} {ref} 0 R>>>>")
        return
    if holder != page.xref:
        doc.xref_set_key(page.xref, "Resources", value)
    _set_nested(doc, page.xref, ["Resources", category, name], f"{ref} 0 R")


def _set_nested(doc, xref, keys, value):
    prefix = []
    for i, key in enumerate(keys[:-1]):
        kind, current = doc.xref_get_key(xref, "/".join(prefix + [key]))
        if kind == "xref":
            xref, prefix = int(current.split()[0]), []
        elif kind == "dict":
            prefix.append(key)
        elif kind == "null":
            nested = value
            for inner in reversed(keys[i + 1:]):
                nested = f"<</{inner} {nested}>>"
            doc.xref_set_key(xref, "/".join(prefix + [key]), nested)
            return
        else:
            raise Refused("The page resources are malformed.")
    doc.xref_set_key(xref, "/".join(prefix + [keys[-1]]), value)


def _append_content(doc, page, stream):
    page.wrap_contents()
    new = doc.get_new_xref()
    doc.update_object(new, "<<>>")
    doc.update_stream(new, stream)
    existing = page.get_contents()
    doc.xref_set_key(page.xref, "Contents", "[" + " ".join(f"{x} 0 R" for x in existing + [new]) + "]")


def add_unicode_text(request):
    doc = _open(request)
    try:
        index = _page_index(doc, request.get("Page"))
        page = doc[index]
        geometry = Geometry(doc, page.xref)
        x = _num(request.get("X"), "X", -1e7, 1e7)
        y = _num(request.get("Y"), "Y", -1e7, 1e7)
        size = _num(request.get("FontSize"), "FontSize", 4, 200)
        color = _color(request, "", (0, 0, 0))
        face_index = _num(request.get("FaceIndex", 0), "FaceIndex", 0, 4096, True)
        text = request.get("Text")
        if not isinstance(text, str):
            raise Refused("The text must be a string.")
        text = _normalise_newlines(text)
        if not (1 <= len(text) <= 2000) or not text.strip():
            raise Refused("The text must be between 1 and 2000 characters.")
        if any(ord(ch) < 32 and ch != "\n" or ord(ch) in range(0x7F, 0xA0) or 0xD800 <= ord(ch) <= 0xDFFF for ch in text):
            raise Refused("The text contains a control character that cannot be placed.")
        path = _validated_font_path(request.get("FontFile"))
        length = os.path.getsize(path)
        if length > MAX_FONT_BYTES:
            raise Refused(f"The font file is {length // (1024 * 1024)} MB, which exceeds the 25 MB embedding limit.")
        with open(path, "rb") as handle:
            data = handle.read()
        face = Face(data, face_index)
        if face.fs_type & 0x0200:
            raise Refused("This font only permits bitmap embedding.")
        if face.fs_type & 0x0002 and not face.fs_type & 0x000C:
            raise Refused("This font's license does not permit embedding.")
        if face.is_cff and face.cff_is_cid_keyed():
            raise Refused("CID-keyed OpenType fonts are not supported.")
        lines = text.split("\n")
        gids, used, missing = {}, {}, 0
        for ch in text:
            if ch == "\n":
                continue
            code = ord(ch)
            if code not in gids:
                gids[code] = face.glyph_for(code)
            if gids[code] == 0:
                missing += 1
            else:
                used.setdefault(gids[code], code)
        if missing:
            raise Refused(f"The selected font has no glyph for {missing} character{'s' if missing != 1 else ''} in the text.")
        scale = size / face.upem
        widest = max(sum(face.advance(gids[ord(ch)]) for ch in line) * scale for line in lines)
        dx, dy = geometry.to_display(x, y)
        bottom = dy + (len(lines) - 1) * size * LEAD + 0.25 * size
        if dx < -TOL or dy - BASELINE * size < -TOL or dx + widest > geometry.disp_w + TOL or bottom > geometry.disp_h + TOL:
            raise Refused("The text would extend beyond the page. Move it or use a smaller size.")
        subset = face.has_glyf and not face.fs_type & 0x0100
        if face.has_glyf:
            font_bytes = face.subset_glyf(set(used)) if subset else face.standalone()
        else:
            font_bytes = face.standalone() if (data[:4] == b"ttcf") else data
        tag = "".join(chr(65 + b % 26) for b in hashlib.sha1(font_bytes).digest()[:6]) if subset else ""
        base = re.sub(r"[^A-Za-z0-9\-]", "", face.names().replace(" ", "-")) or "EmbeddedFont"
        base_font = (tag + "+" if tag else "") + base[:60]
        bbox, ascent, descent, cap, italic = face.metrics()
        file_ref = doc.get_new_xref()
        if face.has_glyf:
            doc.update_object(file_ref, f"<</Length1 {len(font_bytes)}>>")
        else:
            doc.update_object(file_ref, "<</Subtype/OpenType>>")
        doc.update_stream(file_ref, font_bytes)
        tounicode = doc.get_new_xref(); doc.update_object(tounicode, "<<>>"); doc.update_stream(tounicode, _tounicode({g: c for g, c in used.items()}))
        descriptor = doc.get_new_xref()
        doc.update_object(descriptor, f"<</Type/FontDescriptor/FontName/{base_font}/Flags 4/FontBBox[{bbox[0]} {bbox[1]} {bbox[2]} {bbox[3]}]"
                                      f"/ItalicAngle {italic:.2f}/Ascent {ascent}/Descent {descent}/CapHeight {cap}/StemV 80"
                                      f"/{'FontFile2' if face.has_glyf else 'FontFile3'} {file_ref} 0 R>>")
        widths = " ".join(f"{g}[{round(face.advance(g) * 1000 / face.upem)}]" for g in sorted(used))
        cid = doc.get_new_xref()
        doc.update_object(cid, f"<</Type/Font/Subtype/{'CIDFontType2' if face.has_glyf else 'CIDFontType0'}/BaseFont/{base_font}"
                               f"/CIDSystemInfo<</Registry(Adobe)/Ordering(Identity)/Supplement 0>>/FontDescriptor {descriptor} 0 R"
                               f"/DW 1000/W[{widths}]" + ("/CIDToGIDMap/Identity" if face.has_glyf else "") + ">>")
        type0 = doc.get_new_xref()
        doc.update_object(type0, f"<</Type/Font/Subtype/Type0/BaseFont/{base_font}/Encoding/Identity-H/DescendantFonts[{cid} 0 R]/ToUnicode {tounicode} 0 R>>")
        name = "HpdfU" + uuid.uuid4().hex[:8]
        a, b, c, d = geometry.matrix
        ops = ["q", f"{_color_ops(color)}"]
        for number, line in enumerate(lines):
            if not line:
                continue
            ex, ey = geometry.to_user(dx, dy + number * size * LEAD)
            codes = "".join(f"{gids[ord(ch)]:04X}" for ch in line)
            ops.append(f"BT /{name} {size:g} Tf {a} {b} {c} {d} {ex:.4f} {ey:.4f} Tm <{codes}> Tj ET")
        ops.append("Q")
        _add_page_resource(doc, page, "Font", name, type0)
        _append_content(doc, page, "\n".join(ops).encode("ascii"))
        _save(doc, request)
        return {"Applied": True, "Page": index, "Lines": len(lines), "FontBytes": len(font_bytes), "FontFileBytes": length,
                "Subset": bool(subset), "GlyphsUsed": len(used)}
    finally:
        doc.close()


HANDLERS = {"AddFreeText": add_free_text, "ListFreeText": list_free_text, "UpdateFreeText": update_free_text,
            "DeleteFreeText": delete_free_text, "AddUnicodeText": add_unicode_text}


def handle(operation, request):
    if operation not in HANDLERS:
        raise Refused("Unsupported text operation.")
    return HANDLERS[operation](request)
