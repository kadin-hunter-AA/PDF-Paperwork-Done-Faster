# Compatibility corpus checkpoint — October 9, 2026

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

Successful run: `development/artifacts/compatibility-6a30bd37363f4857a227d45f8cb087b9`.

Three synthetic fixtures were generated using ReportLab and pypdf, loaded/saved/reopened by the pinned PDFium adapter, and independently inspected with pypdf after saving. See `native-compatibility.json` and `independent-compatibility.json` for checks and fixture hashes.

## Verified behavior

- AES-256 file rejects missing and incorrect passwords. The correct password unlocks text. PDFium output remains encrypted; pypdf verifies the incorrect password still fails and the correct password still works.
- Canonical AcroForm text value is `Corpus value`, and the page widget agrees. Its normal appearance stream remains nonempty.
- An unsigned signature field remains present with no signature value. This does not test signing, certificate trust or signature validity.
- A bookmark, internal link, exact attachment bytes and optional layer remain present after native save.
- Crop, trim and bleed boxes survive. Transparency state and clipping operators remain present.
- PDFium page-content rendering is byte-identical before and after saving the graphics and interactive fixtures. This does not include widget text: page-only prototype rendering omits it. Poppler independently displays the retained field value correctly.
- Native previews and an independent Poppler form preview were visually inspected. Main graphics remain readable; the gold shape is deliberately clipped and the mint/plum overlap is intentionally translucent.

## User inspection

Run `development/run-compatibility.ps1 -OpenResults`. In the interactive fixture, look for `Corpus value` in the field and the mint layer rectangle. In the graphics fixture, confirm the translucent overlap and framed gold rectangle. Use `corpus-user` to open the protected file. Opening alone does not change field contents.

Remaining corpus gaps: certificate-signed files, ligatures and missing Unicode mappings, real OCR samples, licensed public fixtures and complete P0 acceptance coverage. Milestone 1's general nested edit issue remains unresolved. No application UI milestone is claimed.
