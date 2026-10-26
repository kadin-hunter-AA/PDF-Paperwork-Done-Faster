# Milestone 5 — application shell complete

Completed October 12, 2026. The application can open PDFs into tabs, switch between them, and close individual documents or the whole workspace with tested unsaved-change handling. The solution builds with zero warnings/errors.

Run `./development/run-milestone5.ps1` from the project root to reproduce all four gates. Launch the desktop with `./development/run-app.ps1`.

## Delivered

- Native Windows title bar and accessible document tabs with selected state, filenames, dirty markers and per-tab close buttons.
- Open/Recent start screen, persistent deduplicated recent history (up to 20 paths), missing-file status and Clear history.
- Compact command bar, recovery access and working Open/Save/Save As/Undo/Redo controls.
- Collapsible, resizable navigation and properties panels. Navigation lists open documents; properties show the active PDF's page count, dimensions, rotation and save state.
- Central PDF canvas with a white, untinted page. Each tab keeps its session, undo/redo, text draft/positions and zoom separately. The shell renders the active first page only, with bounded raster dimensions.
- Footer with current page position, actual zoom, document state, named active operation and indeterminate progress.
- Light/dark themes using the Hunter Ops palette. Windows high contrast overrides the chosen theme with system colors and responds to the system high-contrast signal; the previous choice is restored afterward.
- Keyboard focus visuals, contrasting selected-item focus borders, explicit automation names/help text, and native tab accessibility semantics.
- File-drop handling at the window preview-event boundary, so dropping PDFs over text inputs still opens documents. Batch opens ignore non-PDF files, select existing paths instead of duplicating tabs, and retain successful opens if another dropped file fails.

Opening another PDF never requires discarding the current tab. Failed opens preserve every existing session. Duplicate paths select the existing tab without replacing its edits.

Closing an inactive dirty tab asks about that document and saves its session, not the currently displayed one. Save As rejects a destination belonging to another open tab. Workspace close collects all decisions before closing any tabs; Cancel, declined overwrite or canceled Save As keeps the workspace open. Closing the last document clears its pixels and returns to the start screen.

Recovery can restore several sessions into separate tabs, retaining each document's unsaved content and history.

## Keyboard shortcuts

| Action | Shortcut |
|---|---|
| Open | Ctrl+O |
| Save / Save As | Ctrl+S / Ctrl+Shift+S |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| Close active document | Ctrl+W |
| Next / previous document | Ctrl+Tab / Ctrl+Shift+Tab |

Tab and Shift+Tab traverse controls. Native controls and access keys support keyboard activation. Resizable splitters have focus visuals and accessibility labels.

## Verification and evidence

All 184 checks pass:
- 17 worker protocol/crash/cancellation checks.
- 70 document-state/safe-save/recovery checks.
- 40 desktop regression checks.
- 57 Milestone 5 shell checks.

Backend evidence: `artifacts/worker-205bacdc06494a628b5912f2d6709e71/results.json` and `session-results.json`.
Desktop evidence: `artifacts/ui-state-b0f769a3f1ea429b941bdfbcc6cc528d/ui-results.json`.
Shell evidence: `artifacts/shell-67e831297afa4237b55ae6b97114b4e8/shell-results.json`.

The shell tests use actual WPF command routing, tab/list controls, automation peers, worker-rendered PDFs and saved/reopened output. Dialog decisions are injected. Coverage includes independent tab edits/drafts/zoom, inactive-tab save correctness, canceled document/workspace closes, cross-tab overwrite protection, duplicate/failing opens, recent history persistence/clearing, panels, operation status, theme changes without PDF tinting, high-contrast override and multi-session recovery.

The frozen corpus still passes: 61 hashed PDFs, 14 categories and 43 P0 scenarios. No synced source material was modified.

Offscreen WPF renders were visually inspected:
- `artifacts/shell-67e831297afa4237b55ae6b97114b4e8/start-dark.png`
- `artifacts/shell-67e831297afa4237b55ae6b97114b4e8/documents-dark.png`
- `artifacts/shell-67e831297afa4237b55ae6b97114b4e8/documents-light.png`
- `artifacts/shell-67e831297afa4237b55ae6b97114b4e8/compact-light.png` (760 × 600)

## Scope and next work

Milestone 5's shell acceptance gate is met. Milestone 6 remains separate: page navigation/continuous scrolling, fit modes, thumbnails, bookmarks, search, text selection, render caching/cancellation and coordinate/DPI verification. The navigation panel currently lists documents, and the canvas currently displays page 1; full viewer navigation is not claimed.

Native file/password dialogs, physical drag gestures, screen-reader device use and actual Windows high-contrast toggling were not exercised by the offscreen gate. High contrast was verified using an injected mode signal and real Windows system brushes. Interactive device/clean-machine checks remain on the release checklist.

Text drafts stay per tab while it is open; only committed PDF edits belong to the durable recovery history. Recent settings contain local paths and theme preference, never PDF text or passwords. One worker is retained per open document; inactive tabs keep their native sessions, while only the active bitmap is displayed. The shell serializes document gestures during worker operations. Large-document performance and disk-checkpoint optimizations remain later work.

No installer, complete editing toolset or installed P0 release acceptance is claimed. Next: Milestone 6, deliver the viewer.

