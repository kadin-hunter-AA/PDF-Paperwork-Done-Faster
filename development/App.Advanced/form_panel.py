"""Broad AcroForm listing and filling for the Form fields panel (text, checkbox, radio, combo, list)."""
from __future__ import annotations

import json
import re

MAX_FIELDS = 2000
MAX_OPTIONS = 64
MAX_STRING = 1000
MAX_RESPONSE_BYTES = 60 * 1024
MAX_FILL_FIELDS = 500
MAX_FILL_VALUE = 4096
MAX_FIELD_NAME = 512

READ_ONLY = 1 << 0
REQUIRED = 1 << 1
NO_TOGGLE_TO_OFF = 1 << 14
RADIO = 1 << 15
PUSHBUTTON = 1 << 16
MULTILINE = 1 << 12
PASSWORD = 1 << 13
COMBO = 1 << 17
EDIT = 1 << 18
MULTI_SELECT = 1 << 21


class Refused(ValueError):
    pass


def _s(value) -> str:
    """Best-effort text of a PDF string, name or stream value."""
    if value is None:
        return ""
    if hasattr(value, "get_data"):
        try:
            data = value.get_data()
            if data[:2] in (b"\xfe\xff", b"\xff\xfe"):
                return data.decode("utf-16", "replace")
            return data.decode("utf-8", "replace") if data[:3] == b"\xef\xbb\xbf" else data.decode("latin-1")
        except Exception:
            return ""
    text = str(value)
    return text[1:] if text.startswith("/") and type(value).__name__ == "NameObject" else text


def _name(value) -> str | None:
    text = str(value) if value is not None else None
    return text[1:] if text and text.startswith("/") else text


def _resolve(obj):
    try:
        return obj.get_object() if obj is not None else None
    except Exception:
        return None


def _open_reader(path: str, password: str | None):
    from pypdf import PdfReader
    try:
        reader = PdfReader(path)
        if reader.is_encrypted and not reader.decrypt(password or ""):
            raise Refused("The PDF is password-protected and could not be opened.")
        return reader
    except Refused:
        raise
    except Exception:
        raise Refused("The PDF could not be read to list its form fields.")


def _chain(field):
    node, seen = field, 0
    while node is not None and seen < 32:
        yield node
        node = _resolve(node.get("/Parent"))
        seen += 1


def _get(node, key):
    value = node.get(key)
    return None if type(value).__name__ == "NullObject" else value


def _inherit(field, key):
    for node in _chain(field):
        value = _get(node, key)
        if value is not None:
            return value
    return None


def _on_states(widget) -> list[str]:
    ap = _resolve(widget.get("/AP"))
    normal = _resolve(ap.get("/N")) if ap is not None else None
    if normal is None or not hasattr(normal, "keys"):
        return []
    return [str(k)[1:] for k in normal.keys() if str(k) != "/Off"]


def _text_limited(text: str, limit: int = MAX_STRING) -> tuple[str, bool]:
    return (text[:limit], True) if len(text) > limit else (text, False)


def _has_script(node) -> bool:
    action = _resolve(node.get("/A"))
    if action is not None and str(action.get("/S")) == "/JavaScript":
        return True
    aa = _resolve(node.get("/AA"))
    if aa is not None and hasattr(aa, "values"):
        for entry in aa.values():
            entry = _resolve(entry)
            if entry is not None and hasattr(entry, "get") and str(entry.get("/S")) == "/JavaScript":
                return True
    return False


def scan(path: str, password: str | None = None) -> dict:
    """Read every terminal field. Returns document flags plus internal field records (not size-bounded)."""
    reader = _open_reader(path, password)
    root = _resolve(reader.trailer["/Root"])
    acro = _resolve(root.get("/AcroForm")) if root is not None else None
    has_xfa = bool(acro is not None and acro.get("/XFA") is not None)
    need_appearances = bool(acro is not None and _get(acro, "/NeedAppearances") is not None and bool(acro["/NeedAppearances"]))
    calc_order = set()
    if acro is not None and isinstance(acro.get("/CO"), list):
        for ref in acro["/CO"]:
            ident = getattr(ref, "idnum", None)
            if ident is not None:
                calc_order.add(ident)
    fields: dict = {}
    order: list = []
    for page_index, page in enumerate(reader.pages):
        annots = _resolve(page.get("/Annots"))
        if annots is None:
            continue
        for ref in annots:
            widget = _resolve(ref)
            if widget is None or str(widget.get("/Subtype")) != "/Widget":
                continue
            terminal = None
            for node in _chain(widget):
                if _get(node, "/T") is not None:
                    terminal = node
                    break
            if terminal is None:
                continue
            ident = getattr(terminal, "indirect_reference", None)
            key = ident.idnum if ident is not None else ("direct", page_index, len(order))
            record = fields.get(key)
            if record is None:
                parts = [_s(n["/T"]) for n in _chain(terminal) if _get(n, "/T") is not None]
                record = fields[key] = {"key": key, "name": ".".join(reversed(parts)), "terminal": terminal, "widgets": []}
                order.append(key)
            rect = [round(float(v), 3) for v in widget.get("/Rect", [0, 0, 0, 0])]
            rect = [min(rect[0], rect[2]), min(rect[1], rect[3]), max(rect[0], rect[2]), max(rect[1], rect[3])]
            wident = getattr(widget, "indirect_reference", None)
            record["widgets"].append({"Page": page_index, "Left": rect[0], "Bottom": rect[1], "Right": rect[2], "Top": rect[3],
                                      "xref": wident.idnum if wident is not None else None, "obj": widget})
    name_counts: dict = {}
    for key in order:
        name_counts[fields[key]["name"]] = name_counts.get(fields[key]["name"], 0) + 1
    result = []
    for key in order:
        record = fields[key]
        terminal = record["terminal"]
        ft = _name(_inherit(terminal, "/FT"))
        ff = int(_inherit(terminal, "/Ff") or 0)
        first = record["widgets"][0]["obj"]
        info = {"FullName": record["name"], "Multiline": False, "Password": False, "ReadOnly": bool(ff & READ_ONLY),
                "Required": bool(ff & REQUIRED), "MaxLength": 0, "Editable": False, "MultiSelect": False,
                "Options": [], "OptionsTruncated": False, "Ambiguous": name_counts[record["name"]] > 1,
                "Selected": [], "ValueTruncated": False}
        raw = _inherit(terminal, "/V")
        if ft == "Tx":
            info["Type"] = "Text"
            info["Multiline"] = bool(ff & MULTILINE)
            info["Password"] = bool(ff & PASSWORD)
            info["MaxLength"] = int(_inherit(terminal, "/MaxLen") or 0)
            value = "" if info["Password"] else _s(raw)
            info["Value"], info["ValueTruncated"] = _text_limited(value)
        elif ft == "Btn":
            if ff & PUSHBUTTON:
                info["Type"], info["Value"] = "Button", ""
            elif ff & RADIO:
                info["Type"] = "Radio"
                exports: list[str] = []
                for w in record["widgets"]:
                    for state in _on_states(w["obj"]):
                        if state not in exports:
                            exports.append(state)
                info["Options"] = [{"Export": e, "Display": e} for e in exports[:MAX_OPTIONS]]
                info["OptionsTruncated"] = len(exports) > MAX_OPTIONS
                value = _name(raw) or ""
                if not value:
                    for w in record["widgets"]:
                        state = _name(w["obj"].get("/AS"))
                        if state and state != "Off":
                            value = state
                            break
                info["Value"] = "" if value == "Off" else value
                info["Selected"] = [info["Value"]] if info["Value"] else []
                info["NoToggleToOff"] = bool(ff & NO_TOGGLE_TO_OFF)
                info["HasOpt"] = _inherit(terminal, "/Opt") is not None
            else:
                info["Type"] = "Checkbox"
                states = _on_states(first)
                on = states[0] if states else "Yes"
                info["Options"] = [{"Export": on, "Display": on}]
                value = _name(raw)
                if not value:
                    value = _name(first.get("/AS")) or "Off"
                info["Value"] = "false" if value in ("Off", "") else "true"
        elif ft == "Ch":
            combo = bool(ff & COMBO)
            info["Type"] = "Combo" if combo else "List"
            info["Editable"] = combo and bool(ff & EDIT)
            info["MultiSelect"] = (not combo) and bool(ff & MULTI_SELECT)
            options = []
            opt = _resolve(_inherit(terminal, "/Opt"))
            for item in (opt or []):
                item = _resolve(item)
                if isinstance(item, list) and len(item) >= 2:
                    options.append({"Export": _s(_resolve(item[0])), "Display": _s(_resolve(item[1]))})
                elif item is not None:
                    options.append({"Export": _s(item), "Display": _s(item)})
            info["OptionsTruncated"] = len(options) > MAX_OPTIONS
            info["Options"] = [{"Export": _text_limited(o["Export"])[0], "Display": _text_limited(o["Display"])[0]}
                               for o in options[:MAX_OPTIONS]]
            info["_allOptions"] = options
            raw = _resolve(raw) if isinstance(raw, list) or hasattr(raw, "get_object") else raw
            selected = [_s(_resolve(v)) for v in raw] if isinstance(raw, list) else ([_s(raw)] if raw is not None and _s(raw) != "" else [])
            info["Selected"] = [_text_limited(v)[0] for v in selected[:MAX_OPTIONS]]
            info["Value"] = _text_limited(", ".join(selected))[0]
        elif ft == "Sig":
            info["Type"], info["Value"] = "Signature", ""
        else:
            continue
        calculated = record["key"] in calc_order
        script = False
        for node in _chain(terminal):
            aa = _resolve(node.get("/AA"))
            if aa is not None and _get(aa, "/C") is not None:
                calculated = True
            script = script or _has_script(node)
        for w in record["widgets"]:
            script = script or _has_script(w["obj"])
        info["Calculated"], info["HasJavaScript"] = calculated, script
        info["Widgets"] = [{k: w[k] for k in ("Page", "Left", "Bottom", "Right", "Top")} for w in record["widgets"]]
        info["_xref"] = record["key"] if isinstance(record["key"], int) else None
        info["_widgetXrefs"] = [w["xref"] for w in record["widgets"]]
        info["_widgetOn"] = {w["xref"]: _on_states(w["obj"]) for w in record["widgets"]}
        result.append(info)
    return {"HasXfa": has_xfa, "NeedAppearances": need_appearances, "Fields": result}


def _public(info: dict) -> dict:
    return {k: v for k, v in info.items() if not k.startswith("_")}


def _list(request: dict) -> dict:
    scanned = scan(request["Input"], request.get("Password"))
    fields = scanned["Fields"]
    count = len(fields)
    truncated = count > MAX_FIELDS
    selected, size = [], 600
    for info in fields[:MAX_FIELDS]:
        public = _public(info)
        piece = len(json.dumps(public, ensure_ascii=True)) + 2
        if size + piece > MAX_RESPONSE_BYTES:
            truncated = True
            break
        selected.append(public)
        size += piece
    return {"Fields": selected, "FieldCount": count, "Truncated": truncated, "HasXfa": scanned["HasXfa"],
            "XfaOnly": scanned["HasXfa"] and count == 0, "NeedAppearances": scanned["NeedAppearances"]}


def _pdf_name(text: str) -> str:
    return "/" + re.sub(r"[^A-Za-z0-9._+\-]", lambda m: "".join("#%02X" % b for b in m.group(0).encode("utf-8")), text)


def _validate(info: dict, value) -> object:
    """Return the normalized value to write, or raise Refused."""
    kind = info["Type"]
    if info["ReadOnly"]:
        raise Refused("A read-only form field cannot be changed.")
    if info["Ambiguous"]:
        raise Refused("A form field shares its name with another field and cannot be filled safely.")
    if kind in ("Signature", "Button"):
        raise Refused("Signature and button fields cannot be filled here.")
    values = value if isinstance(value, list) else [value]
    if not all(isinstance(v, str) for v in values):
        raise Refused("Form field values must be text.")
    if any(len(v) > MAX_FILL_VALUE for v in values):
        raise Refused("A form field value is too long.")
    if kind != "List" and len(values) != 1:
        raise Refused("Only list boxes accept more than one value.")
    v = values[0] if values else ""
    if kind == "Text":
        if info["Password"]:
            raise Refused("Password fields cannot be filled here.")
        if "\x00" in v:
            raise Refused("A form field value contains an unsupported character.")
        if not info["Multiline"] and ("\n" in v or "\r" in v):
            raise Refused("A single-line text field cannot contain line breaks.")
        if info["MaxLength"] and len(v) > info["MaxLength"]:
            raise Refused("A value is longer than its field allows.")
        return v
    if kind == "Checkbox":
        if v not in ("true", "false"):
            raise Refused("Checkbox values must be true or false.")
        return v
    if kind == "Radio":
        if info.get("HasOpt"):
            raise Refused("This radio group uses an unsupported option mapping.")
        exports = [o["Export"] for o in info["Options"]]
        if v == "":
            if info.get("NoToggleToOff"):
                raise Refused("This radio group cannot be cleared.")
            return ""
        if v not in exports:
            raise Refused("A radio value is not one of the group's choices.")
        return v
    if kind in ("Combo", "List"):
        options = info["_allOptions"]
        by_export = {o["Export"]: o for o in options}
        by_display = {o["Display"]: o for o in options}
        out = []
        for item in values:
            if item in by_export:
                out.append(item)
            elif item in by_display:
                out.append(by_display[item]["Export"])
            elif kind == "Combo" and info["Editable"]:
                if "\n" in item or "\r" in item or "\x00" in item:
                    raise Refused("A form field value contains an unsupported character.")
                out.append(item)
            else:
                raise Refused("A value is not one of the field's choices.")
        if kind == "List":
            if len(out) > 1 and not info["MultiSelect"]:
                raise Refused("Only multi-select list boxes accept more than one value.")
            return out
        return out[0]
    raise Refused("This field type cannot be filled.")


def _latin(text: str) -> bytes:
    return text.encode("cp1252", "replace")


def _pdf_literal(data: bytes) -> bytes:
    return b"(" + data.replace(b"\\", b"\\\\").replace(b"(", b"\\(").replace(b")", b"\\)") + b")"


def _choice_appearance(doc, widget_xref: int, width: float, height: float, lines: list[tuple[str, bool]], font_size: float) -> None:
    """Replace a choice widget's appearance with listed lines; selected lines are highlighted."""
    leading = font_size * 1.15
    body = [b"/Tx BMC", b"q", b"1 1 %.3f %.3f re W n" % (max(width - 2, 1), max(height - 2, 1))]
    for i, (_, picked) in enumerate(lines):
        top = height - 2 - i * leading
        if top < 0:
            break
        if picked:
            body.append(b"0.6 0.75 0.9 rg 1 %.3f %.3f %.3f re f" % (top - leading, width - 2, leading))
    body.append(b"BT 0 g /Helv %.3f Tf" % font_size)
    for i, (text, _) in enumerate(lines):
        top = height - 2 - i * leading
        if top < 0:
            break
        body.append(b"1 0 0 1 3 %.3f Tm %s Tj" % (top - font_size * 0.95, _pdf_literal(_latin(text))))
    body += [b"ET", b"Q", b"EMC"]
    font = doc.get_new_xref()
    doc.update_object(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    form = doc.get_new_xref()
    doc.update_object(form, "<< /Type /XObject /Subtype /Form /BBox [0 0 %.3f %.3f] /Resources << /Font << /Helv %d 0 R >> >> >>"
                      % (width, height, font))
    doc.update_stream(form, b"\n".join(body))
    doc.xref_set_key(widget_xref, "AP", "<< /N %d 0 R >>" % form)


def _del_key(doc, xref: int, key: str) -> None:
    """Remove a dictionary key outright (xref_set_key would store an explicit null instead)."""
    import pymupdf
    if doc.xref_get_key(xref, key)[0] == "null":
        return
    pdf = pymupdf._as_pdf_document(doc)
    obj = pymupdf.mupdf.pdf_load_object(pdf, xref)
    pymupdf.mupdf.pdf_dict_dels(obj, key)
    pymupdf.mupdf.pdf_update_object(pdf, xref, obj)


def _write(request: dict, scanned: dict, targets: dict) -> list[str]:
    import pymupdf
    warnings: list[str] = []
    try:
        doc = pymupdf.open(request["Input"])
        if doc.needs_pass and not doc.authenticate(request.get("Password") or ""):
            raise Refused("The PDF is password-protected and could not be opened.")
    except Refused:
        raise
    except Exception:
        raise Refused("The PDF could not be opened to fill its form fields.")
    try:
        by_name = {f["FullName"]: f for f in scanned["Fields"]}
        pages = [doc[i] for i in range(len(doc))]  # keep pages alive: widgets are bound to them
        for name, value in targets.items():
            info = by_name[name]
            kind = info["Type"]
            xrefs = [x for x in info["_widgetXrefs"] if x is not None]
            if info["_xref"] is None or len(xrefs) != len(info["_widgetXrefs"]):
                raise Refused("A form field uses an unsupported structure and cannot be filled safely.")
            field_xref = info["_xref"]
            if kind in ("Checkbox", "Radio"):
                if kind == "Checkbox":
                    on = info["Options"][0]["Export"]
                    selected = on if value == "true" else ""
                else:
                    selected = value
                if selected and not any(selected in info["_widgetOn"][x] for x in xrefs):
                    raise Refused("A checkbox or radio choice has no appearance state to select.")
                doc.xref_set_key(field_xref, "V", _pdf_name(selected) if selected else "/Off")
                for x in xrefs:
                    doc.xref_set_key(x, "AS", _pdf_name(selected) if selected and selected in info["_widgetOn"][x] else "/Off")
                    if x != field_xref:
                        _del_key(doc, x, "V")
                continue
            if kind == "Text":
                _del_key(doc, field_xref, "RV")
                if any(not _cp1252_ok(c) for c in value):
                    warnings.append("Some characters may not display in a field's font in other PDF apps.")
            page_widgets = {}
            for page in pages:
                for w in page.widgets() or []:
                    if w.xref in xrefs:
                        page_widgets[w.xref] = w
            if len(page_widgets) != len(xrefs):
                raise Refused("A form field's widget could not be located.")
            for x in xrefs:
                w = page_widgets[x]
                if kind == "Text":
                    if value == "":
                        doc.xref_set_key(field_xref, "V", "()")
                        w.field_value = ""
                    else:
                        w.field_value = value
                    w.update()
                    continue
                options = info["_allOptions"]
                display_of = {o["Export"]: o["Display"] for o in options}
                values = value if isinstance(value, list) else [value]
                if kind == "List":
                    literal = [pymupdf.get_pdf_str(v) for v in values]
                    doc.xref_set_key(field_xref, "V", "[" + "".join(literal) + "]" if len(values) != 1 else literal[0])
                    exports = [o["Export"] for o in options]
                    indices = sorted(exports.index(v) for v in values if v in exports)
                    if indices:
                        doc.xref_set_key(field_xref, "I", "[" + " ".join(map(str, indices)) + "]")
                    else:
                        _del_key(doc, field_xref, "I")
                    rect = w.rect
                    lines = [(o["Display"], o["Export"] in values) for o in options]
                    _choice_appearance(doc, x, rect.width, rect.height, lines, 11.0)
                else:  # Combo
                    shown = display_of.get(value, value)
                    if value == "":
                        doc.xref_set_key(field_xref, "V", "()")
                    else:
                        doc.xref_set_key(field_xref, "V", pymupdf.get_pdf_str(value))
                    _del_key(doc, field_xref, "I")
                    rect = w.rect
                    size = max(min(rect.height - 4, 12.0), 6.0)
                    _choice_appearance(doc, x, rect.width, rect.height, [(shown, False)], size)
        doc.need_appearances(True)
        doc.save(request["Output"], garbage=0, deflate=True)
    finally:
        doc.close()
    return warnings


def _cp1252_ok(ch: str) -> bool:
    try:
        ch.encode("cp1252")
        return True
    except UnicodeEncodeError:
        return False


def _fill(request: dict) -> dict:
    output = request.get("Output")
    if not isinstance(output, str):
        raise Refused("The form fill request is malformed.")
    values = request.get("Values")
    if not isinstance(values, dict) or not values:
        raise Refused("There are no form values to apply.")
    if len(values) > MAX_FILL_FIELDS:
        raise Refused("Too many form fields were supplied at once.")
    for name, value in values.items():
        if not isinstance(name, str) or not name or len(name) > MAX_FIELD_NAME:
            raise Refused("A form field name is invalid.")
        if not (isinstance(value, str) or (isinstance(value, list) and len(value) <= MAX_OPTIONS)):
            raise Refused("Form field values must be text.")
    scanned = scan(request["Input"], request.get("Password"))
    if scanned["HasXfa"]:
        raise Refused("This form uses XFA, which this app cannot fill without breaking it. No changes were made.")
    if not scanned["Fields"]:
        raise Refused("This document has no fillable form fields.")
    by_name = {f["FullName"]: f for f in scanned["Fields"]}
    targets, warnings = {}, []
    for name, value in values.items():
        info = by_name.get(name)
        if info is None:
            raise Refused("A form field in the request does not exist in this document.")
        targets[name] = _validate(info, value)
    touched = [by_name[n] for n in targets]
    if any(f["Calculated"] for f in scanned["Fields"]):
        warnings.append("This form has calculations that run in other PDF apps; they were not run here.")
    if any(f["HasJavaScript"] for f in touched):
        warnings.append("Some filled fields have scripts (formatting or validation) that were not run here.")
    written = _write(request, scanned, targets)
    for message in written:
        if message not in warnings:
            warnings.append(message)
    # Independent read-back of the written file.
    after = {f["FullName"]: f for f in scan(output, request.get("Password"))["Fields"]}
    readback, changed = {}, 0
    for name, wanted in targets.items():
        now = after.get(name)
        if now is None:
            raise Refused("The filled form could not be verified.")
        kind = now["Type"]
        if kind == "Checkbox":
            got = [now["Value"]]
            expect = [wanted]
        elif kind == "List":
            got, expect = sorted(now["Selected"]), sorted(wanted)
        elif kind == "Radio":
            got, expect = [now["Value"]], [wanted]
        elif kind == "Combo":
            got, expect = [now["Value"]], [wanted]
        else:
            got, expect = [now["Value"]], [wanted]
        if got != expect:
            raise Refused("The filled form could not be verified.")
        before = by_name[name]
        if (before["Value"], before["Selected"]) != (now["Value"], now["Selected"]):
            changed += 1
        readback[name] = now["Selected"] if kind == "List" else now["Value"]
    return {"Applied": True, "Count": len(targets), "Changed": changed, "Warnings": warnings, "Values": readback}


def handle(operation: str, request: dict) -> dict:
    if operation == "ListFields":
        return _list(request)
    if operation == "FillFields":
        return _fill(request)
    raise Refused("Unsupported form panel operation.")
