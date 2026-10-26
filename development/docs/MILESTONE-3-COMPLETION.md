# Milestone 3 — solution and isolated worker checkpoint

Implemented October 12, 2026. The five-project solution builds with zero warnings and errors using the existing local SDK and pinned PDFium binary.

Run `./development/run-worker-checks.ps1` from the project root.
Latest evidence: `artifacts/worker-9b3507407c4c4ad5854df746db8384cd/results.json` (17 passing checks).

## Delivered

- App.Desktop: WPF checkpoint UI opens a PDF and displays its first page through the worker; worker errors leave Open available.
- App.Core: typed request/response envelopes and an asynchronous, serialized worker client.
- App.Pdfium: owns document/page/bitmap handles; reuses the proven base adapter through a linked source file.
- App.Worker: owns PDFium in a separate process and executes requests serially.
- App.Tests: executable integration gate, including a deterministic stalled peer for active cancellation.

Operations: Open, Close, Render, Extract, AddText, SaveCandidate, ValidateCandidate. The edit command is a boundary/persistence proof; the finished text editor, font substitution, signed-document workflow and other editing tools belong to later milestones.

## Verification

Tests cover serializable object geometry, stable IDs within a revision, concurrent request serialization, multiple document sessions, edit isolation, bounded rendering, oversized-render rejection, revision increments, stale-request rejection, exclusive candidate creation, save/reopen text persistence, original-file protection, forced worker termination, client restart/reopen, queued cancellation, active cancellation during a stalled response, and desktop worker/native deployment.

The integration client remains alive after the worker is killed and successfully opens another document. This establishes the automated worker-failure acceptance gate. The WPF executable builds; manual interaction and UI crash-recovery smoke testing remain pending and are not claimed by the integration gate.

## Limits and next milestone

Cancellation or timeout after dispatch terminates the worker, discarding every in-memory session. Reopen is explicit; edits are not recovered yet. Milestone 4 must supply command history, recovery snapshots, external-change detection and validated destination replacement.

Object IDs are document/revision/page/object-index values, stable only within the current revision. Cross-edit page/object identity belongs to the future document model. Render bytes use bounded BGRA buffers; widget rendering, page navigation and caching belong to the viewer milestone. Candidate validation only checks native reopen and page loading; it does not validate signatures, all PDF structures or appearance fidelity.

No installer, clean-machine verification or P0 release acceptance is claimed.
