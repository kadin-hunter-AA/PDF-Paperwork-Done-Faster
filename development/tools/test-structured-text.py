"""Exercise product text adapter against generated reference cases, never fixtures/."""
import hashlib
import json
from pathlib import Path
import sys
import subprocess
import uuid

root = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(root / "App.Advanced"))
sys.path.insert(0, str(root / "tools"))
import structured_text as adapter
import nested_checks

output = root / "artifacts" / ("structured-text-" + uuid.uuid4().hex)
output.mkdir()
nested_checks.create(output)
cases = json.loads((output / "nested-cases.json").read_text())
passed = []
for case in cases:
    source = output / case["source"]
    destination = output / case["destination"]
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    request = {**case, "source": str(source), "destination": str(destination), "allowWider": True}
    try:
        adapter.edit_candidate(request)
        assert not case["reject"], case["name"]
    except adapter.UnsupportedEdit:
        assert case["reject"], case["name"]
    assert destination.exists() != case["reject"], case["name"]
    assert hashlib.sha256(source.read_bytes()).hexdigest() == before
    passed.append(case["name"])

source = output / cases[0]["source"]
inspection = adapter.inspect_text(source, 0)
assert inspection["SourceSHA256"] == hashlib.sha256(source.read_bytes()).hexdigest()
assert any(e["CanEdit"] and e["Text"] == "Before" for e in inspection["Entries"])
assert len(json.dumps(inspection, ensure_ascii=True).encode()) < 60000
passed.append("bounded nested selection inspection")
top = next(e for e in inspection["Entries"] if not e["FormPath"] and e["CanEdit"])
top_output = output / "top-level-edited.pdf"
adapter.edit_candidate({"source": str(source), "destination": str(top_output), "sourceSha256": inspection["SourceSHA256"],
    "pageIndex": 0, "formPath": [], "textIndex": top["TextIndex"], "expectedText": top["Text"], "replacement": "Mapped editing"})
assert "Mapped editing" in adapter.PdfReader(top_output).pages[0].extract_text()
passed.append("top-level text with empty form path")
assert inspection["NextCursor"] is not None
following = adapter.inspect_text(source, 0, inspection["NextCursor"])
assert following["Entries"] and following["Entries"][0] != inspection["Entries"][0]
passed.append("text-run pagination advances selection")
request = {**cases[0], "source": str(source), "destination": str(output / "width-refused.pdf")}
try:
    adapter.edit_candidate(request)
    raise AssertionError("wider replacement accepted without opt-in")
except adapter.UnsupportedEdit:
    assert not Path(request["destination"]).exists()
passed.append("wider text requires opt-in")
request["allowWider"] = True
request["destination"] = str(output / "cancelled.pdf")
try:
    adapter.edit_candidate(request, cancelled=lambda: True)
    raise AssertionError("cancelled edit published")
except adapter.UnsupportedEdit:
    assert not Path(request["destination"]).exists()
passed.append("cancel preserves source and destination")
assert not list(output.glob(".*.text.pdf"))
if len(sys.argv) > 1:
    from PIL import Image, ImageChops
    for case in cases:
        expected = output / (case["name"] + "-expected.pdf")
        if case["reject"] or not expected.exists():
            continue
        actual = output / case["destination"]
        for path in (expected, actual):
            subprocess.run([sys.argv[1], "-r", "96", "-png", str(path), str(path.with_suffix(""))], check=True, capture_output=True)
        for page in range(1, len(adapter.PdfReader(actual).pages) + 1):
            with Image.open(str(actual.with_suffix("")) + f"-{page}.png") as a, Image.open(str(expected.with_suffix("")) + f"-{page}.png") as b:
                assert ImageChops.difference(a.convert("RGB"), b.convert("RGB")).getbbox() is None, (case["name"], page)
        passed.append("exact independent Poppler pixels: " + case["name"])
report = {"Status": "PASS", "Checks": len(passed), "Passed": passed,
          "Scope": "Product Python adapter; optional independent Poppler rendering. App integration reported separately."}
(output / "results.json").write_text(json.dumps(report, indent=2))
print(str(output))
print(json.dumps(report))
