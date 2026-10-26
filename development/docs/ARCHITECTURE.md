# PDF editor architecture

## Purpose and status

Implemented C#/.NET 10 proof harness with PDFium chromium/8086 and direct interop. Nested text serialization uses a pinned pypdf 6.1.3 subprocess behind the adapter; the independent regression gate is documented in NESTED-REPLACEMENT.md.

## Components

- **Harness**: drives named proof scenarios and records outcomes.
- **PDFium adapter**: the only layer declaring or invoking wrapper/native APIs. Exposes narrow operations for document/page lifetime, rendering, object enumeration and editing, annotations, page operations, save, and reopen.
- **Nested text adapter**: source-fingerprinted content operator selections; copy-on-write form ancestry; existing-font encoding and text advance preservation; temporary candidate, independent native reopen, exclusive publish and cleanup. No page flattening or masking.
- **Fixtures and result record**: controlled input PDFs plus per-scenario expected/observed behavior and artifact paths.
- **Independent viewer**: separate application used for visual inspection of saved/reopened output.

The application/worker boundary is implemented in HunterPdf.slnx. App.Desktop references App.Core only for document operations; its build also deploys App.Worker without importing its assembly. App.Worker references App.Core and App.Pdfium. Only the worker instantiates the engine and owns native handles.

## Worker protocol

Private redirected stdin/stdout carry one JSON envelope per line. Each request and response includes request ID, document ID and revision. Native access is serialized in the worker; the client serializes callers and verifies correlation and returned revision. Revisions begin at zero and increment after an edit; stale revisions fail. Extract returns revision-local object IDs, PDF-space bounds and text; Render returns bounded BGRA pixels. Native pointers never cross the boundary.

Cancellation before dispatch leaves the worker intact. Cancellation or the 30-second request timeout after dispatch kills the worker to interrupt native work and prevent orphaned responses from poisoning the next request. All sessions must then be reopened. A crash or broken pipe becomes a recoverable client error. Request payloads and exception details are not logged. Stderr is drained without retention.

This checkpoint exposes AddText as a minimal edit-command persistence proof. The complete proven editing surface, nested pypdf dependency, undo/redo, recovery and document-model identities will be integrated in their respective milestones. SaveCandidate exclusively creates a new file; ValidateCandidate closes and reopens it, loading each page. Final destination replacement is deferred to milestone 4.

## Lifetime and persistence

Milestone 5 adds OpenDocumentTab and ShellPreferences in App.Desktop. Each tab owns a DocumentSession, draft text/coordinates and zoom. Opening a known original/saved path selects its existing session. Active-tab selection synchronizes the tab strip and navigation list; only the active page bitmap is displayed. The shell disables document gestures during operations and rejects a render if the selected tab or session revision changed.

Tab close decisions are evaluated against the requested tab's session, including inactive-tab saves. Workspace close preflights all unsaved decisions before disposing any session. Saving rejects a path owned by another tab. Recovery adds multiple sessions rather than replacing the current one.

Window-scoped dynamic brush resources provide light/dark palettes. Windows high contrast uses system brushes and overrides the preference through the system property-change signal. Explicit automation names and focus styles accompany custom control templates. Theme/recent-path settings are stored beside recovery in shell-settings.json; PDF text/passwords are never stored there. Rendering remains first-page-only until Milestone 6.

Milestone 4 adds App.Core/DocumentSession and RecoveryStore. Each document session owns a worker client and an exclusive recovery lease. The source is copied into an immutable baseline; successful edit commands produce complete hashed checkpoints and atomically published manifests. Session revisions remain monotonic across undo/redo; reloaded worker sessions receive fresh document IDs and native revision zero. Saved-checkpoint identity determines dirty state. This permits later structural commands to use the same checkpoint boundary.

Save publication uses exact committed bytes, a flushed sibling candidate, native reopen/page-count and hash validation, source/destination conflict checks, then exclusive move or replacement retaining a sibling backup. Recovery manifests never contain passwords or edit text payloads. Abnormal shutdown leaves checkpoints for an explicit recovery choice. Native state lost through cancellation/crash is reconstructed from the last acknowledged checkpoint. See MILESTONE-4-COMPLETION.md for filesystem and disk-use limits.

The harness should make ownership explicit: document, page, object, annotation, and bitmap handles are released in deterministic reverse order, including error paths. Save edited output to a new path. Close the document and its dependent handles before reopening the output. Do not overwrite the fixture.

## Coordinates and object identity

Record PDF page dimensions/rotation and the geometry used by each edit. Keep coordinates in PDF page space in the test records; document any wrapper conversion at the adapter boundary. Treat object IDs as session-local unless the selected API proves otherwise. Record shared-resource and nested-object behavior explicitly.

## Evidence

For each scenario capture input fixture identity, wrapper and native binary identity, operation sequence, return/error details, saved output identity, reopen result, extraction/structural observations, and independent visual inspection. Do not log document text beyond synthetic fixture content.
