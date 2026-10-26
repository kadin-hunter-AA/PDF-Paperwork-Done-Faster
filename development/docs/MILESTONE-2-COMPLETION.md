# Milestone 2 completed — October 9, 2026

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

**Acceptance gate met:** each P0 feature has a realistic fixture and defined expected results. The canonical corpus is `development/fixtures/corpus-v1`.

## Deliverables and evidence

- 61 PDF files: inputs, paired saved outputs, setup bases and deliberate negative cases. They are not 61 independently sourced documents.
- 14 categorized fixture groups cover every item in roadmap section 6, including mixed-language/style content, every P0 annotation type, image sharing, nested/rotated/clipped content, subset/ligature/missing mappings, actual rasterized readable scans, mixed pages, encrypted and signed files, interactive structures, malformed input and 500 pages.
- 43 scenarios cover every P0 View, Edit, Markup and Reliability feature from the feature table. Each includes existing fixture paths, steps and concrete expected results. Undo/recovery/printing/installer checks describe the future app or clean-VM workflow, not passing behavior in this prototype.
- `manifest.json` records SHA-256, size, page count, role and expected behavior for every PDF, plus hashes of supporting reports, source scan images, certificates, notices and scenario definitions. Category coverage and all 98 scenario references were checked.
- Native PDFium save/reopen and render checks passed. Independent pypdf checks passed for field/widget values and appearances, annotations/comments, bookmarks, links, attachments, layers, page boxes, mappings and protected document persistence.
- pyHanko checks passed for intact test-trusted signature, covered-byte tampering rejection, native rewrite invalidation and unchanged signed source. Test key was held only in memory; no certificate or key was installed. Revocation and public trust are not established; no trusted timestamp exists.
- An altered scratch copy was rejected by the integrity gate while the original baseline still passed. Evidence: `development/artifacts/corpus-integrity-results.json`.
- User verified the preceding checkpoints. Final mixed-language/styles and two-page annotation layouts were inspected with independent Poppler rendering. Readable text-box appearance, shape outlines and all markup styles are present.

## Reproduction

From the project root:

```powershell
& .\development\run-milestone2.ps1
```

To inspect final sample fixtures, add `-OpenResults`. Validation reads the baseline without rebuilding or overwriting it. Rebuild into a new folder with `-BuildCorpus -OutputPath <new-folder>`; expect a new hash baseline because signatures/certificates/timestamps and installed font versions may differ. Build instructions and pinned dependency lists are in the README.

## Provenance, logs and boundaries

The public PDFium fixture is pinned to source revision `e644f8cba5313005b89c16d83d453151a2c8fad5`; full upstream license, source URL and hash are retained. Other fixture content is synthetic. Local Windows fonts are embedded in some tests: do not assume those generated fixtures can be redistributed without reviewing font terms. Password fixture user credential is `corpus-user`, owner credential `corpus-owner`. Document contents are kept out of logs; reports contain metadata, hashes and outcomes.

At the October 9 corpus freeze, Milestone 1 remained open on general nested editing. The October 12 adapter and independent regression suite resolve that blocker without changing this snapshot. PDFium's prototype page-only rendering also omits widget text; independent rendering confirms retained form appearances. These issues do not make the corpus incomplete and are explicitly retained as future acceptance cases.

No application, installer, recovery system or P0 release is claimed. Milestones 3–12 and P1/P2 have not begun. Work stops here as requested, awaiting user directions.
