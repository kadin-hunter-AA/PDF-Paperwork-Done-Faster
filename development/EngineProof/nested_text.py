"""Copy-on-write Form XObject text editing for the pinned PDFium adapter.

PDFium 8086 does not serialize ordinary nested text mutations. Keep all graphics
operators and form dictionaries, and replace one text-show operation instead.
The JSON stdin protocol is private to NestedTextEditor.cs; no document text is
written to diagnostics. pypdf is pinned because its font-map helpers are private.
"""
from __future__ import annotations

import hashlib
import io
import json
import math
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "tools/python-packages"))
import pypdf
from pypdf import PdfReader, PdfWriter
from pypdf.errors import DependencyError
from pypdf._cmap import build_char_map_from_dict
from pypdf._codecs.core_fontmetrics import CORE_FONT_METRICS
from pypdf._codecs import charset_encoding
from pypdf.generic import (
    ArrayObject, ByteStringObject, ContentStream, DecodedStreamObject,
    DictionaryObject, FloatObject, NameObject, NullObject, TextStringObject,
)


class UnsupportedEdit(ValueError):
    pass


def require(ok, message):
    if not ok:
        raise UnsupportedEdit(message)


def number(value):
    value = float(value)
    require(math.isfinite(value), "Non-finite PDF number")
    return value


SHOW = {b"Tj", b"TJ", b"'", b'"'}
STRINGS = (ByteStringObject, TextStringObject)


class FontCodec:
    """Only encode glyphs with known mappings AND known horizontal metrics."""
    def __init__(self, font):
        self.font = font
        subtype = font.get("/Subtype")
        require(subtype in ("/Type1", "/TrueType", "/Type0"), "Unsupported font type")
        self.cid = subtype == "/Type0"
        if self.cid:
            require(font.get("/Encoding") == "/Identity-H" and "/ToUnicode" in font,
                    "CID edits require Identity-H and a ToUnicode map")
        _, _, encoding, mapping = build_char_map_from_dict(200, font)
        self.decode_map = {}
        if self.cid:
            # For Identity-H the CMap source is a two-byte CID, not a Unicode codepoint.
            require(mapping.get(-1) == 2, "Unsupported CID code length")
            for code, text in mapping.items():
                if isinstance(code, str) and len(code) == 1:
                    self.decode_map[ord(code)] = text
        else:
            if encoding == "charmap":
                if str(font.get("/BaseFont", ""))[1:] in CORE_FONT_METRICS and not mapping:
                    encoding = dict(enumerate(charset_encoding["/StandardEncoding"]))
                elif mapping:
                    encoding = {ord(c): c for c in mapping if isinstance(c, str) and len(c) == 1}
            require(isinstance(encoding, dict), "Unsupported simple font encoding")
            for code, char in encoding.items():
                if 0 <= code <= 255:
                    self.decode_map[code] = mapping.get(char, char)
        self.widths = {}
        self.default_width = None
        if self.cid:
            descendant = font["/DescendantFonts"][0].get_object()
            self.default_width = number(descendant.get("/DW", 1000))
            entries = descendant["/W"] if "/W" in descendant else []
            i = 0
            while i < len(entries):
                start = int(entries[i]); following = entries[i + 1].get_object()
                if isinstance(following, ArrayObject):
                    for offset, width in enumerate(following):
                        self.widths[start + offset] = number(width)
                    i += 2
                else:
                    end = int(following)
                    require(0 <= start <= end <= 65535, "Invalid CID width range")
                    for code in range(start, end + 1):
                        self.widths[code] = number(entries[i + 2])
                    i += 3
        elif "/Widths" in font:
            start = int(font["/FirstChar"])
            self.widths = {start + i: number(w) for i, w in enumerate(font["/Widths"])}
        else:
            metrics = CORE_FONT_METRICS.get(str(font.get("/BaseFont", ""))[1:])
            require(metrics is not None, "Font has no reliable width table")
            require(not mapping, "Core font with ToUnicode requires explicit widths")
            # Core metrics use Unicode keys. Differences and ToUnicode are respected.
            self.widths = {code: metrics.character_widths[text]
                           for code, text in self.decode_map.items()
                           if text in metrics.character_widths}
        self.encode_map = {}
        for code, text in self.decode_map.items():
            if text and text != "\ufffd" and (code in self.widths or self.cid):
                self.encode_map.setdefault(text, []).append(code)
        # Do not guess which glyph to use for an ambiguous Unicode mapping.
        self.encode_map = {text: codes[0] for text, codes in self.encode_map.items() if len(codes) == 1}
        self.tokens = sorted(self.encode_map, key=len, reverse=True)

    def codes(self, raw):
        if self.cid:
            require(len(raw) % 2 == 0, "Invalid CID string length")
            return [int.from_bytes(raw[i:i+2], "big") for i in range(0, len(raw), 2)]
        return list(raw)

    def decode(self, raw):
        codes = self.codes(raw)
        require(all(c in self.decode_map for c in codes), "Unmapped original glyph")
        return "".join(self.decode_map[c] for c in codes)

    def encode(self, text):
        codes = []
        offset = 0
        while offset < len(text):
            token = next((s for s in self.tokens if text.startswith(s, offset)), None)
            require(token is not None, "Replacement has absent or ambiguous glyphs")
            codes.append(self.encode_map[token]); offset += len(token)
        return b"".join(code.to_bytes(2 if self.cid else 1, "big") for code in codes)

    def advance(self, raw, state):
        codes = self.codes(raw)
        require(all(c in self.widths or self.cid for c in codes), "Unknown original glyph width")
        width = sum(self.widths.get(c, self.default_width) for c in codes)
        # PDF word spacing applies only to single-byte code 32.
        spaces = 0 if self.cid else codes.count(32)
        return width + 1000 / state["size"] * (len(codes) * state["char"] + spaces * state["word"])


def stream_for(container, pdf):
    raw = container if container.get("/Subtype") == "/Form" else container.get("/Contents")
    require(raw is not None, "Missing content stream")
    return ContentStream(raw, pdf)


def resources_for(container, inherited=None):
    result = container.get("/Resources", inherited)
    require(result is not None, "Missing resources")
    return result.get_object()


def locate(stream, resources, inherited, kind, ordinal):
    """Resolve a local operator occurrence and the effective inherited text state."""
    require(isinstance(ordinal, int) and not isinstance(ordinal, bool) and ordinal >= 0,
            "Invalid occurrence index")
    state = inherited.copy()
    stack, marks = [], []
    count = 0
    for index, (args, op) in enumerate(stream.operations):
        if op == b"q":
            stack.append(state.copy())
        elif op == b"Q":
            require(bool(stack), "Unbalanced graphics state")
            state = stack.pop()
        elif op == b"Tf":
            state["font"] = resources["/Font"][args[0]].get_object()
            state["size"] = number(args[1])
        elif op in (b"Tc", b"Tw", b"Tr"):
            state[{b"Tc": "char", b"Tw": "word", b"Tr": "render"}[op]] = number(args[0])
        elif op == b"gs":
            gs = resources["/ExtGState"][args[0]].get_object()
            if "/Font" in gs:
                state["font"] = gs["/Font"][0].get_object()
                state["size"] = number(gs["/Font"][1])
        elif op in (b"BMC", b"BDC"):
            prop = args[1] if op == b"BDC" else None
            if isinstance(prop, NameObject):
                prop = resources["/Properties"][prop].get_object()
            marks.append(isinstance(prop, DictionaryObject) and "/ActualText" in prop)
        elif op == b"EMC":
            require(bool(marks), "Unbalanced marked content")
            marks.pop()
        if op == b'"':
            state["word"], state["char"] = number(args[0]), number(args[1])
        if (kind == "form" and op == b"Do") or (kind == "text" and op in SHOW):
            if count == ordinal:
                state["actual"] = state.get("actual", False) or any(marks)
                return index, args, op, state
            count += 1
    raise UnsupportedEdit("Target occurrence not found")


def clone_resources(resources):
    result = DictionaryObject(resources)
    result[NameObject("/XObject")] = DictionaryObject(resources.get("/XObject", {}).get_object()
                                                     if "/XObject" in resources else {})
    return result


def clone_form(form):
    result = DecodedStreamObject()
    # All semantic attributes survive, including BBox, Matrix, Group, OC and metadata.
    for key, value in form.items():
        if key not in ("/Length", "/Filter", "/DecodeParms"):
            result[key] = value
    result.set_data(form.get_data())
    return result


def set_content(container, stream, writer):
    data = stream.get_data()
    if container.get("/Subtype") == "/Form":
        container.set_data(data)
    else:
        content = DecodedStreamObject(); content.set_data(data)
        container[NameObject("/Contents")] = writer._add_object(content)


def walk(pdf, page_index, path, writer=None):
    require(isinstance(page_index, int) and 0 <= page_index < len(pdf.pages), "Invalid page index")
    require(isinstance(path, list) and 1 <= len(path) <= 64, "Form path must have 1 to 64 occurrences")
    container = pdf.pages[page_index]
    resources = resources_for(container)
    state = {"font": None, "size": 0, "char": 0, "word": 0, "render": 0, "actual": False}
    seen = set()
    for ordinal in path:
        stream = stream_for(container, pdf)
        index, args, _, state = locate(stream, resources, state, "form", ordinal)
        form = resources["/XObject"][args[0]].get_object()
        require(form.get("/Subtype") == "/Form", "Selected Do is not a form")
        identity = id(form)
        require(identity not in seen, "Cyclic form path")
        seen.add(identity)
        child_resources = resources_for(form, resources)
        if writer:
            private_resources = clone_resources(resources)
            xobjects = private_resources["/XObject"]
            n = 0
            while NameObject(f"/NestedEdit{n}") in xobjects:
                n += 1
            name = NameObject(f"/NestedEdit{n}")
            form = clone_form(form)
            form[NameObject("/Resources")] = clone_resources(child_resources)
            xobjects[name] = writer._add_object(form)
            container[NameObject("/Resources")] = private_resources
            operations = stream.operations
            operations[index] = ([name], b"Do")
            stream.operations = operations
            set_content(container, stream, writer)
        container = form
        resources = resources_for(container, child_resources)
    return container, resources, state


def text_parts(args, op):
    return args[0] if op == b"TJ" else [args[-1]]


def has_signature(reader):
    if "/Perms" in reader.root_object:
        return True
    acroform = reader.root_object.get("/AcroForm")
    fields = acroform.get_object()["/Fields"] if acroform and "/Fields" in acroform.get_object() else []
    pending = [(f, None) for f in fields]
    seen = set()
    while pending:
        reference, inherited_type = pending.pop()
        field = reference.get_object()
        require(id(field) not in seen, "Cyclic form field tree")
        seen.add(id(field))
        field_type = field.get("/FT", inherited_type)
        if field_type == "/Sig" and "/V" in field and not isinstance(field["/V"], NullObject):
            return True
        pending.extend((child, field_type) for child in (field["/Kids"] if "/Kids" in field else []))
    return False


def edit(request):
    require(pypdf.__version__ == "6.1.3", "Nested editor requires pypdf 6.1.3")
    source = Path(request["source"]).resolve()
    destination = Path(request["destination"]).resolve()
    require(source != destination and not destination.exists(), "Destination must be new")
    original = source.read_bytes()
    source_hash = hashlib.sha256(original).hexdigest()
    require(source_hash.lower() == request["sourceSha256"].lower(), "Source changed; refresh selection")
    try:
        reader = PdfReader(io.BytesIO(original), strict=True)
    except DependencyError:
        # pypdf tries the empty password during construction. The nested runtime
        # intentionally has no crypto backend because encrypted edits are rejected.
        raise UnsupportedEdit("Encrypted nested edits require a separate password-preserving workflow") from None
    require(not reader.is_encrypted, "Encrypted nested edits require a separate password-preserving workflow")
    # Rewriting a signed document would invalidate its signature.
    require(not has_signature(reader), "Signed nested edits require a separate signature workflow")
    writer = PdfWriter(clone_from=reader)
    require("/StructTreeRoot" not in reader.root_object, "Tagged documents require a structure-tree-aware edit workflow")
    page_index, path = request["pageIndex"], request["formPath"]
    leaf, resources, state = walk(writer, page_index, path, writer)
    stream = stream_for(leaf, writer)
    index, args, op, state = locate(stream, resources, state, "text", request["textIndex"])
    require(state["font"] is not None and state["size"] != 0, "Missing or zero-size font")
    require(0 <= state["render"] <= 3, "Text that changes clipping requires a separate edit workflow")
    require(not state["actual"], "ActualText replacement requires a tagged-text workflow")
    codec = FontCodec(state["font"])
    parts = text_parts(args, op)
    original_text = "".join(codec.decode(p.original_bytes) for p in parts if isinstance(p, STRINGS))
    require(original_text == request["expectedText"], "Selected text changed; refresh selection")
    new_bytes = codec.encode(request["replacement"])
    old_advance = sum(codec.advance(p.original_bytes, state) if isinstance(p, STRINGS) else -number(p)
                      for p in parts)
    leading = []
    for p in parts:
        if isinstance(p, STRINGS) and p.original_bytes:
            break
        if not isinstance(p, STRINGS):
            leading.append(p)
    new_advance = codec.advance(new_bytes, state) - sum(number(p) for p in leading)
    # Keep the next text run at its original position, even when the replacement is longer.
    replacement = ArrayObject([*leading, ByteStringObject(new_bytes), FloatObject(new_advance - old_advance)])
    prefix = []
    if op == b'"':
        prefix.extend([([args[0]], b"Tw"), ([args[1]], b"Tc")])
    if op in (b"'", b'"'):
        prefix.append(([], b"T*"))
    operations = stream.operations
    operations[index:index+1] = [*prefix, ([replacement], b"TJ")]
    stream.operations = operations
    set_content(leaf, stream, writer)
    # Write only after all selection, mapping and preservation checks have succeeded.
    with destination.open("xb") as target:
        writer.write(target)
    reopened = PdfReader(str(destination), strict=True)
    final_leaf, final_resources, final_state = walk(reopened, page_index, path)
    _, final_args, final_op, final_state = locate(stream_for(final_leaf, reopened), final_resources,
                                                final_state, "text", request["textIndex"])
    final_codec = FontCodec(final_state["font"])
    persisted = "".join(final_codec.decode(p.original_bytes) for p in text_parts(final_args, final_op)
                        if isinstance(p, STRINGS))
    require(persisted == request["replacement"], "Nested edit failed save/reopen verification")
    require(hashlib.sha256(source.read_bytes()).hexdigest() == source_hash, "Source changed during edit")
    return {"nestedTextPersistence": True, "formDepth": len(path), "sourceSha256": source_hash}


if __name__ == "__main__":
    try:
        print(json.dumps(edit(json.load(sys.stdin))))
    except UnsupportedEdit as error:
        print(str(error), file=sys.stderr)
        sys.exit(2)
    except Exception:
        # Do not leak PDF content or paths through parser exception messages.
        print("Nested edit failed while parsing or saving the document", file=sys.stderr)
        sys.exit(1)
