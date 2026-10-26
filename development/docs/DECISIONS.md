# Decision log

## D-001 — Begin with a PDF-engine proof

**Status:** Accepted from the project roadmap.

**Decision:** Prove PDFium editing and persistence in a small harness before building the full application interface.

**Reason:** The roadmap makes save/reopen fidelity and preservation of unrelated content a prerequisite for later work.

**Consequences:** The harness must exercise actual output, close native handles, reopen the saved file, and inspect it independently. Unsupported cases must be recorded before proceeding.

## D-002 — Keep PDFium interop behind one adapter

**Status:** Accepted from the project roadmap.

**Decision:** Centralize native and wrapper calls behind a small adapter.

**Reason:** Avoid scattering interop declarations and keep a future worker boundary feasible.

## Open decisions

## D-008 — Keep shell tabs independent and preflight workspace close

Accepted October 12, 2026. A tab owns its existing document session and per-tab draft/zoom; opening another PDF does not close or replace it. Duplicate known paths select the existing session. Inactive-tab saves operate on that tab's session and reject destinations owned by another open tab. Workspace close collects all decisions before closing any session, preserving every tab if a later decision is canceled.

Use native WPF tab semantics with a custom header strip, window-scoped theme resources and system-color high-contrast overrides. Persist recent paths/theme locally. Keep the shell's navigation list distinct from Milestone 6's page thumbnails/bookmarks and full viewer behavior. See MILESTONE-5-COMPLETION.md for the 57-check shell gate and UI previews.

## D-007 — Commit full PDF checkpoints before acknowledging edits

Accepted October 12, 2026. Use immutable full-document snapshots for current undo/redo and recovery. They preserve structural page metadata without requiring every inverse command to be correct. Commit a hashed checkpoint and durable manifest before accepting an edit; discard uncertain native state on failure. Track session revisions separately from fresh worker revisions after checkpoint reload. The tradeoff is disk space proportional to document size and edit count; quota/pruning work remains future work.

Publish exact committed checkpoint bytes after candidate validation, keeping an existing destination as a backup during replacement. This avoids an extra native rewrite when saving and preserves byte-identical signed PDFs on an unedited Save As. Signed edits require explicit copy consent and never overwrite the signed source. See MILESTONE-4-COMPLETION.md for evidence and limits.

## D-006 — Isolate native operations using a serialized process protocol

Accepted October 12, 2026. Use private redirected JSON-line pipes between the asynchronous core client and worker. Every message carries request/document/revision identifiers; stale operations fail and edits advance revisions. Native handles stay in App.Pdfium inside App.Worker. Reuse the proven base adapter as linked source without changing the engine harness.

Cancellation after dispatch kills the worker; silently allowing native work to continue would leave unknown mutations and orphaned replies. All affected sessions then require reopen. The next document-state milestone must provide recovery before this becomes an end-user editing workflow. Candidate files are created exclusively and validated by reopen; replacement of existing destination files remains deferred.

## D-003 — Pin the engine and corpus tooling

PDFium Windows x64 `chromium/8086`, direct C# interop, .NET SDK 10.0.401 and pinned Python fixture dependencies are selected and tested. PDFium calls stay in the adapter/worker boundary. Worker protocol, durable document sessions, and viewer are implemented; complete editing UI, combine UI, and release packaging remain open.

## D-004 — Freeze corpus readiness separately from application readiness

Milestone 2 is complete when all required categories exist with fixture hashes and concrete P0 expected outcomes. The versioned baseline is immutable during validation; editing tests use copies. Native and independent-parser checks validate corpus structures. Forty-three future application scenarios are specified but not marked as passed. Corpus completion did not waive the engine gate. D-005 subsequently resolves the general nesting limitation with separate evidence.

Historical selection questions below are superseded by D-003 where explicitly resolved; wider engine compatibility and release licensing remain open.

- Which PDFium binary and C# wrapper/version will be pinned?
- Does the selected combination expose every required editing, annotation, page, save, and reopen operation?
- What are its redistribution/license and update obligations?
- Which independent viewer and fixture-generation method will be used?

Do not treat these open items as resolved by this log.

## D-005 - Preserve nested forms with a structural editing adapter

Accepted October 12, 2026. Keep PDFium chromium/8086 for the existing native capabilities and use pinned pypdf 6.1.3 for occurrence-based nested text serialization. Copy only the selected form ancestry, preserve drawing operations and dictionaries, encode through existing font maps, and preserve following text positions. Verify candidates through both pypdf and PDFium before exclusive publication.

This replaces the fixture-specific flattening workaround without a custom PDFium binary or unsafe native internals. Python/pypdf are now prototype runtime dependencies for nested editing; packaging them and carrying their notices is required for a future release. Poppler stays a test-only dependency. Unsupported font, tagged, signed and encrypted workflows fail explicitly. See `NESTED-REPLACEMENT.md` for the successful regression gate and boundaries.

## D-009 — Publish combined PDFs only after worker-side validation

Accepted October 12, 2026. Keep combine operations stateless and execute PDFium only in the worker. Preflight every ordered input and range, reject encrypted/signed sources and destination collisions, hash inputs before and after processing, write to a unique sibling candidate, reopen it to validate the page count/pages, then publish with an exclusive no-overwrite move. Limit outputs to 50,000 selected pages. Cancellation terminates the worker; an already published output may remain if cancellation races finalization; a worker killed during native writing can leave a uniquely named hidden candidate. The feature does not preserve bookmarks, forms, attachments, signatures, or source document metadata. See `App.Tests/CombineProof.cs` and current `BUILD-STATUS.md`; the GUI is integrated with the verified subset.

## D-010 — Restrict initial existing-text replacement to verifiable top-level text

Accepted October 12, 2026. Expose text/font details with worker-revision-bound object IDs. Replace the actual text object through its existing standard PDF base font; reject non-ASCII text, nonstandard fonts, transformed/clipped objects, unsupported or stale selections, overflow, and overlap. Commit only through the existing immutable save/reopen session checkpoint. On failure, discard the uncertain checkpoint and reopen the prior committed document. This is a narrow prototype capability and does not claim nested, tagged/ActualText, OCR, arbitrary font, reflow, or redaction support. See `App.Tests/TextReplacementProof.cs` and current `BUILD-STATUS.md`.

