"""Isolated pypdf AcroForm-fill proof. Inputs stay read-only; all PDFs are generated under artifacts."""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ROOT / "tools" / "python-packages"))

from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, ContentStream, DictionaryObject, NameObject, NumberObject, TextStringObject, encode_pdfdocencoding
from candidate import FillCanceled, FillRefused, SourceChanged, fill_candidate


SOURCE = ROOT / "fixtures" / "corpus-v1" / "compatibility" / "interactive-features.pdf"
OUT = ROOT / "artifacts" / ("form-fill-proof-" + uuid.uuid4().hex)
WORKER = ROOT / "App.Worker" / "bin" / "Debug" / "net10.0" / "App.Worker.exe"
POPPLER = Path(os.environ.get("POPPLER_BIN", ""))
PDFTOPPM = POPPLER / "pdftoppm.exe"
FILL = "café – déjà vu"
CHECKS: list[str] = []


def check(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)
    CHECKS.append(message)


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def widget_for(writer: PdfWriter, field_name: str):
    for ref in writer.pages[0].get("/Annots", []):
        annot = ref.get_object()
        if annot.get("/Subtype") != "/Widget":
            continue
        parent = annot.get("/Parent")
        field = parent.get_object() if parent is not None else annot
        if field.get("/T") == field_name:
            return ref, annot, field
    raise AssertionError(f"No page widget found for {field_name}")


def inherited_source(destination: Path) -> None:
    writer = PdfWriter()
    writer.clone_document_from_reader(PdfReader(str(SOURCE)))
    widget_ref, widget, field = widget_for(writer, "sample_name")
    check(widget_ref is not None, "source text field has a page widget")
    # The frozen fixture uses a merged field/widget dictionary. Move field keys to
    # a parent field object to exercise the inherited AcroForm field-tree path.
    parent = DictionaryObject()
    for key in ("/FT", "/T", "/Ff", "/V", "/DV", "/MaxLen", "/DA"):
        if key in field:
            parent[NameObject(key)] = field[key]
            del field[NameObject(key)]
    parent[NameObject("/Kids")] = ArrayObject([widget_ref])
    parent_ref = writer._add_object(parent)
    widget[NameObject("/Parent")] = parent_ref
    acro = writer._root_object[NameObject("/AcroForm")].get_object()
    acro[NameObject("/Fields")] = ArrayObject(
        [ref for ref in acro[NameObject("/Fields")] if ref != widget_ref] + [parent_ref]
    )
    write(writer, destination)


def write(writer: PdfWriter, path: Path) -> None:
    with path.open("wb") as stream:
        writer.write(stream)


def set_parent_value(source: Path, destination: Path, *, flags: int | None = None,
                     max_length: int | None = None, action: bool = False,
                     widget_action: bool = False, page_action: bool = False,
                     document_action: bool = False, calculation: bool = False,
                     xfa: bool = False, duplicate_name: bool = False,
                     populated_signature: bool = False) -> None:
    writer = PdfWriter()
    writer.clone_document_from_reader(PdfReader(str(source)))
    widget_ref, widget, field = widget_for(writer, "sample_name")
    if flags is not None:
        field[NameObject("/Ff")] = NumberObject(flags)
    if max_length is not None:
        field[NameObject("/MaxLen")] = NumberObject(max_length)
    if action:
        js_action = DictionaryObject({NameObject("/S"): NameObject("/JavaScript"),
                                      NameObject("/JS"): TextStringObject("void(0)")})
        field[NameObject("/AA")] = DictionaryObject({NameObject("/V"): writer._add_object(js_action)})
    if widget_action:
        js_action = DictionaryObject({NameObject("/S"): NameObject("/JavaScript"),
                                      NameObject("/JS"): TextStringObject("void(0)")})
        widget[NameObject("/A")] = writer._add_object(js_action)
    if page_action:
        js_action = DictionaryObject({NameObject("/S"): NameObject("/JavaScript"),
                                      NameObject("/JS"): TextStringObject("void(0)")})
        writer.pages[0][NameObject("/AA")] = DictionaryObject({NameObject("/O"): writer._add_object(js_action)})
    if document_action:
        js_action = DictionaryObject({NameObject("/S"): NameObject("/JavaScript"),
                                      NameObject("/JS"): TextStringObject("void(0)")})
        writer._root_object[NameObject("/OpenAction")] = writer._add_object(js_action)
    if calculation:
        acro = writer._root_object[NameObject("/AcroForm")].get_object()
        acro[NameObject("/CO")] = ArrayObject([acro[NameObject("/Fields")][-1]])
    if xfa:
        acro = writer._root_object[NameObject("/AcroForm")].get_object()
        acro[NameObject("/XFA")] = TextStringObject("synthetic unsupported XFA")
    if duplicate_name:
        acro = writer._root_object[NameObject("/AcroForm")].get_object()
        duplicate = DictionaryObject({NameObject("/FT"): NameObject("/Tx"),
                                     NameObject("/T"): TextStringObject("sample_name"),
                                     NameObject("/V"): TextStringObject("duplicate")})
        acro[NameObject("/Fields")].append(writer._add_object(duplicate))
    if populated_signature:
        _, signature_widget, signature_field = widget_for(writer, "unsigned_signature")
        signature = DictionaryObject({NameObject("/Type"): NameObject("/Sig"),
                                     NameObject("/ByteRange"): ArrayObject([NumberObject(0), NumberObject(0), NumberObject(0), NumberObject(0)])})
        signature_field[NameObject("/V")] = writer._add_object(signature)
    write(writer, destination)


def set_font_variant(source: Path, destination: Path, *, missing: bool = False,
                     custom: bool = False, malformed_da: bool = False) -> None:
    writer = PdfWriter()
    writer.clone_document_from_reader(PdfReader(str(source)))
    acro = writer._root_object[NameObject("/AcroForm")].get_object()
    if missing:
        fonts = acro[NameObject("/DR")].get_object()[NameObject("/Font")].get_object()
        del fonts[NameObject("/Helv")]
    if custom:
        font = acro[NameObject("/DR")].get_object()[NameObject("/Font")][NameObject("/Helv")].get_object()
        font[NameObject("/BaseFont")] = NameObject("/UnapprovedCustomSans")
    if malformed_da:
        _, _, field = widget_for(writer, "sample_name")
        field[NameObject("/DA")] = TextStringObject("/Helv 0 Tf /Helv 12 Tf")
    write(writer, destination)


def render_pdfium(path: Path) -> tuple[int, int, int, bytes]:
    proc = subprocess.Popen([str(WORKER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, encoding="utf-8")
    doc = "b67f9a7b-9232-4dab-86a6-6e66ecfa0d92"
    seq = 0

    def request(operation: str, args: dict, revision: int = 0) -> dict:
        nonlocal seq
        seq += 1
        req = {"RequestId": f"00000000-0000-0000-0000-{seq:012d}", "DocumentId": doc,
               "Revision": revision, "Operation": operation, "Arguments": args}
        assert proc.stdin is not None and proc.stdout is not None
        proc.stdin.write(json.dumps(req, ensure_ascii=False) + "\n")
        proc.stdin.flush()
        response = json.loads(proc.stdout.readline())
        if not response["Success"]:
            raise RuntimeError(f"PDFium {operation} failed: {response['Error']}")
        return response

    try:
        request("Open", {"Path": str(path)})
        data = request("RenderForPrint", {"Page": 0, "Width": 612, "Height": 792}, 0)["Data"]
        request("Close", {}, 0)
        return (data["Width"], data["Height"], data["Stride"], __import__("base64").b64decode(data["Bgra"]))
    finally:
        if proc.stdin:
            proc.stdin.close()
        proc.wait(timeout=10)


def render_poppler(path: Path, stem: str) -> tuple[int, int, bytes]:
    prefix = OUT / stem
    subprocess.run([str(PDFTOPPM), "-f", "1", "-l", "1", "-r", "72", "-singlefile",
                    str(path), str(prefix)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    raw = prefix.with_suffix(".ppm").read_bytes()
    if not raw.startswith(b"P6"):
        raise ValueError("Poppler did not return a binary PPM image")
    i = 2

    def token() -> bytes:
        nonlocal i
        while i < len(raw):
            if raw[i] == 35:
                while i < len(raw) and raw[i] not in (10, 13):
                    i += 1
            elif raw[i] in b" \t\r\n":
                i += 1
            else:
                break
        start = i
        while i < len(raw) and raw[i] not in b" \t\r\n":
            i += 1
        return raw[start:i]

    width, height, maximum = int(token()), int(token()), int(token())
    if maximum != 255:
        raise ValueError(f"Unsupported PPM channel maximum {maximum}")
    if raw[i:i + 2] == b"\r\n":
        i += 2
    else:
        i += 1
    pixels = raw[i:]
    if len(pixels) != width * height * 3:
        raise ValueError("Poppler PPM size does not match its header")
    png_raw = b"".join(b"\x00" + pixels[y * width * 3:(y + 1) * width * 3] for y in range(height))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack("!I", len(data)) + kind + data + struct.pack("!I", zlib.crc32(kind + data) & 0xffffffff)

    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack("!IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(png_raw)) + chunk(b"IEND", b"")
    (OUT / f"{stem}.png").write_bytes(png)
    return width, height, pixels


def region_differences(before: tuple, after: tuple, channels: int, stride: int) -> int:
    # Widget Rect [50,640,350,670] on a 612 x 792 page; probe its interior.
    differences = 0
    for y in range(122, 152):
        for x in range(55, 345):
            left = y * stride + x * channels
            if before[left:left + 3] != after[left:left + 3]:
                differences += 1
    return differences


def refuse(source: Path, field_name: str, expected_fragment: str, label: str,
           *, value: str = FILL) -> None:
    candidate = OUT / (label.lower().replace(" ", "-") + "-must-not-exist.pdf")
    if candidate.exists():
        candidate.unlink()
    try:
        fill_candidate(source, candidate, field_name, value)
    except FillRefused as error:
        reason = str(error)
        check(expected_fragment in reason, f"{label} is refused before mutation ({reason})")
    else:
        raise AssertionError(f"{label} was not refused by the fill entry point")
    check(not candidate.exists(), f"{label} refusal publishes no candidate")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    source_hash = digest(SOURCE)
    encrypted = ROOT / "fixtures" / "corpus-v1" / "compatibility" / "password-aes256.pdf"
    encrypted_hash = digest(encrypted)
    inherited = OUT / "inherited-input.pdf"
    filled = OUT / "unicode-filled.pdf"
    inherited_source(inherited)
    candidate_paths: list[Path] = []
    fill_result = fill_candidate(inherited, filled, "sample_name", FILL,
                                 before_publish=lambda candidate: candidate_paths.append(candidate))
    check(len(candidate_paths) == 1 and candidate_paths[0].parent == filled.parent and
          candidate_paths[0] != filled and not candidate_paths[0].exists(),
          "candidate uses a unique sibling temporary and is renamed only after validation")
    check(fill_result["Destination"] == str(filled.resolve()) and fill_result["FieldValue"] == FILL,
          "candidate result reports the create-only output and exact Unicode value")
    second_output = OUT / "unicode-filled-second.pdf"
    second_candidate: list[Path] = []
    fill_candidate(inherited, second_output, "sample_name", FILL,
                   before_publish=lambda candidate: second_candidate.append(candidate))
    check(len(second_candidate) == 1 and second_candidate[0] != candidate_paths[0],
          "separate fills receive distinct sibling candidate names")

    input_reader = PdfReader(str(inherited))
    filled_reader = PdfReader(str(filled))
    source_fields = input_reader.get_fields() or {}
    fields = filled_reader.get_fields() or {}
    check(source_fields["sample_name"].get("/FT") == "/Tx" and
          "/FT" not in widget_for_reader(input_reader, "sample_name"),
          "test input uses inherited text-field keys on a parent dictionary")
    check(fields["sample_name"].get("/V") == FILL,
          "Unicode field value survives pypdf save and reopen through the inherited field tree")
    check(fields["unsigned_signature"].get("/FT") == "/Sig",
          "unrelated unsigned signature field remains present")
    widget = widget_for_reader(filled_reader, "sample_name")
    ap = widget.get("/AP").get_object()["/N"].get_object().get_data()
    old_ap = widget_for_reader(input_reader, "sample_name").get("/AP").get_object()["/N"].get_object().get_data()
    check(len(ap) > 0 and ap != old_ap, "pypdf regenerates the field's normal appearance stream")
    acro = filled_reader.trailer["/Root"]["/AcroForm"].get_object()
    check(str(acro.get("/NeedAppearances", "False")) != "True",
          "saved output does not rely on a viewer prompt to regenerate appearances")
    stream = ContentStream(widget.get("/AP").get_object()["/N"].get_object(), filled_reader)
    appearance_text = [str(operands[0]) for operands, operator in stream.operations
                       if operator == b"Tj" and operands]
    check(appearance_text == [FILL], "generated normal appearance carries the exact requested Unicode text")
    appearance_bytes = [operands[0].get_original_bytes() for operands, operator in stream.operations
                        if operator == b"Tj" and operands]
    check(appearance_bytes == [encode_pdfdocencoding(FILL)],
          "appearance bytes exactly match the proven glyph encoding sequence")
    # pypdf's independent parser reports the actual inherited value; the visible
    # checks below establish that the appearance contains rendered glyphs too.

    pdfium_before = render_pdfium(inherited)
    pdfium_after = render_pdfium(filled)
    check(pdfium_before[:3] == pdfium_after[:3] == (612, 792, pdfium_before[2]),
          "PDFium returns bounded render dimensions before and after save/reopen")
    pdfium_delta = region_differences(pdfium_before[3], pdfium_after[3], 4, pdfium_after[2])
    check(pdfium_delta >= 10, f"PDFium form renderer displays the changed Unicode value ({pdfium_delta} changed widget pixels)")

    poppler_before = render_poppler(inherited, "poppler-before")
    poppler_after = render_poppler(filled, "poppler-after")
    check(poppler_before[:2] == poppler_after[:2] == (612, 792),
          "Poppler independently renders the same page geometry")
    poppler_delta = region_differences(poppler_before[2], poppler_after[2], 3, 612 * 3)
    check(poppler_delta >= 10, f"Poppler displays the changed Unicode value from the saved appearance ({poppler_delta} changed widget pixels)")

    readonly = OUT / "readonly.pdf"
    password = OUT / "password.pdf"
    rich = OUT / "richtext.pdf"
    action = OUT / "action.pdf"
    widget_action = OUT / "widget-action.pdf"
    page_action = OUT / "page-action.pdf"
    document_action = OUT / "document-action.pdf"
    calculation = OUT / "calculation.pdf"
    xfa = OUT / "xfa.pdf"
    duplicate = OUT / "duplicate-name.pdf"
    populated_sig = OUT / "populated-signature.pdf"
    too_long = OUT / "short-maxlen.pdf"
    missing_font = OUT / "missing-font.pdf"
    custom_font = OUT / "custom-font.pdf"
    malformed_da = OUT / "multiple-font-operators.pdf"
    set_parent_value(inherited, readonly, flags=1)
    set_parent_value(inherited, password, flags=1 << 13)
    set_parent_value(inherited, rich, flags=1 << 25)
    set_parent_value(inherited, action, action=True)
    set_parent_value(inherited, widget_action, widget_action=True)
    set_parent_value(inherited, page_action, page_action=True)
    set_parent_value(inherited, document_action, document_action=True)
    set_parent_value(inherited, calculation, calculation=True)
    set_parent_value(inherited, xfa, xfa=True)
    set_parent_value(inherited, duplicate, duplicate_name=True)
    set_parent_value(inherited, populated_sig, populated_signature=True)
    set_parent_value(inherited, too_long, max_length=3)
    set_font_variant(inherited, missing_font, missing=True)
    set_font_variant(inherited, custom_font, custom=True)
    set_font_variant(inherited, malformed_da, malformed_da=True)
    refuse(readonly, "sample_name", "read-only", "Read-only field")
    refuse(password, "sample_name", "password", "Password field")
    refuse(rich, "sample_name", "rich text", "Rich text field")
    refuse(inherited, "unsigned_signature", "plain text", "Signature field")
    refuse(action, "sample_name", "actions", "Action-bearing field")
    refuse(widget_action, "sample_name", "actions", "Widget action")
    refuse(page_action, "sample_name", "page actions", "Page action")
    refuse(document_action, "sample_name", "document actions", "Document action")
    refuse(calculation, "sample_name", "calculation", "Calculation action")
    refuse(xfa, "sample_name", "XFA", "XFA document")
    refuse(duplicate, "sample_name", "ambiguous", "Duplicate field name")
    refuse(populated_sig, "sample_name", "populated signature", "Populated signature")
    refuse(encrypted, "sample_name", "encrypted", "Encrypted document")
    refuse(too_long, "sample_name", "MaxLen", "MaxLen overflow")
    refuse(missing_font, "sample_name", "cannot be resolved", "Missing appearance font")
    refuse(custom_font, "sample_name", "standard Type1", "Custom appearance font")
    refuse(malformed_da, "sample_name", "ambiguous", "Multiple font operators")
    refuse(inherited, "sample_name", "overflow", "Widget overflow", value="W" * 100)
    refuse(inherited, "sample_name", "glyphs", "Unsupported glyph", value="東京")
    refuse(inherited, "sample_name", "without proven", "Unmapped standard-font glyph", value="•")

    try:
        fill_candidate(inherited, inherited, "sample_name", FILL)
    except FillRefused as error:
        check("source" in str(error), "same-source destination is refused")
    else:
        raise AssertionError("same-source destination was accepted")
    check(digest(inherited) == digest(OUT / "inherited-input.pdf"), "same-source refusal preserves the input copy")

    occupied = OUT / "occupied.pdf"
    occupied.write_bytes(b"existing destination")
    try:
        fill_candidate(inherited, occupied, "sample_name", FILL)
    except FillRefused as error:
        check("already exists" in str(error), "existing destination is refused")
    else:
        raise AssertionError("existing destination was accepted")
    check(occupied.read_bytes() == b"existing destination", "existing destination bytes are preserved")

    canceled_out = OUT / "canceled.pdf"
    cancel_count = 0

    def cancel_before_publish() -> bool:
        nonlocal cancel_count
        cancel_count += 1
        return cancel_count >= 4

    try:
        fill_candidate(inherited, canceled_out, "sample_name", FILL, is_canceled=cancel_before_publish)
    except FillCanceled:
        check(not canceled_out.exists(), "cancellation after candidate validation prevents publication")
    else:
        raise AssertionError("candidate cancellation was ignored")
    check(not list(OUT.glob(f".{canceled_out.stem}.*.candidate.pdf")), "cancellation removes its sibling candidate")

    drift_source = OUT / "source-drift.pdf"
    drift_source.write_bytes(inherited.read_bytes())
    drift_out = OUT / "source-drift-output.pdf"

    def mutate_source_for_proof(_: Path) -> None:
        with drift_source.open("ab") as stream:
            stream.write(b"\n% source drift test\n")

    try:
        fill_candidate(drift_source, drift_out, "sample_name", FILL,
                       before_publish=mutate_source_for_proof)
    except SourceChanged:
        check(not drift_out.exists(), "source hash drift blocks publication")
    else:
        raise AssertionError("source hash drift was ignored")
    check(not list(OUT.glob(f".{drift_out.stem}.*.candidate.pdf")), "source-drift refusal removes its sibling candidate")

    race_out = OUT / "publish-race.pdf"

    def create_destination_race(_: Path) -> None:
        race_out.write_bytes(b"racing destination")

    try:
        fill_candidate(inherited, race_out, "sample_name", FILL,
                       after_destination_check=create_destination_race)
    except FileExistsError:
        check(race_out.read_bytes() == b"racing destination",
              "atomic create-only rename preserves a racing destination")
    else:
        raise AssertionError("atomic create-only publish overwrote a racing destination")
    check(not list(OUT.glob(f".{race_out.stem}.*.candidate.pdf")), "publish collision removes its sibling candidate")

    check(digest(SOURCE) == source_hash and digest(encrypted) == encrypted_hash,
          "frozen form and encrypted fixtures remain unchanged")

    results = {"Passed": CHECKS, "UnicodeValue": FILL, "PdfiumChangedPixels": pdfium_delta,
               "PopplerChangedPixels": poppler_delta, "SourceSha256": source_hash,
               "PypdfVersion": __import__("pypdf").__version__}
    (OUT / "results.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PASS {len(CHECKS)} existing-form fill checks. Evidence: {OUT}")


def widget_for_reader(reader: PdfReader, name: str):
    for ref in reader.pages[0].get("/Annots", []):
        annot = ref.get_object()
        if annot.get("/Subtype") != "/Widget":
            continue
        parent = annot.get("/Parent")
        field = parent.get_object() if parent is not None else annot
        if field.get("/T") == name:
            return annot
    raise AssertionError(f"No page widget found for {name}")


if __name__ == "__main__":
    main()
