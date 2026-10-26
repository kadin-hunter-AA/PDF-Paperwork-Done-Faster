# Existing AcroForm fill proof

This is a standalone development proof. It uses the repository's pinned pypdf 6.1.3 package, the supplied Python runtime, the already built PDF worker for PDFium form rendering, and Poppler for independent rendering. It does not add a product command or runtime dependency.

Run from the workspace root after building `development/App.Worker`:

```powershell
$env:PYTHONPATH = (Resolve-Path .\development\tools\advanced-python-3.14.8\packages).Path + [IO.Path]::PathSeparator + (Resolve-Path .\development\tools\python-packages).Path
& .\development\tools\advanced-python-3.14.8\python.exe .\development\FormFillProof\proof.py
```

The harness reads the frozen interactive form fixture and writes all generated inputs, candidates, renders, and `results.json` under a unique `development/artifacts/form-fill-proof-<guid>/` directory. `candidate.py` provides the reusable guarded candidate function; `proof.py` creates fixtures and drives acceptance checks. The helper refuses encrypted files, populated signatures, document/page/field/widget actions, calculation actions, XFA, read-only/password/rich text fields, field-name ambiguity, unsupported glyphs, MaxLen overflow, and values that could overflow the widget. It builds a unique sibling candidate, reopens and verifies target and unrelated field values, verifies the regenerated appearance and page count, detects source hash drift, honors cancellation before publication, and uses a create-only same-directory rename. The harness independently renders the published candidate through PDFium and Poppler, and checks exact text in the appearance stream. Frozen fixture hashes are checked at the end.

This proves a narrow pypdf implementation path for an inherited plain text field with a WinAnsi/PDFDocEncoding-compatible Unicode value. It does not establish support for arbitrary Unicode glyph coverage, JavaScript validation/calculation, choice fields, button states, rotated widgets, malformed appearance streams, PDF encryption, signed-document modification, or production session integration. In particular, pypdf's update call must remain behind explicit field-type/flag/action checks; the library call itself is not the product's safety policy.
