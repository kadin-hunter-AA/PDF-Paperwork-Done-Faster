# Font mapping, scanned text and signed-document checkpoint

October 12, 2026 update: the historical nested-edit limitation below is superseded by the form-preserving adapter and verified regression gate in `NESTED-REPLACEMENT.md`. Historical outputs and the frozen corpus remain unchanged.

Verified October 9, 2026, run `development/artifacts/edge-b4347d3694cf419ab682b566e88bfadc`. Reports: `native-edge-results.json`, `edge-results.json`, `signature-results.json`. PDF hashes are recorded. Final sample previews were inspected with independent Poppler rendering.

## Passing checks

- Embedded Arial document with U+FB01 ligature and accented/Greek/Cyrillic characters saves/reopens without lost visual content. Independent extraction retains the Unicode mappings. This is an explicit ligature glyph test, not automatic typography shaping.
- A separate fixture intentionally omits ToUnicode mappings. That absence survives native save; the visual render remains intact. No reliable text editing is claimed for this case.
- Rasterized readable text has no hidden/extractable text. Native save preserves appearance. It is a synthetic scan with known source text, not an OCR accuracy or real-world noise benchmark.
- Public PDFium `hello_world.pdf`, pinned to revision `e644f8cba5313005b89c16d83d453151a2c8fad5`, retains both text runs. Full upstream license/provenance are stored in `fixtures/public` and copied into run output. Its missing stream-length warning is intentional upstream test content.
- A real CMS-backed PDF signature is created with a disposable in-memory key. It validates against the self-signed certificate explicitly trusted in this isolated test. No certificate or key is installed on the user's computer.
- Mutating a covered header byte makes the document digest fail. The CMS signature can still be mathematically valid while the document is not intact; the report keeps these facts separate.
- PDFium rewriting the signed document produces a copy that no longer validates as the original signed document. The source's SHA-256 is unchanged. This demonstrates why the application must detect signatures and warn before editing a copy; it does not implement that future UI or a production signature validator.

## Limits and next coverage

Revocation is offline/not established; no trusted timestamp or public certificate trust is claimed. Signed outputs are synthetic test artifacts. General nested-form editing remains unsupported. Full P0 fixture/acceptance mapping, varied natural scanned documents and broader pathological PDFs are still pending.

The runner opens only mapped-font, readable-scan and public sample outputs. Tampered and rewritten signed copies are failure evidence, not presented as valid signed deliverables.
