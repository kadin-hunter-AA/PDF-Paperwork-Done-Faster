# Font and complex-fixture proof — October 9, 2026

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

Verified run: `development/artifacts/check-175248f5f87740f58dbeb4595dc57c02`.

The expanded runner builds successfully and executes all cases. Zero exit means the suite completed, not that every PDF capability passed. Read the booleans and limitations in `proof.json`. Each newly generated report also records SHA-256 hashes of PDF fixtures.

## Supported in these fixtures

- Existing embedded Arial text: replace `ABC` with longer `CABCABCAB`; save/reopen retains the text and unrelated content.
- Real font subset generated with PDFium's subset flag: decoded embedded font size drops from 1,045,720 to 16,752 bytes. Reordering and repeating the original glyphs persists.
- Missing Greek/Cyrillic characters: direct mutation of subset text loses the new characters. Removing that actual text object and inserting a replacement object with a newly embedded font produces `ABCΩЖ` after save/reopen. This is genuine object replacement, with no cover-up rectangle. Fixture placement and size are preserved; arbitrary paragraph/style preservation is not proved.
- A 90-degree rotated page: image edit and page rotation persist, with PDF-coordinate geometry checked.
- Two pages referencing the same original image resource: replacing/moving/resizing the first page's image leaves the second page's red image intact. Reopened decoded pixels are checked on both pages.
- Nested reusable objects are enumerated through two form levels; unrelated content and form content remain present.

## Blocking limitation

Nested text edits do not persist. `FPDFText_SetText` succeeds on the nested text, and `FPDFPage_GenerateContent` succeeds on the owning page, but reopening the saved PDF returns `Nested original`. Identity transforms marking both parent forms dirty did not resolve it. See `ComplexProof.Run` for the complete minimal reproduction. The suite reports `nestedTextPersistence: false`, retaining the original content and keeping the milestone gate incomplete. Do not build an editing UI that promises nested text editing around this path.

## Reproduction and visual expectations

Run `development/run-checks.ps1 -OpenResults` from PowerShell. It generates a fresh output folder and opens five new-case PDFs:

- `font-embedded-edited.pdf` and `font-subset-edited.pdf`: `CABCABCAB` plus `Unrelated font fixture text`.
- `font-missing-reembedded.pdf`: `ABCΩЖ` plus the unrelated text.
- `complex-original.pdf`: first page rotated, with a red image and `Nested original`; second page red image.
- `complex-edited.pdf`: first page still rotated, larger green image moved right, nested text still `Nested original` (known failure); second page's image remains red.

Independent viewer checks are pending. The direct missing-glyph negative-test PDF is retained as failure evidence, not opened by the runner.

Font bytes are read from the installed Windows `arial.ttf`. They are embedded in local test outputs only; the font is not copied into source or distributed as an app asset. Do not redistribute generated font fixtures without reviewing font permissions. Runtime font SHA-256 is in the report. Tests verify this one font/subset and these synthetic shared/nested structures, not universal PDF compatibility.
