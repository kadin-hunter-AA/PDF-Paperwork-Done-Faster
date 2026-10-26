# Milestone 4 — document state, safe saving and recovery

Implemented October 12, 2026. The solution builds with zero warnings/errors. Run `./development/run-milestone4.ps1` from the project root to reproduce the worker, document-state and WPF command gates.

## Delivered behavior

DocumentSession owns the original path, source/destination fingerprints, monotonically increasing session revision, saved checkpoint and dirty state. It copies the source into local recovery storage before the worker opens it; native handles never keep the original destination locked.

Each accepted edit saves a complete immutable PDF checkpoint, closes the candidate writer, reopens and loads its pages, records its hash, and commits the recovery manifest before reporting success. Undo/redo restores these complete checkpoints. Tests cover actual added text and page rotation. A new edit after undo abandons the redo branch. Failed edit or history commits discard uncertain native state and return to the last committed checkpoint.

Save As is the desktop default for the first save. Saving copies the exact committed checkpoint into a temporary file beside the destination, flushes it, checks native reopen/page count and its hash, then publishes it. Existing files require explicit overwrite consent; replacement keeps the previous PDF in a uniquely named sibling backup. Original and last-saved fingerprints detect outside changes, and the destination is rechecked immediately before publication. Failed validation, locked/read-only files, unavailable folders and worker failures preserve the existing PDF and the unsaved checkpoint. A successful save followed by recovery-bookkeeping failure is reported distinctly as a completed save.

Recovery records preserve checkpoint history, current selection, saved state and file fingerprints. Startup discovers abandoned sessions through an exclusive session lease and prompts to recover. Declining retains the files for later; clean close or explicit discard removes only that session directory. Corrupt hashes, malformed metadata and escaping checkpoint paths are rejected while retaining files. Recovery works even if the original file has disappeared. PDF passwords are requested again and are never written to recovery metadata.

Signed PDFs are detected using the pinned signature API. Editing requires explicit signed-copy consent in the UI and worker. Edited copies are labeled as having invalidated existing signatures; the signed original cannot be overwritten. Detection does not validate cryptographic signatures.

The desktop now has Save/Save As, Undo/Redo, a dirty title indicator, unsaved-close prompts, recovery prompts, password entry, basic Latin text addition and first-page rotation. These basic edit commands exercise document-state behavior; complete text/image/page tools remain later milestones.

## Evidence and verification

- 17 existing worker checks.
- 70 document-state/save/recovery checks: actual persisted edits, undo/redo, branch behavior, destination/original protection, backups, external changes, candidate corruption before/after validation, publication failure, worker termination, file locks, read-only destinations, unavailable folders, failed checkpoint commits, distinct completed-save bookkeeping failure, abrupt process exit, encrypted recovery, missing-source recovery and invalid metadata.
- 40 WPF control/command checks exercise actual worker-rendered PDFs, injected native-dialog choices, save/close cancellation, overwrite decline/acceptance, external-change errors, worker restart, recovery decline/acceptance and signed-copy decisions.
- The frozen 61-PDF, 43-scenario corpus still passes its integrity gate.

Core evidence: `artifacts/worker-69dee40d06944d5099e204cbeecc1e43/results.json` and `session-results.json`. The abrupt-exit subprocess deliberately bypasses Dispose and CloseAsync; recovery then restores its text and history.

WPF evidence: `artifacts/ui-state-69f0932b61064eeeaf4c5b362c94f5fc/ui-results.json` and `desktop-checkpoint.png`. The WPF tests run without visible windows; native dialogs use injected decisions. The actual control tree render was visually inspected. Interactive Windows dialogs, high-contrast/DPI behavior, clean-machine packaging and installed P0 acceptance remain pending.

## Practical limits

Full-PDF checkpoints consume disk space proportional to document size and edit count; clean close/discard removes the session history. No history quota or power-loss/filesystem durability guarantee is claimed. Recovery lives under the user's LocalAppData/HunterOps/PdfEditor/Recovery directory. Its documents are local files; unencrypted source PDFs produce unencrypted checkpoints.

Destination hashing and atomic replacement are separate operations. A competing rename in their very small gap can race the final check; the replacement backup retains the replaced version. Cloud-sync/network-filesystem behavior and directory-flush guarantees are not established by local Windows tests.

Native reopen, page-count and hash checks do not establish universal structural, visual, signature or standards validity. The page-only renderer still omits AcroForm widget text. Cross-edit object identity, the full shell, tabs, navigation, typography/font-aware editing and the installer remain later work.

Next: Milestone 5, the application shell and multi-document UX.

