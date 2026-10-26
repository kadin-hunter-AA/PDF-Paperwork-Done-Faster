"""Host-only smoke of newly added operations in one exact packaged runtime."""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True

# The host interpreter may not have pypdf; use only the private package runtime.
if len(sys.argv) >= 3:
    sys.path.insert(0, str(Path(sys.argv[2]).resolve() / "advanced" / "packages"))
from pypdf import PdfReader, PdfWriter
from pypdf.generic import (
    ArrayObject, DecodedStreamObject, DictionaryObject, FloatObject,
    NameObject, NumberObject, TextStringObject,
)


if len(sys.argv) < 4:
    raise SystemExit("usage: verify-completion-package.py ROOT PACKAGE FRESH_OUTPUT")
ROOT, PACKAGE, OUTPUT = (Path(value).resolve() for value in sys.argv[1:4])
OUTPUT.mkdir(parents=True, exist_ok=False)
PYTHON = PACKAGE / "advanced" / "python.exe"
HELPER = PACKAGE / "advanced" / "worker.py"
WORKER = PACKAGE / "worker" / "App.Worker.exe"
checks = []


def check(ok, message):
    if not ok:
        raise AssertionError(message)
    checks.append(message)


def sha(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def extended(path):
    value = str(Path(path).absolute())
    if os.name != "nt" or value.startswith("\\\\?\\"):
        return value
    return "\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value


def invoke(operation, source, destination, **kwargs):
    request = {"Operation": operation, "Input": extended(source), "Output": extended(destination), **kwargs}
    run = subprocess.run([str(PYTHON), "-I", "-B", extended(HELPER)], input=json.dumps(request) + "\n",
                         capture_output=True, text=True, timeout=240, cwd=HELPER.parent)
    try:
        response = json.loads(run.stdout)
    except json.JSONDecodeError as error:
        raise AssertionError(f"Packaged {operation} returned invalid JSON: {run.stderr[:500]}") from error
    check(run.returncode == 0 and response.get("Success") is True,
          f"Packaged companion completes {operation}: {response.get('Error', '')}")
    check(not run.stderr, f"Packaged {operation} writes no diagnostics to stderr")
    return response["Data"]


def native_steps(source, operations):
    """Run a document's requests together in one package-worker process."""
    document = str(uuid.uuid4())
    steps = [("Open", {"Path": str(source)}), *operations, ("Close", {})]
    revision = 0
    request_lines = []
    for name, arguments in steps:
        request_lines.append({"RequestId": str(uuid.uuid4()), "DocumentId": document,
                              "Revision": revision, "Operation": name, "Arguments": arguments})
        if name in {"AddDrawing", "AddText", "AddMarkup", "ReplaceText"}:
            revision += 1
    run = subprocess.run([str(WORKER)], input="\n".join(json.dumps(item) for item in request_lines) + "\n",
                         capture_output=True, text=True, timeout=120, cwd=WORKER.parent)
    result = [json.loads(line) for line in run.stdout.splitlines()]
    if run.returncode != 0 or len(result) != len(request_lines):
        raise AssertionError(f"Packaged worker returned an incomplete response sequence: {run.stderr[:500]}")
    return result


def open_error(source):
    request = {"RequestId": str(uuid.uuid4()), "DocumentId": str(uuid.uuid4()), "Revision": 0,
               "Operation": "Open", "Arguments": {"Path": str(source)}}
    run = subprocess.run([str(WORKER)], input=json.dumps(request) + "\n", capture_output=True,
                         text=True, timeout=60, cwd=WORKER.parent)
    rows = [json.loads(line) for line in run.stdout.splitlines()]
    if run.returncode != 0 or len(rows) != 1:
        raise AssertionError(f"Packaged worker could not classify {source.name}: {run.stderr[:500]}")
    return rows[0]


# Native Open must preserve the distinct password-required discriminator.
encrypted = ROOT / "fixtures/corpus-v1/compatibility/password-aes256.pdf"
malformed = ROOT / "fixtures/corpus-v1/corpus/malformed.pdf"
encrypted_hash, malformed_hash = sha(encrypted), sha(malformed)
password_result = open_error(encrypted)
malformed_result = open_error(malformed)
check(not password_result["Success"] and password_result.get("ErrorCode") == "password_required",
      "Packaged native worker types encrypted input as password-required")
check(not malformed_result["Success"] and malformed_result.get("ErrorCode") is None and
      "password" not in str(malformed_result.get("Error", "")).lower(),
      "Packaged native worker keeps malformed-PDF failure distinct from password-required")
check(sha(encrypted) == encrypted_hash and sha(malformed) == malformed_hash,
      "Password and malformed-input checks preserve both frozen sources")


# Drawing operation: require native annotations, exact geometry, print flags, and reopen.
drawing_source = ROOT / "fixtures/corpus-v1/engine/original.pdf"
drawing_hash = sha(drawing_source)
drawing_output = OUTPUT / "drawings-saved.pdf"
drawing_ops = [
    ("AddDrawing", {"Page": 0, "Kind": "Line", "Left": 90, "Bottom": 600, "Right": 220, "Top": 690}),
    ("AddDrawing", {"Page": 0, "Kind": "Arrow", "Left": 90, "Bottom": 550, "Right": 220, "Top": 590}),
    ("AddDrawing", {"Page": 0, "Kind": "Rectangle", "Left": 250, "Bottom": 600, "Right": 370, "Top": 690}),
    ("AddDrawing", {"Page": 0, "Kind": "Ellipse", "Left": 400, "Bottom": 600, "Right": 520, "Top": 690}),
    ("SaveCandidate", {"Path": str(drawing_output)}),
]
drawing_responses = native_steps(drawing_source, drawing_ops)
check(all(item["Success"] for item in drawing_responses),
      "Packaged native worker adds line, arrow, Square and Circle drawings and saves a candidate")
reader = PdfReader(str(drawing_output), strict=True)
annotations = [ref.get_object() for ref in reader.pages[0].get("/Annots", [])]
expected_rects = {"/Square": [250, 600, 370, 690], "/Circle": [400, 600, 520, 690]}
ink_matches = [annotation for annotation in annotations if annotation.get("/Subtype") == "/Ink"]
check(len(ink_matches) == 2, "Independent parser finds native Ink annotations for line and arrow")
ink_matches.sort(key=lambda annotation: float(annotation["/Rect"][1]), reverse=True)
check(list(ink_matches[0]["/Rect"]) == [90, 600, 220, 690], "Independent parser confirms line geometry")
check(list(ink_matches[0]["/InkList"][0]) == [90, 600, 220, 690], "Independent parser confirms line stroke endpoints")
arrow_paths = ink_matches[1]["/InkList"]
check(len(arrow_paths) == 3 and list(arrow_paths[0]) == [90, 550, 220, 590] and
      all(list(path)[:2] == [220, 590] for path in arrow_paths[1:]),
      "Independent parser confirms arrow shaft and two joined arrowhead strokes")
for annotation in ink_matches:
    check(int(annotation.get("/F", 0)) & 4 != 0, "Independent parser confirms Ink annotation has the PDF Print flag")
for subtype, rectangle in expected_rects.items():
    matches = [annotation for annotation in annotations if annotation.get("/Subtype") == subtype]
    check(len(matches) == 1, f"Independent parser finds one {subtype} drawing annotation")
    check(list(matches[0]["/Rect"]) == rectangle,
          f"Independent parser confirms {subtype} drawing geometry")
    check(int(matches[0].get("/F", 0)) & 4 != 0,
          f"Independent parser confirms {subtype} drawing has the PDF Print flag")
reopened = native_steps(drawing_output, [("Extract", {"Page": 0})])
check(all(item["Success"] for item in reopened) and len(reopened[1]["Data"]["Annotations"]) >= 4,
      "Packaged native worker independently reopens the drawing candidate")
check(sha(drawing_source) == drawing_hash, "Drawing candidate save preserves the frozen source")
check(reopened[1]["Data"].get("SupportsZeroOriginCoordinates") is True,
      "Packaged extraction exposes supported zero-origin page coordinates")

# Offset visible boundaries must be refused before annotation creation, in the exact package.
for mode in ("crop", "media"):
    offset_source = OUTPUT / f"offset-{mode}.pdf"
    writer = PdfWriter(clone_from=PdfReader(str(drawing_source)))
    page = writer.pages[0]
    if mode == "media":
        page.mediabox.lower_left = (100, 200)
        page.mediabox.upper_right = (712, 992)
        page.cropbox.lower_left = (100, 200)
        page.cropbox.upper_right = (712, 992)
    else:
        page.cropbox.lower_left = (100, 200)
        page.cropbox.upper_right = (500, 600)
    with offset_source.open("wb") as stream:
        writer.write(stream)
    offset_hash = sha(offset_source)
    offset_saved = OUTPUT / f"offset-{mode}-saved.pdf"
    document = str(uuid.uuid4())
    operations = [
        ("Open", {"Path": str(offset_source)}), ("Extract", {"Page": 0}),
        ("Render", {"Page": 0, "Width": 320, "Height": 320}),
        ("AddDrawing", {"Page": 0, "Kind": "Rectangle", "Left": 10, "Bottom": 10, "Right": 60, "Top": 60}),
        ("Extract", {"Page": 0}), ("Render", {"Page": 0, "Width": 320, "Height": 320}),
        ("SaveCandidate", {"Path": str(offset_saved)}),
    ]
    requests = [{"RequestId": str(uuid.uuid4()), "DocumentId": document, "Revision": 0,
                 "Operation": name, "Arguments": arguments} for name, arguments in operations]
    run = subprocess.run([str(WORKER)], input="\n".join(json.dumps(item) for item in requests) + "\n",
                         capture_output=True, text=True, timeout=120, cwd=WORKER.parent)
    rows = [json.loads(line) for line in run.stdout.splitlines()]
    check(run.returncode == 0 and len(rows) == len(requests) and all(rows[i]["Success"] for i in (0, 1, 2, 4, 5, 6)),
          f"Packaged offset {mode} refusal retains a readable, renderable revision-zero document")
    check(rows[1]["Data"].get("SupportsZeroOriginCoordinates") is False and not rows[3]["Success"],
          f"Packaged drawing refuses offset {mode} coordinates before committing")
    check(rows[1]["Data"]["Annotations"] == rows[4]["Data"]["Annotations"] and
          rows[2]["Data"]["Bgra"] == rows[5]["Data"]["Bgra"],
          f"Packaged offset {mode} refusal preserves annotation geometry and exact rendered pixels")
    saved_page = PdfReader(str(offset_saved), strict=True).pages[0]
    check(list(saved_page.cropbox) == list(page.cropbox) and list(saved_page.mediabox) == list(page.mediabox),
          f"Independent parser verifies offset {mode} page boundaries survive the refused drawing save")
    check(sha(offset_source) == offset_hash,
          f"Packaged offset {mode} refusal preserves source bytes")


# Checkbox update via the exact companion and raw PDF state inspection.
form_source = ROOT / "fixtures/corpus-v1/compatibility/interactive-features.pdf"
form_hash = sha(form_source)
checkbox_source = OUTPUT / "checkbox-source.pdf"
checkbox_writer = PdfWriter()
checkbox_writer.clone_document_from_reader(PdfReader(str(form_source)))
checkbox_page = checkbox_writer.pages[0]
checkbox_acroform = checkbox_writer._root_object[NameObject("/AcroForm")].get_object()


def appearance_stream(content):
    stream = DecodedStreamObject()
    stream.set_data(content)
    stream.update({
        NameObject("/Type"): NameObject("/XObject"),
        NameObject("/Subtype"): NameObject("/Form"),
        NameObject("/BBox"): ArrayObject([FloatObject(0), FloatObject(0), FloatObject(20), FloatObject(20)]),
        NameObject("/Resources"): DictionaryObject(),
    })
    return checkbox_writer._add_object(stream)


checkbox_off = appearance_stream(b"q 1 w 0 G 1 1 18 18 re S Q")
checkbox_on = appearance_stream(b"q 1 w 0 G 1 1 18 18 re S 3 10 m 8 4 l 17 16 l S Q")
checkbox_widget = DictionaryObject({
    NameObject("/Type"): NameObject("/Annot"),
    NameObject("/Subtype"): NameObject("/Widget"),
    NameObject("/FT"): NameObject("/Btn"),
    NameObject("/T"): TextStringObject("helper_check"),
    NameObject("/Rect"): ArrayObject([FloatObject(420), FloatObject(700), FloatObject(440), FloatObject(720)]),
    NameObject("/F"): NumberObject(4),
    NameObject("/Ff"): NumberObject(0),
    NameObject("/V"): NameObject("/Off"),
    NameObject("/AS"): NameObject("/Off"),
    NameObject("/AP"): DictionaryObject({
        NameObject("/N"): DictionaryObject({NameObject("/Off"): checkbox_off, NameObject("/Yes"): checkbox_on})
    }),
})
checkbox_widget_ref = checkbox_writer._add_object(checkbox_widget)
checkbox_page[NameObject("/Annots")].append(checkbox_widget_ref)
checkbox_acroform[NameObject("/Fields")].append(checkbox_widget_ref)
with checkbox_source.open("wb") as stream:
    checkbox_writer.write(stream)
checkbox_source_hash = sha(checkbox_source)
checkbox_output = OUTPUT / "checkbox-checked.pdf"
invoke("FillForm", checkbox_source, checkbox_output, FieldName="helper_check", Value="true")
form_reader = PdfReader(str(checkbox_output), strict=True)
checkbox_field = (form_reader.get_fields() or {}).get("helper_check")
check(checkbox_field is not None and str(checkbox_field.get("/V")) not in ("/Off", "Off", "None"),
      "Independent parser confirms the checkbox field /V is set")
checkbox_widgets = [annotation.get_object() for page in form_reader.pages
                    for annotation in page.get("/Annots", [])
                    if annotation.get_object().get("/Subtype") == "/Widget" and
                    annotation.get_object().get("/T") == "helper_check"]
check(len(checkbox_widgets) == 1, "Independent parser finds exactly one checkbox widget")
widget = checkbox_widgets[0]
check(str(widget.get("/AS")) == str(checkbox_field.get("/V")),
      "Independent parser confirms the checkbox widget /AS matches field /V")
normal = widget["/AP"]["/N"].get_object()
check(str(widget.get("/AS")) in {str(key) for key in normal.keys()},
      "Checkbox /AS names an explicit normal appearance state")
check(sha(form_source) == form_hash and sha(checkbox_source) == checkbox_source_hash,
      "Packaged checkbox fill preserves both the frozen base and generated source PDFs")


# Duplicate a frozen scan page into a two-page input, then validate every page's
# OCR text and exact visible pixels through the packaged native worker.
scan = ROOT / "fixtures/corpus-v1/edge/scan-real-text.pdf"
scan_hash = sha(scan)
two_page_scan = OUTPUT / "two-page-scan.pdf"
scan_reader = PdfReader(str(scan), strict=True)
if len(scan_reader.pages) != 1:
    raise AssertionError("The selected OCR fixture must be exactly one page before duplication")
scan_writer = PdfWriter()
scan_writer.add_page(scan_reader.pages[0])
scan_writer.add_page(scan_reader.pages[0])
scan_writer.write(str(two_page_scan))
before = native_steps(two_page_scan, [
    ("Render", {"Page": 0, "Width": 612, "Height": 792}),
    ("Render", {"Page": 1, "Width": 612, "Height": 792}),
])
check(all(item["Success"] for item in before), "Packaged native worker renders both generated scan pages before OCR")
ocr_output = OUTPUT / "two-page-searchable.pdf"
invoke("OcrScan", two_page_scan, ocr_output,
       TrainedDataPath=extended(PACKAGE / "advanced/tessdata/eng.traineddata"),
       PdfiumPath=extended(PACKAGE / "worker/pdfium.dll"))
after = native_steps(ocr_output, [
    ("Extract", {"Page": 0}), ("Extract", {"Page": 1}),
    ("Render", {"Page": 0, "Width": 612, "Height": 792}),
    ("Render", {"Page": 1, "Width": 612, "Height": 792}),
])
check(all(item["Success"] for item in after), "Packaged native worker reopens and reads both OCR pages")
for page in (0, 1):
    text = after[1 + page]["Data"].get("Text", "")
    check(bool(text.strip()), f"OCR candidate page {page + 1} contains searchable native text")
    check(before[1 + page]["Data"]["Bgra"] == after[3 + page]["Data"]["Bgra"],
          f"OCR candidate page {page + 1} retains exact native-rendered visible pixels")
check(sha(scan) == scan_hash, "Two-page OCR smoke preserves the frozen source scan")


# Mapped text: inspect and select a nested content stream, edit via companion,
# then independently reopen/extract/render the candidate with the package worker.
nested_source = OUTPUT / "nested-text-source.pdf"
fixture_script = (
    "import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); "
    "import nested_checks; nested_checks.fixture(Path(sys.argv[2]))"
)
fixture_run = subprocess.run([str(PYTHON), "-I", "-B", "-c", fixture_script,
                              str(ROOT / "tools"), extended(nested_source)],
                             capture_output=True, text=True, timeout=90, cwd=PACKAGE / "advanced")
check(fixture_run.returncode == 0 and nested_source.is_file(),
      f"Nested text fixture generated for package smoke: {fixture_run.stderr[:300]}")
nested_hash = sha(nested_source)
inspection = invoke("InspectText", nested_source, OUTPUT / "unused.pdf", Page=0, Cursor=0)
selected = next(entry for entry in inspection["Entries"]
                if entry["Text"] == "Before" and entry["CanEdit"])
check(len(selected["FormPath"]) > 1 and sha(nested_source) == nested_hash,
      "Packaged mapped-text inspection selects a nested editable text run without changing source")
selection = {key: selected[key] for key in ("FormPath", "TextIndex", "Text")}
selection["SourceSHA256"] = inspection["SourceSHA256"]
text_output = OUTPUT / "nested-text-edited.pdf"
edited = invoke("EditText", nested_source, text_output, Page=0, Selection=selection,
                Replacement="After package edit", AllowWider=True)
check(edited.get("Applied") is True and sha(text_output) == edited.get("OutputSHA256"),
      "Packaged mapped-text companion publishes its validated nested edit")
edited_reopen = native_steps(text_output, [("Extract", {"Page": 0}),
                                            ("Render", {"Page": 0, "Width": 612, "Height": 792})])
check(all(item["Success"] for item in edited_reopen) and
      "After package edit" in edited_reopen[1]["Data"].get("Text", "") and
      len(edited_reopen[2]["Data"].get("Bgra", "")) > 0,
      "Packaged native worker reopens, extracts and renders the mapped-text candidate")
check(sha(nested_source) == nested_hash, "Mapped-text candidate preserves the generated source PDF")

report = {
    "Passed": checks,
    "Package": str(PACKAGE),
    "Scope": "Host-only exact-package smoke; not clean-machine, physical-dialog or release acceptance",
}
(OUTPUT / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(f"PASS {len(checks)} completion-package checks. Evidence: {OUTPUT}")
