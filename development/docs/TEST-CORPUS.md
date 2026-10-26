# Completed Milestone 2 test corpus

Canonical snapshot: `development/fixtures/corpus-v1`. It contains 61 PDFs including inputs, saved outputs, intentional negative cases and setup bases. `manifest.json` records hashes, page counts, sizes, expected outcomes and provenance. `scenarios.json` defines 43 P0 feature scenarios; those are future application checks, not claims of implemented behavior. Verify the frozen snapshot with `development/run-milestone2.ps1`.

All roadmap categories are present: simple text/images; mixed fonts/sizes/colors/languages; columns/tables; subsets/ligatures/missing maps; nonwhite/overlap; shared images; nested/transparency/clipping; scans/mixed pages; rotation/page boxes; passwords; forms/signatures/bookmarks/links/attachments/layers; malformed/500-page documents; licensed public material. A full ten-annotation gallery covers all P0 markup types with retained comments and appearances.

Use scratch copies for future editing tests; retain baseline hashes. `-BuildCorpus -OutputPath <new-folder>` rebuilds the synthetic fixtures without overwriting an existing snapshot. Newly generated signatures, timestamps and font versions can change hashes: a new run is a new baseline, not byte-for-byte identical generation. Keep original manifests with benchmark results.

The following table describes original engine-proof cases now retained in the completed corpus:

| Fixture | Required property | Scenarios |
|---|---|---|
| Basic mixed page | Text and at least one image with nearby unrelated content | Open/render, enumerate, text/image edits, save/reopen |
| Long replacement | Text object where replacement exceeds original width | Replace with longer text; inspect clipping/overlap and neighbors |
| Subset font | Embedded subset lacking selected replacement characters | Replace with absent characters; record substitution/failure and persistence |
| Embedded font | Text using an embedded font | Text enumeration/replacement behavior |
| Rotated page | Non-zero page rotation | Render, geometry-aware edits, reopen |
| Shared image | Same image resource used more than once | Determine selected occurrence vs shared-resource effects |
| Reusable/nested content | Target content inside a reusable PDF object/form | Enumerate/edit behavior and unrelated-content preservation |
| Annotation/page set | Multiple pages with distinct identifiers/content | Add/modify highlight and ink; reorder and merge |

Credentials, synthetic certificate trust limits and licensing notes are recorded in the manifest and `MILESTONE-2-COMPLETION.md`. Document contents are not emitted into logs. The frozen corpus retains direct PDFium nested setters as a negative case. The newer adapter resolves general nested editing; its separate regression evidence is in NESTED-REPLACEMENT.md. The snapshot is unchanged.
