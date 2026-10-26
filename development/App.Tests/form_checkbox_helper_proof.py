"""Focused independent proof for single-checkbox helper updates."""
from __future__ import annotations

import hashlib
import sys
from pathlib import Path
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "App.Advanced"))
from forms import FillRefused, fill_candidate
from pypdf import PdfReader, PdfWriter
from pypdf.generic import (
    ArrayObject, DecodedStreamObject, DictionaryObject, FloatObject,
    NameObject, NumberObject, TextStringObject,
)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def add_appearance(writer: PdfWriter, content: bytes):
    stream = DecodedStreamObject()
    stream.set_data(content)
    stream.update({
        NameObject("/Type"): NameObject("/XObject"),
        NameObject("/Subtype"): NameObject("/Form"),
        NameObject("/BBox"): ArrayObject([FloatObject(0), FloatObject(0), FloatObject(20), FloatObject(20)]),
        NameObject("/Resources"): DictionaryObject(),
    })
    return writer._add_object(stream)


def make_source(base: Path, output: Path) -> None:
    writer = PdfWriter()
    writer.clone_document_from_reader(PdfReader(str(base)))
    page = writer.pages[0]
    acro = writer._root_object[NameObject("/AcroForm")].get_object()
    off = add_appearance(writer, b"q 1 w 0 G 1 1 18 18 re S Q")
    on = add_appearance(writer, b"q 1 w 0 G 1 1 18 18 re S 3 10 m 8 4 l 17 16 l S Q")
    widget = DictionaryObject({
        NameObject("/Type"): NameObject("/Annot"),
        NameObject("/Subtype"): NameObject("/Widget"),
        NameObject("/FT"): NameObject("/Btn"),
        NameObject("/T"): TextStringObject("helper_check"),
        NameObject("/Rect"): ArrayObject([FloatObject(420), FloatObject(700), FloatObject(440), FloatObject(720)]),
        NameObject("/F"): NumberObject(4),
        NameObject("/Ff"): NumberObject(0),
        NameObject("/V"): NameObject("/Off"),
        NameObject("/AS"): NameObject("/Off"),
        NameObject("/AP"): DictionaryObject({NameObject("/N"): DictionaryObject({NameObject("/Off"): off, NameObject("/Yes"): on})}),
    })
    widget_ref = writer._add_object(widget)
    page[NameObject("/Annots")].append(widget_ref)
    acro[NameObject("/Fields")].append(widget_ref)
    with output.open("wb") as stream:
        writer.write(stream)


def checkbox(path: Path):
    reader = PdfReader(str(path))
    fields = reader.get_fields()
    field = fields["helper_check"]
    page_widgets = [a.get_object() for a in reader.pages[0]["/Annots"]
                    if a.get_object().get("/Subtype") == "/Widget" and a.get_object().get("/T") == "helper_check"]
    assert len(page_widgets) == 1
    widget = page_widgets[0]
    return str(field["/V"]), str(widget["/AS"]), widget


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: form_checkbox_helper_proof.py base.pdf output-directory")
    base, parent = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    root = parent / ("run-" + uuid4().hex)
    root.mkdir(parents=True, exist_ok=False)
    source = root / "checkbox-source.pdf"
    checked = root / "checkbox-checked.pdf"
    unchecked = root / "checkbox-unchecked.pdf"
    make_source(base, source)
    source_hash = digest(source)
    fill_candidate(source, checked, "helper_check", "true")
    assert checkbox(checked)[:2] == ("/Yes", "/Yes")
    assert checkbox(source)[:2] == ("/Off", "/Off")
    assert checkbox(checked)[2]["/AP"]["/N"]["/Yes"].get_object().get_data() == \
           checkbox(source)[2]["/AP"]["/N"]["/Yes"].get_object().get_data()
    fill_candidate(checked, unchecked, "helper_check", "false")
    assert checkbox(unchecked)[:2] == ("/Off", "/Off")
    assert digest(source) == source_hash

    ambiguous = PdfReader(str(source))
    widget = next(a.get_object() for a in ambiguous.pages[0]["/Annots"]
                  if a.get_object().get("/T") == "helper_check")
    normal = widget["/AP"]["/N"]
    normal[NameObject("/Maybe")] = normal[NameObject("/Yes")]
    malformed = root / "ambiguous-checkbox.pdf"
    writer = PdfWriter()
    writer.clone_document_from_reader(ambiguous)
    with malformed.open("wb") as stream:
        writer.write(stream)
    try:
        fill_candidate(malformed, root / "must-not-exist.pdf", "helper_check", "true")
    except FillRefused:
        pass
    else:
        raise AssertionError("ambiguous checkbox appearance states were accepted")
    assert not (root / "must-not-exist.pdf").exists()
    print("PASS: checked and unchecked checkbox values, /AS states, appearance preservation, source preservation, and ambiguous-state refusal")


if __name__ == "__main__":
    main()
