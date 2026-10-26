# Independent-viewer verification

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

The user confirmed the compatibility checkpoint after inspecting interactive, graphics and password-protected outputs. Exact run directory was not supplied for that user confirmation. Native and independent-parser evidence from the development run is in `COMPATIBILITY-RESULTS.md`.

Historical October 9 checkpoint: the user confirmed the single-text-form replacement. The new adapter supersedes that implementation.

The user subsequently confirmed the font and complex outputs from the expanded suite displayed successfully. The exact run directory was not supplied for that confirmation. This applies to the prior version where nested text remained unchanged. The later user confirmation above completed that checkpoint.

On October 9, 2026, the user ran `development/run-checks.ps1 -OpenResults` and reported that all four output PDFs matched the expected visual results.

Run directory: `development/artifacts/check-d07f1054e6504ab384aeae9072dd31c6`.

Verified by the user:

- `edited.pdf`: longer replacement text and intact unrelated text.
- `objects-original.pdf`: two red image rectangles, yellow highlight, blue ink.
- `objects-edited.pdf`: one larger green image farther right, green highlight, modified blue ink.
- `page-reordered-merged.pdf`: three pages in the expected reordered/merged sequence.

The harness also reported all automated checks passed. Viewer name/version was not recorded. This is user-reported visual evidence for these synthetic fixtures, not independent structural validation or evidence for arbitrary PDFs.

The JSON report's independent-viewer entry is a static reminder emitted before manual inspection; this document records completion for this specific run. Future runs still require visual verification.

The original checkpoint was incomplete. The subsequent font/complex runs and October 12 nested-adapter regression gate supply the remaining engine evidence, within the documented supported boundaries.

## October 12 nested adapter verification

Final run: `development/artifacts/nested-acceptance-003`. All 28 nested cases pass, including 15 deliberate rejections with checked reasons. Poppler independently rendered the original, expected, edited and subsequent PDFium-saved versions of ten two-page graphics fixtures. Edited and native-resaved output matches expected pixels exactly; untouched second pages match the originals. The assistant inspected both sharing-sample pages, the eight-level transform sample, the shared-child sample and the embedded Greek/Cyrillic output. This records tool-based independent rendering and visual inspection, not a new user confirmation. The ordinary engine suite and unchanged frozen corpus also pass.
