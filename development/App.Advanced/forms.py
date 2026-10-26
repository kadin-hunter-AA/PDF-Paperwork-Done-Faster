"""Guarded existing AcroForm text-field candidate generation."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import math
import os
from pathlib import Path
import re
import tempfile
from typing import Callable

from pypdf import PdfReader, PdfWriter
from pypdf.generic import ContentStream, NameObject, encode_pdfdocencoding

MAX_INPUT_BYTES = 128 * 1024 * 1024
MAX_TEXT_UTF16_UNITS = 4096
READ_ONLY = 1 << 0
MULTILINE = 1 << 12
PASSWORD = 1 << 13
RICH_TEXT = 1 << 25
RADIO = 1 << 15
PUSHBUTTON = 1 << 16
ANNOTATION_READ_ONLY = 1 << 6
BASE14_FONTS = {
    "/Courier", "/Courier-Bold", "/Courier-Oblique", "/Courier-BoldOblique",
    "/Helvetica", "/Helvetica-Bold", "/Helvetica-Oblique", "/Helvetica-BoldOblique",
    "/Times-Roman", "/Times-Bold", "/Times-Italic", "/Times-BoldItalic",
}
EXTRA_GLYPH_NAMES = {"é": "/eacute", "à": "/agrave", "–": "/endash"}
PUNCTUATION_GLYPHS = {
    " ": "/space", "!": "/exclam", '"': "/quotedbl", "#": "/numbersign",
    "$": "/dollar", "%": "/percent", "&": "/ampersand", "'": "/quotesingle",
    "(": "/parenleft", ")": "/parenright", "*": "/asterisk", "+": "/plus",
    ",": "/comma", "-": "/hyphen", ".": "/period", "/": "/slash", ":": "/colon",
    ";": "/semicolon", "<": "/less", "=": "/equal", ">": "/greater",
    "?": "/question", "@": "/at", "[": "/bracketleft", "\\": "/backslash",
    "]": "/bracketright", "^": "/asciicircum", "_": "/underscore", "`": "/grave",
    "{": "/braceleft", "|": "/bar", "}": "/braceright", "~": "/asciitilde",
}
WINANSI_HIGH_GLYPHS = {
    0x80: "/Euro", 0x82: "/quotesinglbase", 0x83: "/florin", 0x84: "/quotedblbase",
    0x85: "/ellipsis", 0x86: "/dagger", 0x87: "/daggerdbl", 0x88: "/circumflex",
    0x89: "/perthousand", 0x8A: "/Scaron", 0x8B: "/guilsinglleft", 0x8C: "/OE",
    0x8E: "/Zcaron", 0x91: "/quoteleft", 0x92: "/quoteright", 0x93: "/quotedblleft",
    0x94: "/quotedblright", 0x95: "/bullet", 0x96: "/endash", 0x97: "/emdash",
    0x98: "/tilde", 0x99: "/trademark", 0x9A: "/scaron", 0x9B: "/guilsinglright",
    0x9C: "/oe", 0x9E: "/zcaron", 0x9F: "/Ydieresis",
}


class FillRefused(ValueError):
    pass


class FillCanceled(RuntimeError):
    pass


class SourceChanged(RuntimeError):
    pass


@dataclass
class FieldInfo:
    name: str
    field_type: str | None
    flags: int
    max_length: int | None
    field: object
    widgets: list[tuple[int, object]]
    ancestors: list[object]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _has_action(dictionary: object) -> bool:
    if not hasattr(dictionary, "get"):
        return False
    return any(key in dictionary and dictionary.get(key) is not None for key in ("/A", "/AA"))


def _resolve(obj: object) -> object:
    return obj.get_object() if hasattr(obj, "get_object") else obj


def _field_tree(reader: PdfReader) -> list[FieldInfo]:
    root_ref = reader.trailer["/Root"] if hasattr(reader, "trailer") else reader._root_object
    root = root_ref.get_object()
    acro_ref = root.get("/AcroForm")
    if acro_ref is None:
        raise FillRefused("document has no AcroForm")
    acro = _resolve(acro_ref)
    fields = acro.get("/Fields")
    if fields is None:
        raise FillRefused("AcroForm has no field tree")
    pages = reader.pages
    if len(pages) > 5000:
        raise FillRefused("document exceeds the page inspection limit")
    widget_locations: dict[tuple[int, int], list[tuple[int, object]]] = {}
    for page_index, page in enumerate(pages):
        annotations = page.get("/Annots", [])
        if len(annotations) > 100_000:
            raise FillRefused("page has too many annotations")
        for ref in annotations:
            annot = _resolve(ref)
            if annot.get("/Subtype") == "/Widget":
                key = (getattr(ref, "idnum", id(annot)), getattr(ref, "generation", 0))
                widget_locations.setdefault(key, []).append((page_index, annot))

    result: list[FieldInfo] = []
    visiting: set[tuple[int, int]] = set()

    def walk(ref_or_obj: object, parent_name: str, inherited_type: str | None,
             inherited_flags: int, inherited_max: int | None, ancestors: list[object]) -> None:
        if len(result) > 20_000:
            raise FillRefused("field tree exceeds the inspection limit")
        ref_key = (getattr(ref_or_obj, "idnum", id(ref_or_obj)), getattr(ref_or_obj, "generation", 0))
        if ref_key in visiting:
            raise FillRefused("field tree contains a cycle")
        visiting.add(ref_key)
        field = _resolve(ref_or_obj)
        if not hasattr(field, "get"):
            visiting.remove(ref_key)
            return
        partial = str(field.get("/T", ""))
        full_name = ".".join(part for part in (parent_name, partial) if part)
        field_type = str(field.get("/FT")) if field.get("/FT") is not None else inherited_type
        try:
            flags = int(field.get("/Ff", inherited_flags))
        except (TypeError, ValueError):
            raise FillRefused("field has malformed flags")
        max_raw = field.get("/MaxLen", inherited_max)
        try:
            max_length = int(max_raw) if max_raw is not None else None
        except (TypeError, ValueError):
            raise FillRefused("field has malformed MaxLen")
        path = ancestors + [field]
        kids = field.get("/Kids", [])
        widgets: list[tuple[int, object]] = []
        field_children = []
        if field.get("/Subtype") == "/Widget":
            widgets.extend(widget_locations.get(ref_key, []))
        else:
            for child_ref in kids:
                child = _resolve(child_ref)
                child_key = (getattr(child_ref, "idnum", id(child)), getattr(child_ref, "generation", 0))
                if child.get("/Subtype") == "/Widget":
                    widgets.extend(widget_locations.get(child_key, []))
                else:
                    field_children.append(child_ref)
        if field_children:
            for child_ref in field_children:
                walk(child_ref, full_name, field_type, flags, max_length, path)
        elif full_name and field_type is not None:
            result.append(FieldInfo(full_name, field_type, flags, max_length, field, widgets, path))
        visiting.remove(ref_key)

    for field_ref in fields:
        walk(field_ref, "", None, 0, None, [])
    return result


def _check_global_safety(reader: PdfReader, fields: list[FieldInfo]) -> None:
    if reader.is_encrypted:
        raise FillRefused("encrypted PDFs are unsupported")
    root = reader.trailer["/Root"].get_object()
    if _has_action(root) or ("/OpenAction" in root and root.get("/OpenAction") is not None):
        raise FillRefused("document actions are unsupported")
    names = _resolve(root.get("/Names", {}))
    if hasattr(names, "get") and names.get("/JavaScript") is not None:
        raise FillRefused("document JavaScript is unsupported")
    acro = _resolve(root.get("/AcroForm", {}))
    if acro.get("/XFA") is not None:
        raise FillRefused("XFA forms are unsupported")
    if _has_action(acro) or ("/CO" in acro and len(acro.get("/CO") or []) > 0):
        raise FillRefused("AcroForm actions or calculation fields are unsupported")
    for field in fields:
        if field.field_type == "/Sig" and field.field.get("/V") is not None:
            raise FillRefused("documents with populated signature fields are unsupported")
        if any(_has_action(ancestor) for ancestor in field.ancestors):
            raise FillRefused("field actions are unsupported")
    for page in reader.pages:
        if _has_action(page):
            raise FillRefused("page actions are unsupported")
        for ref in page.get("/Annots", []):
            if _has_action(_resolve(ref)):
                raise FillRefused("annotation or widget actions are unsupported")


def _appearance(reader: PdfReader, target_name: str) -> bytes:
    matches = [field for field in _field_tree(reader) if field.name == target_name]
    if len(matches) != 1 or len(matches[0].widgets) != 1:
        raise FillRefused("target field must have one normal widget appearance")
    widget = matches[0].widgets[0][1]
    ap = _resolve(widget.get("/AP", {}))
    normal = ap.get("/N") if hasattr(ap, "get") else None
    if normal is None:
        raise FillRefused("target field has no normal widget appearance")
    return _resolve(normal).get_data()


def _glyph_name(character: str) -> str | None:
    if character in EXTRA_GLYPH_NAMES:
        return EXTRA_GLYPH_NAMES[character]
    if "A" <= character <= "Z" or "a" <= character <= "z":
        return "/" + character
    if "0" <= character <= "9":
        return "/" + ("zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine")[ord(character) - 48]
    return PUNCTUATION_GLYPHS.get(character)


def _parse_default_appearance(da: object) -> tuple[str, float]:
    text = str(da)
    tokens = text.split()
    if not tokens:
        raise FillRefused("field default appearance is malformed")
    pending: list[str] = []
    font_alias: str | None = None
    font_size: float | None = None
    font_count = 0
    color_count = 0
    for token in tokens:
        if not (token.startswith("/") and re.fullmatch(r"/[A-Za-z0-9_.+-]+", token)) and \
                re.fullmatch(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", token) is None and \
                token not in ("Tf", "g", "rg", "k"):
            raise FillRefused("field default appearance contains unsupported syntax")
        if token.startswith("/") or re.fullmatch(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", token):
            pending.append(token)
            continue
        if token == "Tf":
            if len(pending) != 2 or not pending[0].startswith("/"):
                raise FillRefused("field default appearance has unsupported font operators")
            try:
                font_size = float(pending[1])
            except ValueError as exc:
                raise FillRefused("field default appearance font size is malformed") from exc
            font_alias = pending[0]
            font_count += 1
        elif token in ("g", "rg", "k"):
            count = {"g": 1, "rg": 3, "k": 4}[token]
            if len(pending) != count or any(not re.fullmatch(r"[+-]?(?:\d+(?:\.\d*)?|\.\d+)", item) for item in pending):
                raise FillRefused("field default appearance has unsupported color operators")
            color_count += 1
        else:
            raise FillRefused("field default appearance contains unsupported operators")
        pending.clear()
    if pending or font_count != 1 or color_count > 1 or font_alias is None or font_size is None:
        raise FillRefused("field default appearance is ambiguous or incomplete")
    return font_alias, font_size


def _resource_font(reader: PdfReader, target: FieldInfo, alias: str) -> object:
    acro = _resolve(reader.trailer["/Root"].get("/AcroForm", {}))
    scopes = list(reversed(target.ancestors)) + [acro]
    found: list[object] = []
    for scope in scopes:
        dr_ref = scope.get("/DR") if hasattr(scope, "get") else None
        if dr_ref is None:
            continue
        dr = _resolve(dr_ref)
        fonts = _resolve(dr.get("/Font", {})) if hasattr(dr, "get") else {}
        if hasattr(fonts, "get") and alias in fonts:
            found.append(fonts[alias])
    if not found:
        raise FillRefused("field appearance font resource cannot be resolved")
    resolved = [_resolve(font) for font in found]
    if any(font != resolved[0] for font in resolved[1:]):
        raise FillRefused("field appearance font resource is ambiguous")
    font = resolved[0]
    if not hasattr(font, "get") or font.get("/Subtype") != "/Type1" or font.get("/BaseFont") not in BASE14_FONTS:
        raise FillRefused("field appearance font is not a supported standard Type1 font")
    if any(key in font for key in ("/FontDescriptor", "/ToUnicode", "/Widths", "/FirstChar", "/LastChar", "/DescendantFonts")):
        raise FillRefused("embedded or custom appearance fonts are unsupported")
    return font


def _font_encoding_map(font: object) -> tuple[str, dict[int, str]]:
    encoding = font.get("/Encoding")
    base_encoding: str
    differences: dict[int, str] = {}
    if encoding is None:
        base_encoding = "/StandardEncoding"
    else:
        encoding = _resolve(encoding)
        if isinstance(encoding, str):
            base_encoding = str(encoding)
        elif hasattr(encoding, "get"):
            base_encoding = str(encoding.get("/BaseEncoding", "/StandardEncoding"))
            items = encoding.get("/Differences", [])
            code: int | None = None
            for item in items:
                if isinstance(item, int):
                    code = int(item)
                    if code < 0 or code > 255:
                        raise FillRefused("font Differences encoding is malformed")
                elif isinstance(item, str) and item.startswith("/") and code is not None:
                    if code in differences:
                        raise FillRefused("font Differences encoding is ambiguous")
                    differences[code] = str(item)
                    code += 1
                else:
                    raise FillRefused("font Differences encoding is malformed")
        else:
            raise FillRefused("field appearance font encoding is unsupported")
    if base_encoding not in ("/StandardEncoding", "/WinAnsiEncoding"):
        raise FillRefused("field appearance font encoding is unsupported")
    return base_encoding, differences


def _verify_font_mapping(font: object, value: str) -> bytes:
    base_encoding, differences = _font_encoding_map(font)
    encoded = encode_pdfdocencoding(value)
    if isinstance(encoded, str):
        encoded = encoded.encode("latin-1")
    for character, byte in zip(value, encoded, strict=True):
        expected = _glyph_name(character)
        if expected is None:
            raise FillRefused("field value includes a glyph without proven standard-font coverage")
        actual = differences.get(byte)
        if actual is None:
            if byte < 128:
                actual = _glyph_name(chr(byte))
            elif base_encoding == "/WinAnsiEncoding":
                actual = WINANSI_HIGH_GLYPHS.get(byte)
                if actual is None and 0xA0 <= byte <= 0xFF:
                    try:
                        actual = _glyph_name(bytes([byte]).decode("cp1252"))
                    except (UnicodeDecodeError, ValueError):
                        actual = None
        if actual != expected:
            raise FillRefused("field appearance font encoding does not map every requested glyph exactly")
    return encoded


def _verify_appearance_text(reader: PdfReader, field_name: str, value: str, expected_bytes: bytes) -> None:
    fields = [field for field in _field_tree(reader) if field.name == field_name]
    if len(fields) != 1 or len(fields[0].widgets) != 1:
        raise FillRefused("candidate text appearance is ambiguous")
    widget = fields[0].widgets[0][1]
    normal = _resolve(_resolve(widget.get("/AP", {})).get("/N"))
    stream = ContentStream(normal, reader)
    displayed: list[bytes] = []
    for operands, operator in stream.operations:
        if operator == b"Tj" and len(operands) == 1:
            item = operands[0]
            if hasattr(item, "get_original_bytes"):
                displayed.append(item.get_original_bytes())
        elif operator == b"TJ" and len(operands) == 1 and isinstance(operands[0], list):
            for item in operands[0]:
                if hasattr(item, "get_original_bytes"):
                    displayed.append(item.get_original_bytes())
    if expected_bytes not in displayed and b"".join(displayed) != expected_bytes:
        raise FillRefused("candidate appearance does not contain the exact encoded requested text")


def _safe_target(reader: PdfReader, field_name: str, value: str) -> tuple[FieldInfo, int, object, bytes, bytes]:
    if not isinstance(value, str):
        raise FillRefused("field value must be Unicode text")
    if len(value.encode("utf-16-be")) // 2 > MAX_TEXT_UTF16_UNITS:
        raise FillRefused("field value exceeds the input size limit")
    try:
        encode_pdfdocencoding(value)
    except UnicodeEncodeError as exc:
        raise FillRefused("field value contains glyphs unsupported by the proven PDFDocEncoding path") from exc
    fields = _field_tree(reader)
    _check_global_safety(reader, fields)
    matches = [field for field in fields if field.name == field_name]
    if len(matches) != 1:
        raise FillRefused("field name is missing or ambiguous; use one unique fully qualified field name")
    target = matches[0]
    if target.field_type != "/Tx":
        raise FillRefused("only plain text fields are supported")
    if target.flags & READ_ONLY:
        raise FillRefused("read-only fields are unsupported")
    if target.flags & PASSWORD:
        raise FillRefused("password fields are unsupported")
    if target.flags & RICH_TEXT or target.field.get("/RV") is not None:
        raise FillRefused("rich text fields are unsupported")
    if target.flags & MULTILINE:
        raise FillRefused("multiline text fields are unsupported")
    if target.max_length is not None and len(value) > target.max_length:
        raise FillRefused("field value exceeds the field MaxLen")
    if len(target.widgets) != 1:
        raise FillRefused("target field must have exactly one page widget")
    page_index, widget = target.widgets[0]
    page = reader.pages[page_index]
    try:
        page_rotation = int(page.get("/Rotate", 0)) % 360
    except (TypeError, ValueError):
        raise FillRefused("page rotation is malformed")
    if page_rotation != 0:
        raise FillRefused("rotated pages are unsupported")
    rect = _resolve(widget.get("/Rect"))
    if len(rect) != 4:
        raise FillRefused("widget rectangle is malformed")
    left, bottom, right, top = (float(v) for v in rect)
    width, height = right - left, top - bottom
    if not all(map(math.isfinite, (left, bottom, right, top))) or width <= 4 or height <= 4:
        raise FillRefused("widget rectangle is invalid or too small")
    mk = _resolve(widget.get("/MK", {}))
    try:
        widget_rotation = int(mk.get("/R", 0)) % 360
    except (TypeError, ValueError):
        raise FillRefused("widget rotation is malformed")
    if widget_rotation != 0:
        raise FillRefused("rotated widgets are unsupported")
    da = next((ancestor.get("/DA") for ancestor in reversed(target.ancestors)
               if ancestor.get("/DA") is not None), None)
    if da is None:
        da = _resolve(reader.trailer["/Root"].get("/AcroForm", {})).get("/DA")
    if da is None:
        raise FillRefused("field default appearance is missing")
    font_alias, font_size = _parse_default_appearance(da)
    font = _resource_font(reader, target, font_alias)
    expected_bytes = _verify_font_mapping(font, value)
    if font_size == 0:
        font_size = height - 2
    elif font_size < 0:
        raise FillRefused("field default appearance font size is invalid")
    if not math.isfinite(font_size) or font_size > height - 1:
        raise FillRefused("field default appearance font does not fit the widget")
    if len(value) * font_size > width - 4:
        raise FillRefused("field value may overflow the widget appearance")
    return target, page_index, widget, _appearance(reader, field_name), expected_bytes


def _field_values(reader: PdfReader) -> dict[str, str | None]:
    result = {}
    for name, field in (reader.get_fields() or {}).items():
        value = field.get("/V")
        result[name] = None if value is None else str(value)
    return result


def _safe_checkbox_target(reader: PdfReader, field_name: str, intent: str) -> tuple[FieldInfo, int, object, str, str]:
    if intent not in ("true", "false"):
        raise FillRefused("checkbox value must be the lowercase string true or false")
    fields = _field_tree(reader)
    _check_global_safety(reader, fields)
    matches = [field for field in fields if field.name == field_name]
    if len(matches) != 1 or matches[0].field_type != "/Btn":
        raise FillRefused("target must be one unique checkbox field")
    target = matches[0]
    if target.flags & READ_ONLY:
        raise FillRefused("read-only checkboxes are unsupported")
    if target.flags & (RADIO | PUSHBUTTON):
        raise FillRefused("radio buttons and pushbuttons are unsupported")
    if len(target.widgets) != 1:
        raise FillRefused("target checkbox must have exactly one page widget")
    page_index, widget = target.widgets[0]
    page = reader.pages[page_index]
    try:
        rotation = int(page.get("/Rotate", 0)) % 360
        widget_rotation = int(_resolve(widget.get("/MK", {})).get("/R", 0)) % 360
        rect = _resolve(widget.get("/Rect"))
        left, bottom, right, top = (float(v) for v in rect)
        widget_flags = int(widget.get("/F", 0))
    except (TypeError, ValueError, IndexError) as exc:
        raise FillRefused("checkbox widget geometry or flags are malformed") from exc
    if rotation or widget_rotation:
        raise FillRefused("rotated pages or widgets are unsupported")
    if not all(map(math.isfinite, (left, bottom, right, top))) or right - left <= 4 or top - bottom <= 4:
        raise FillRefused("checkbox widget rectangle is invalid")
    if widget_flags & ANNOTATION_READ_ONLY:
        raise FillRefused("read-only checkboxes are unsupported")

    appearance = _resolve(widget.get("/AP", {}))
    normal = _resolve(appearance.get("/N")) if hasattr(appearance, "get") else None
    if not hasattr(normal, "keys"):
        raise FillRefused("checkbox normal appearance must define explicit named states")
    states = {str(key): _resolve(normal[key]) for key in normal.keys()}
    on_states = set(states) - {"/Off"}
    if "/Off" not in states or len(on_states) != 1:
        raise FillRefused("checkbox must have exactly one explicit on state and one off state")
    if any(not hasattr(stream, "get_data") for stream in states.values()):
        raise FillRefused("checkbox appearance states must be valid appearance streams")
    for stream in states.values():
        try:
            bbox = _resolve(stream.get("/BBox"))
            x0, y0, x1, y1 = (float(v) for v in bbox)
            data = stream.get_data()
        except (TypeError, ValueError, IndexError, KeyError) as exc:
            raise FillRefused("checkbox appearance stream is malformed") from exc
        if (len(bbox) != 4 or not all(map(math.isfinite, (x0, y0, x1, y1))) or
                x1 <= x0 or y1 <= y0 or not data):
            raise FillRefused("checkbox appearance stream has invalid bounds or no drawing data")
    if "/AS" not in widget:
        raise FillRefused("checkbox widget must explicitly name its current appearance state")
    current_value = str(target.field.get("/V", "/Off"))
    current_widget_state = str(widget.get("/AS", "/Off"))
    if current_value not in states or current_widget_state not in states or current_value != current_widget_state:
        raise FillRefused("checkbox field and widget values do not match their explicit appearance states")
    desired = next(iter(on_states)) if intent == "true" else "/Off"
    return target, page_index, widget, desired, current_value


def _checkbox_state_streams(reader: PdfReader, field_name: str) -> dict[str, bytes]:
    fields = [field for field in _field_tree(reader) if field.name == field_name]
    if len(fields) != 1 or len(fields[0].widgets) != 1:
        raise FillRefused("candidate checkbox appearance is ambiguous")
    widget = fields[0].widgets[0][1]
    appearance = _resolve(widget.get("/AP", {}))
    normal = _resolve(appearance.get("/N")) if hasattr(appearance, "get") else None
    if not hasattr(normal, "keys"):
        raise FillRefused("candidate checkbox appearances are missing")
    return {str(key): _resolve(normal[key]).get_data() for key in normal.keys()}


def fill_candidate(source_path: str | os.PathLike[str], destination_path: str | os.PathLike[str],
                   field_name: str, value: str, *, is_canceled: Callable[[], bool] | None = None,
                   before_publish: Callable[[Path], None] | None = None,
                   after_destination_check: Callable[[Path], None] | None = None) -> dict[str, object]:
    """Create-only publish a verified sibling candidate; source bytes are never opened for writing."""
    source = Path(source_path).resolve(strict=True)
    destination = Path(os.path.abspath(os.fspath(destination_path)))
    resolved_destination = destination.resolve(strict=False)
    if os.path.normcase(str(source)) == os.path.normcase(str(resolved_destination)):
        raise FillRefused("destination must not replace the source PDF")
    if destination.is_symlink():
        raise FillRefused("destination must not be a symbolic link")
    if destination.exists():
        raise FillRefused("destination already exists")
    if not destination.parent.is_dir():
        raise FillRefused("destination folder must already exist")
    if destination.suffix.lower() != ".pdf":
        raise FillRefused("destination must use a .pdf extension")
    if source.stat().st_size > MAX_INPUT_BYTES:
        raise FillRefused("source PDF exceeds the proof size limit")
    initial_hash = sha256(source)

    def canceled() -> None:
        if is_canceled is not None and is_canceled():
            raise FillCanceled("fill canceled before candidate publication")

    canceled()
    reader = PdfReader(str(source))
    if reader.is_encrypted:
        raise FillRefused("encrypted PDFs are unsupported")
    fields = _field_tree(reader)
    matches = [field for field in fields if field.name == field_name]
    is_checkbox = len(matches) == 1 and matches[0].field_type == "/Btn"
    if is_checkbox:
        target, page_index, _, expected_value, old_value = _safe_checkbox_target(reader, field_name, value)
        old_checkbox_appearances = _checkbox_state_streams(reader, field_name)
        expected_bytes = b""
        old_ap = b""
    else:
        target, page_index, _, old_ap, expected_bytes = _safe_target(reader, field_name, value)
        expected_value = value
        old_value = ""
    before_fields = _field_values(reader)
    if field_name not in before_fields:
        raise FillRefused("target field is missing from the field-value index")
    acro = _resolve(reader.trailer["/Root"].get("/AcroForm", {}))
    if str(acro.get("/NeedAppearances", "False")) == "True":
        raise FillRefused("source relies on viewer-generated appearances")

    descriptor, candidate_name = tempfile.mkstemp(
        prefix=f".{destination.stem}.{os.getpid()}-", suffix=".candidate.pdf", dir=destination.parent
    )
    os.close(descriptor)
    candidate = Path(candidate_name)
    try:
        writer = PdfWriter()
        writer.clone_document_from_reader(reader)
        if is_checkbox:
            writer_fields = [field for field in _field_tree(writer) if field.name == field_name]
            if len(writer_fields) != 1 or len(writer_fields[0].widgets) != 1:
                raise FillRefused("candidate checkbox became ambiguous")
            updated_field = writer_fields[0]
            updated_field.field[NameObject("/V")] = NameObject(expected_value)
            updated_field.widgets[0][1][NameObject("/AS")] = NameObject(expected_value)
        else:
            writer.update_page_form_field_values(writer.pages[page_index], {field_name: value}, auto_regenerate=False)
        with candidate.open("wb") as stream:
            writer.write(stream)
            stream.flush()
            os.fsync(stream.fileno())
        canceled()

        reopened = PdfReader(str(candidate))
        reopened_fields = _field_values(reopened)
        if reopened_fields.get(field_name) != expected_value:
            raise FillRefused("candidate did not reopen with the requested field value")
        if set(reopened_fields) != set(before_fields):
            raise FillRefused("candidate changed the form field inventory")
        for name, prior_value in before_fields.items():
            if name != field_name and reopened_fields[name] != prior_value:
                raise FillRefused("candidate changed an unrelated field value")
        if is_checkbox:
            reopened_targets = [field for field in _field_tree(reopened) if field.name == field_name]
            if len(reopened_targets) != 1 or len(reopened_targets[0].widgets) != 1:
                raise FillRefused("candidate checkbox became ambiguous")
            reopened_widget = reopened_targets[0].widgets[0][1]
            if str(reopened_widget.get("/AS", "/Off")) != expected_value:
                raise FillRefused("candidate checkbox widget appearance state does not match its value")
            new_checkbox_appearances = _checkbox_state_streams(reopened, field_name)
            if new_checkbox_appearances != old_checkbox_appearances or expected_value not in new_checkbox_appearances:
                raise FillRefused("candidate checkbox appearances changed or omit the selected state")
        else:
            new_ap = _appearance(reopened, field_name)
            if not new_ap or new_ap == old_ap:
                raise FillRefused("candidate appearance was not regenerated")
            _verify_appearance_text(reopened, field_name, value, expected_bytes)
        after_acro = _resolve(reopened.trailer["/Root"].get("/AcroForm", {}))
        if str(after_acro.get("/NeedAppearances", "False")) == "True":
            raise FillRefused("candidate requires viewer appearance regeneration")
        if len(reopened.pages) != len(reader.pages):
            raise FillRefused("candidate changed the page count")

        canceled()
        if before_publish is not None:
            before_publish(candidate)
        if sha256(source) != initial_hash:
            raise SourceChanged("source changed while the candidate was being prepared")
        canceled()
        if destination.exists() or destination.is_symlink():
            raise FillRefused("destination appeared before publication")
        if after_destination_check is not None:
            after_destination_check(candidate)
        os.rename(candidate, destination)
        return {"Destination": str(destination), "SourceSha256": initial_hash,
                "FieldName": field_name, "FieldValue": expected_value, "Page": page_index}
    finally:
        try:
            candidate.unlink(missing_ok=True)
        except OSError:
            pass
