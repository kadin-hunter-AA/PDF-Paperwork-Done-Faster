# Corpus groundwork checkpoint

Run `development/run-corpus.ps1 -OpenResults`. It compiles and runs real PDF creation, reopen, extraction and selected rendering checks, writes a fresh folder, and opens three samples. It never changes reference files under `sources/` or overwrites prior runs.

Verified October 9, 2026: 19 fixtures recorded with SHA-256 hashes and expected results in `corpus.json`. The generated 500-page fixture was reopened, pages 1, 250 and 500 were text-checked, and only the last page was rendered. The initial run measured 1,361 ms to generate/save and 56 ms for reopen/random-access/render; working set was 47,013,888 bytes. These are single-run harness observations, not app startup/scrolling benchmarks or bounded-cache evidence.

Viewer expectations:

- `layout-columns-table.pdf`: lilac background, two columns of eight lines each, a 4-row × 3-column labeled table, and intentional rectangle overlap near the bottom.
- `mixed-digital-scan.pdf`: the same layout on page 1; page 2 contains rasterized black bars with no selectable text. This is a scan-like classifier fixture, not an OCR accuracy sample.
- `large-500-pages.pdf`: 500 pages, each labeled `Corpus page NNN of 500`; check first, page 250 and last.

The malformed fixture must fail to open through the adapter. It is not opened in the viewer. The engine-cases subfolder retains direct-edit negative tests as evidence; those failures must not be mistaken for supported behavior.

Remaining coverage is listed in `corpus.json`. The complete milestone gate is not passed. This is synthetic corpus groundwork while supported engine boundaries are documented; no UI/worker/release milestone is claimed.
