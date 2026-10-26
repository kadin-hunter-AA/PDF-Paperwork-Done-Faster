"""Independent visual/structure check of the session proof's saved PDF."""
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import nested_checks
from pypdf import PdfReader
from PIL import Image, ImageChops

work = Path(sys.argv[1]).resolve()
expected = work / "independent-expected.pdf"
nested_checks.fixture(expected, edited=True)
saved = work / "saved.pdf"
original = work / "nested.pdf"
assert len(PdfReader(saved).pages) == len(PdfReader(original).pages) == 2
for a, b in zip(PdfReader(original).pages, PdfReader(saved).pages):
    assert nested_checks.hierarchy(a) == nested_checks.hierarchy(b)
for pdf in (expected, saved):
    subprocess.run([sys.argv[2], "-r", "96", "-png", str(pdf), str(pdf.with_suffix(""))], check=True, capture_output=True)
for page in (1, 2):
    with Image.open(str(saved.with_suffix("")) + f"-{page}.png") as a, Image.open(str(expected.with_suffix("")) + f"-{page}.png") as b:
        assert ImageChops.difference(a.convert("RGB"), b.convert("RGB")).getbbox() is None, page
report = {"Status": "PASS", "SavedSessionOutput": str(saved), "Renderer": "Poppler", "ExactExpectedPagePixels": 2,
          "FormHierarchyMatricesBoundsImagesPreserved": True}
(work / "independent-saved-output.json").write_text(json.dumps(report, indent=2))
print(json.dumps(report))
