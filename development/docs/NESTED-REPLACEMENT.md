# Nested form text editing

Updated October 12, 2026. The single-text-form flattening workaround has been replaced by a general occurrence-based adapter. It edits the selected text inside its Form XObject and keeps the form hierarchy. It does not add a mask or flatten the page.

## Cause and solution

PDFium chromium/8086 still loses direct nested `FPDFText_SetText` changes on save. Its `CPDF_PageContentGenerator::ProcessForm` only regenerates a form when `HasDirtyStreams()` is set; ordinary text mutation does not set that holder state. Marking ancestor page objects dirty is insufficient. `complex-direct-edit.pdf` remains the negative reproduction.

`Pdf.ReplaceNestedText` in `EngineProof/NestedTextEditor.cs` uses the pinned pypdf 6.1.3 helper `nested_text.py` for structural serialization and PDFium for independent reopen/extraction. The helper copies only the selected invocation's form ancestry and its resource dictionaries, assigns fresh collision-free XObject names, and changes only the selected `Do` at each level. Other instances on this page, inside other forms, or on other pages retain their original resources and streams.

The selected text-show operation becomes a `TJ` operation with font-encoded replacement bytes. A trailing advance adjustment keeps following text at its original location. Leading positioning in an existing `TJ` is retained. Quote operators retain their line and spacing effects. All other content operations, form matrices, bounding boxes, clipping, transparency groups, images and drawing order are preserved.

Selections contain the input SHA-256, zero-based page index, local `Do` occurrence path, local text-show occurrence index, and expected original text. `Do` indices include images; a selected image is rejected. Text indices count `Tj`, `TJ`, single-quote and double-quote operations. These are content operator addresses, not PDFium object indices. Refresh selections after any edit.

## Evidence

Run `./development/run-nested-checks.ps1`. It runs the existing engine proof, 28 nested cases (13 successful edits and 15 expected rejections), overwrite/cleanup checks, native save/reopen, and independent Poppler comparisons. The ten graphics cases compare every edited page and subsequent PDFium save against independently authored expected PDFs with exact pixel equality. Untouched second pages also match the source pixels exactly. Structure checks retain form depth, matrices, bounds, groups, images, page rotation and crop boxes.

Coverage includes eight form levels, multiple sibling objects, repeated uses of the same resource name, same-page and cross-page sharing, shared child forms inside another form, shared page resources, inherited font/resources/color, clipping, transparency, rotation, all four text-show operators, character/word spacing, genuine embedded/subset CID fonts, and Greek/Cyrillic replacement in a full embedded font.

Evidence: `development/artifacts/nested-acceptance-003/milestone1.json`, `native-nested.json`, `independent-nested.json`, and `engine/proof.json`. Poppler previews were also inspected visually. Earlier failed development runs are retained separately; the numbered acceptance run is the final evidence.

## Supported boundaries

- Form nesting is bounded at 64 levels; cyclic or stale paths fail. There is no single-child restriction.
- Horizontal Type1/TrueType fonts need a known single-byte encoding and reliable widths. Standard base fonts and explicit width tables are supported. CID fonts require Identity-H, a two-byte ToUnicode map and horizontal metrics. Present subset glyphs are editable; absent or ambiguous glyphs are rejected. Nested font substitution/re-embedding and shaping/reflow remain separate work.
- Explicit path clipping and form bounding-box clipping are preserved. Editing text that itself establishes a clipping path is rejected because it changes the visibility of unrelated content.
- Encrypted, signed, tagged-structure and enclosing ActualText cases are rejected before publication. They require their respective preservation workflows. Unsupported encodings, Type3/vertical fonts and unreliable metrics also fail explicitly.
- The source and any existing destination are never overwritten. The candidate is verified with both parsers before an exclusive move publishes it; temporary files are removed on success and failure. The helper has a 60-second process timeout.
- This is object editing, not secure redaction; unused original resources may remain in the rewritten file.

## Dependencies and reproduction

Python and pypdf 6.1.3 are now dependencies of this prototype editing path, not just fixture tooling. Build copies the pinned pypdf Python modules and license beside the harness. Install the existing `tools/compatibility-requirements.txt` if missing. `PDF_EDITOR_PYTHON` can select the interpreter. The runner needs Poppler on PATH or the bundled Codex runtime; use `-PopplerPath` elsewhere. Poppler is a test dependency only.

`./development/run-nested-checks.ps1 -OpenResults` opens the two-page sharing sample after all checks pass. Only the first instance on page one changes from Before to After edit. All other instances, leaf siblings, images and translucent/clipped artwork remain unchanged. `complex-edited.pdf` now also retains its two-level form hierarchy.
