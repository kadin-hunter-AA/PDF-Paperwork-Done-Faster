"""Independent proof harness against the actual app redaction helper."""
from pathlib import Path
import json
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "redaction-pymupdf"))
sys.path.insert(0, str(ROOT / "tools" / "python-packages"))
sys.path.insert(0, str(ROOT / "App.Advanced"))
sys.path.insert(0, str(ROOT / "RedactionProof"))
from redaction import RedactionRefused, _preflight, redact_candidate
import proof
from pypdf import PdfReader, PdfWriter
from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject

def apply(source, destination, regions, *, cancellation_check=None, before_publish=None):
    selections = [{"Page": page, "Left": rect.x0, "Top": rect.y0, "Right": rect.x1, "Bottom": rect.y1}
                  for page, rects in regions.items() for rect in rects]
    return redact_candidate(source, destination, selections,
        cancelled=cancellation_check or (lambda: False),
        before_publish=(lambda: before_publish(source, destination)) if before_publish else (lambda: None))

proof.apply_fresh = apply
proof.preflight = _preflight
proof.RefusedInput = RedactionRefused
output = ROOT / "artifacts" / ("redaction-app-helper-" + uuid.uuid4().hex)
output.mkdir()
checks = []
accepted = proof.exercise_accepted_subset(output, checks)
refused = proof.exercise_refusals(output, checks)
metadata = proof.exercise_metadata_default(output, checks)
safety = proof.exercise_publication_safety(output, checks)

def refused_case(path, description):
    try:
        _preflight(path)
    except RedactionRefused:
        checks.append(description)
        return
    raise AssertionError(description)

rotated = output / "rotated-cropped.pdf"
proof.make_rotated_crop_source(rotated)
refused_case(rotated, "App helper refuses rotated/cropped pages until coordinate handling is integrated")
source = Path(accepted["source"])
writer = PdfWriter(clone_from=PdfReader(source))
content = writer.pages[0].get_contents().get_data()
stream = DecodedStreamObject()
stream.set_data(b"/Span << /ActualText (HIDDEN_MARKED_CONTENT) >> BDC\n" + content + b"\nEMC")
writer.pages[0][NameObject("/Contents")] = writer._add_object(stream)
marked = output / "marked-content.pdf"
with marked.open("wb") as target: writer.write(target)
refused_case(marked, "App helper refuses marked content and ActualText")
writer = PdfWriter(clone_from=PdfReader(source))
fonts = writer.pages[0]["/Resources"]["/Font"]
next(iter(fonts.values())).get_object()[NameObject("/FontDescriptor")] = DictionaryObject()
font = output / "custom-font.pdf"
with font.open("wb") as target: writer.write(target)
refused_case(font, "App helper refuses custom or embedded font descriptors")

report = {"Passed": checks, "Accepted": accepted, "Refused": refused, "Metadata": metadata, "Safety": safety}
(output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(f"PASS {len(checks)} app redaction helper checks. Evidence: {output}")
