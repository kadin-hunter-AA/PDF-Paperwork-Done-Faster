# First engine checkpoint — October 9, 2026

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

Command: run `EngineProof` with output `development/artifacts/run-001`.
Build and execution exited successfully.

The harness created a two-text-object fixture, saved it, reopened it, and replaced the existing first text object's content with a longer string. It regenerated page content and saved a separate edited file. After all editing handles were closed, it reopened that output, verified the new string, verified removal of the old string, and verified the unrelated second text. It rendered a 612 × 792 RGB PPM.

No private documents were used. Original and edited documents are separate files; existing output PDFs are never overwritten. Handle cleanup uses deterministic disposal/finally. This proves a simple Helvetica text-object edit only, without paragraph reflow or font-subset claims.

The complete milestone gate is **not passed**. Independent-viewer inspection, embedded/subset fonts, absent glyphs, image edits, annotations, page organization, rotated/shared/nested content remain pending. No desktop UI, recovery system, installer or final worker protocol exists yet.

## Expanded checkpoint

`development/artifacts/expanded-001` passed on October 9, 2026. The image fixture was saved and reopened before mutation: one existing image became green, moved and resized; the second was removed. Reopened output assertions checked object count, image bounds and decoded replacement pixels. Highlight quadrilateral/color and replaced ink coordinates persisted. Page import reordered a saved two-page source and appended a separately saved one-page source; all three reopened pages contained their expected labels.

Independent visual checks for these new cases remain pending. Font and complex-document checks remain unimplemented. Earlier pending entries above describe the first checkpoint, not the expanded result.
