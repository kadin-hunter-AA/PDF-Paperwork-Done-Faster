"""Host-only smoke of the exact private runtime and native worker in a package."""
from pathlib import Path
import hashlib
import json
import os
import secrets
import subprocess
import sys
import uuid

sys.dont_write_bytecode = True

ROOT, PACKAGE, OUTPUT = map(lambda p: Path(p).resolve(), sys.argv[1:4])
OUTPUT.mkdir(parents=True, exist_ok=False)
PYTHON = PACKAGE / "advanced/python.exe"
HELPER = PACKAGE / "advanced/worker.py"
WORKER = PACKAGE / "worker/App.Worker.exe"
checks = []

def check(ok, message):
    if not ok: raise AssertionError(message)
    checks.append(message)

def sha(path):
    with open(path, "rb") as stream: return hashlib.file_digest(stream, "sha256").hexdigest()

def extended(path):
    value = str(Path(path).absolute())
    if os.name != "nt" or value.startswith("\\\\?\\"): return value
    return "\\\\?\\UNC\\" + value[2:] if value.startswith("\\\\") else "\\\\?\\" + value

def invoke(operation, source, destination, **kwargs):
    request = {"Operation": operation, "Input": extended(source), "Output": extended(destination), **kwargs}
    run = subprocess.run([str(PYTHON), "-I", "-B", extended(HELPER)], input=json.dumps(request) + "\n",
        capture_output=True, text=True, timeout=180, cwd=HELPER.parent)
    response = json.loads(run.stdout)
    check(run.returncode == 0 and response.get("Success") is True, f"Packaged companion completes {operation}")
    check(not run.stderr, f"Packaged {operation} writes no diagnostic content to stderr")
    return response["Data"]

def native(source, operation, arguments):
    document = str(uuid.uuid4())
    requests = [{"RequestId": str(uuid.uuid4()), "DocumentId": document, "Revision": 0,
        "Operation": name, "Arguments": args} for name, args in
        [("Open", {"Path": str(source)}), (operation, arguments), ("Close", {})]]
    run = subprocess.run([str(WORKER)], input="\n".join(json.dumps(r) for r in requests) + "\n",
        capture_output=True, text=True, timeout=60, cwd=WORKER.parent)
    responses = [json.loads(line) for line in run.stdout.splitlines()]
    check(run.returncode == 0 and len(responses) == 3 and all(r["Success"] for r in responses),
        f"Packaged native worker reopens {source.name} and executes {operation}")
    return responses[1]["Data"]

inventory = json.loads((PACKAGE / "advanced/runtime-inventory.json").read_text(encoding="utf-8-sig"))
for item in inventory["Files"]:
    check(sha(PACKAGE / "advanced" / item["Path"]) == item["SHA256"], "Runtime inventory: " + item["Path"])
for name in ("forms.py", "ocr.py", "signing.py", "redaction.py", "worker.py"):
    check(sha(PACKAGE / "advanced" / name) == sha(ROOT / "App.Advanced" / name), "Packaged helper matches source: " + name)
if "--with-structured-text" in sys.argv[4:]:
    check(sha(PACKAGE / "advanced/structured_text.py") == sha(ROOT / "App.Advanced/structured_text.py"), "Packaged mapped-text helper matches source")
    font_source = ROOT / "fixtures/corpus-v1/engine/font-embedded-original.pdf"
    font_hash = sha(font_source)
    inspection = invoke("InspectText", font_source, OUTPUT / "unused.pdf", Page=0, Cursor=0)
    selected = next(e for e in inspection["Entries"] if e["Text"] == "ABC" and e["CanEdit"])
    text_output = OUTPUT / "mapped-text.pdf"
    result = invoke("EditText", font_source, text_output, Page=0,
        Selection={**selected, "SourceSHA256": inspection["SourceSHA256"]}, Replacement="ABCΩЖ", AllowWider=True)
    check(result["Applied"] and sha(text_output) == result["OutputSHA256"], "Packaged text editor publishes validated bytes")
    check("ABCΩЖ" in native(text_output, "Extract", {"Page": 0})["Text"], "Packaged native worker reads mapped Greek/Cyrillic replacement")
    check(sha(font_source) == font_hash, "Packaged text editing preserves the frozen font source")
check(sha(PACKAGE / "worker/pdfium.dll") == sha(ROOT / "native/bin/pdfium.dll"), "Packaged PDFium matches the pinned native library")

form_source = ROOT / "fixtures/corpus-v1/compatibility/interactive-features.pdf"
form_hash = sha(form_source)
filled = OUTPUT / "filled.pdf"
invoke("FillForm", form_source, filled, FieldName="sample_name", Value="café – déjà vu")
fields = native(filled, "InspectFormFields", {"Page": 0})["Fields"]
check(next(f["Value"] for f in fields if f["Name"] == "sample_name") == "café – déjà vu", "Packaged PDFium reads the exact filled Unicode value")
check(sha(form_source) == form_hash, "Packaged filling preserves the frozen form source")

scan = ROOT / "fixtures/corpus-v1/edge/scan-real-text.pdf"
scan_hash = sha(scan)
searchable = OUTPUT / "searchable.pdf"
invoke("OcrScan", scan, searchable, TrainedDataPath=extended(PACKAGE / "advanced/tessdata/eng.traineddata"),
       PdfiumPath=extended(PACKAGE / "worker/pdfium.dll"))
check("Invoice number 12345" in native(searchable, "Extract", {"Page": 0})["Text"], "Packaged OCR output is searchable with native PDFium")
check(sha(scan) == scan_hash, "Packaged OCR preserves the frozen scan")

sys.path.insert(0, str(ROOT / "SigningProof"))
from test_pfx_transport import make_pfx, independently_verify
source = ROOT / "fixtures/corpus-v1/engine/original.pdf"
source_hash = sha(source)
secret = secrets.token_urlsafe(32)
pfx = OUTPUT / "disposable.pfx"
try:
    certificate = make_pfx(pfx, secret)
    signed = OUTPUT / "signed.pdf"
    result = invoke("SignPdf", source, signed, CertificatePath=extended(pfx), CertificatePassword=secret)
    check(result["Intact"] and result["CryptographicallyValid"] and not result["CertificateTrustEstablished"], "Packaged signing reports integrity without trust")
    independently_verify(signed, certificate)
    check(True, "Independent CMS/RSA verification accepts packaged signing output")
    native(signed, "Extract", {"Page": 0})
finally:
    pfx.unlink(missing_ok=True); secret = ""
check(sha(source) == source_hash, "Packaged signing preserves the frozen source")

sys.path.insert(0, str(ROOT / "RedactionProof"))
import proof
redaction_source = OUTPUT / "redaction-source.pdf"
proof.make_primary_source(redaction_source)
redaction_hash = sha(redaction_source)
redacted = OUTPUT / "redacted.pdf"
regions = [{"Page": 0, "Left": 65, "Top": 95, "Right": 350, "Bottom": 130},
           {"Page": 0, "Left": 65, "Top": 200, "Right": 400, "Bottom": 230}]
invoke("Redact", redaction_source, redacted, Regions=regions)
text = native(redacted, "Extract", {"Page": 0})["Text"]
check(proof.SECRET_TEXT not in text and proof.SECRET_HIDDEN not in text and proof.PUBLIC_AFTER in text, "Packaged redaction removes visible/hidden text and retains unrelated text")
reader, objects = proof.all_object_streams(redacted)
check(all(marker.encode() not in b"\n".join(objects) for marker in (proof.SECRET_TEXT, proof.SECRET_HIDDEN)), "Independent raw-object/stream inspection finds neither redacted marker")
check(not reader.metadata and sha(redaction_source) == redaction_hash, "Packaged redaction strips standard metadata and preserves source bytes")

if "--with-annotation-management" in sys.argv[4:]:
    document = str(uuid.uuid4())
    # Resolve the appended annotation's index from the source before exercising
    # the exact packaged mutation protocol.
    from pypdf import PdfReader
    annotation_index = len(PdfReader(source).pages[0].get("/Annots", []))
    before = OUTPUT / "annotation-before-render.pdf"
    after = OUTPUT / "annotation-after-render.pdf"
    removed = OUTPUT / "annotation-deleted.pdf"
    new_contents = "Packaged updated note — café\nSecond line."
    reference = lambda revision: f"{document.replace('-', '')}:{revision}:0:annotation:{annotation_index}"
    operations = [(0, "Open", {"Path": str(source)}),
        (0, "AddComment", {"Page": 0, "Contents": "Packaged original note", "Left": 220, "Bottom": 650, "Right": 244, "Top": 674}),
        (1, "SaveCandidate", {"Path": str(before)}),
        (1, "Render", {"Page": 0, "Width": 612, "Height": 792}),
        (1, "RenderForPrint", {"Page": 0, "Width": 612, "Height": 792}),
        (1, "InspectFormFields", {"Page": 0}),
        (1, "UpdateComment", {"Page": 0, "AnnotationId": reference(1), "Contents": new_contents}),
        (2, "SaveCandidate", {"Path": str(after)}),
        (2, "DeleteAnnotation", {"Page": 0, "AnnotationId": reference(2)}),
        (3, "SaveCandidate", {"Path": str(removed)}), (3, "Close", {})]
    requests = [{"RequestId": str(uuid.uuid4()), "DocumentId": document, "Revision": revision,
                 "Operation": operation, "Arguments": arguments} for revision, operation, arguments in operations]
    run = subprocess.run([str(WORKER)], input="\n".join(json.dumps(request) for request in requests) + "\n",
        capture_output=True, text=True, timeout=120, cwd=WORKER.parent)
    responses = [json.loads(line) for line in run.stdout.splitlines()]
    check(run.returncode == 0 and len(responses) == len(requests) and all(item["Success"] for item in responses),
          "Packaged worker completes comment creation, viewer/print reads, update and deletion")
    before_note = PdfReader(before).pages[0]["/Annots"][annotation_index].get_object()
    after_note = PdfReader(after).pages[0]["/Annots"][annotation_index].get_object()
    check(list(before_note["/Rect"]) == list(after_note["/Rect"]) == [220, 650, 244, 674],
          "Independent parser confirms packaged viewer/print rendering never normalizes later-saved note rectangles")
    check(after_note["/Contents"] == new_contents and len(PdfReader(removed).pages[0].get("/Annots", [])) == annotation_index,
          "Packaged update retains exact Unicode contents and deletion removes only the added note")
    check(sha(source) == source_hash, "Packaged annotation management preserves the frozen source")

report = {"Passed": checks, "Package": str(PACKAGE), "RuntimeFileCount": inventory["FileCount"],
          "AnnotationManagementChecked": "--with-annotation-management" in sys.argv[4:],
          "Scope": "Host-only package smoke; not clean-machine, physical-dialog or release acceptance"}
(OUTPUT / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(f"PASS {len(checks)} package inventory/advanced smoke checks. Evidence: {OUTPUT}")
