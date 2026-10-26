# Release checklist — current checkpoint and historical gates

## October 13, 2026 continuation — latest source, release not accepted

- [x] Rebuild the integrated solution after the sticky-note code and product-UI gate; zero warnings and errors. The sticky-note selector remains hidden because the CLR denied loading the focused runtime regression assembly. See `BUILD-STATUS.md` and `artifacts/p0-sticky-note-20261013/VERIFICATION.md`.
- [x] Add per-user schema-2 install manifests and versioned upgrade/repair behavior; disposable install/uninstall checks passed. Installed upgrade/repair acceptance remains open.
- [x] Complete a source/package-v6 redistribution inventory review. The report identifies unresolved provider-license and exact .NET/WPF notice questions; it does not clear redistribution.
- [x] Clean low-alpha icon-edge specks, generate safe-margin exports, and inspect small assets on light/dark backgrounds. Actual Windows shell/taskbar/installed checks remain open.
- [ ] Produce a complete current portable package. The v7 staging output is explicitly preserved as incomplete because host access denied execution of the publisher and copying the current installer scripts; it has no verified ZIP.
- [ ] Run on a clean Windows 11 x64 machine, verify install/launch/save/reopen/recovery, then upgrade/repair/uninstall while preserving user data.
- [ ] Complete native dialog, drag/drop, accessibility/scaling and physical printer checks. Computer-use activation failed; the configured Kyocera is offline.
- [ ] Complete remaining P0 editing/markup workflows and exact-artifact redistribution review before release.


## October 13, 2026 current integrated checkpoint — portable v6

- [x] Build the integrated solution with zero warnings/errors and pass the worker/session suite (285 checks), AcroForm viewer/print render proof (11 checks), and independent pypdf extraction/split verification. Evidence: `artifacts/worker-710d04306ed24744bd3fa8542143783b/`.
- [x] Pass full UI suite: desktop state 41, shell 69, workflow UI, image decoder 10 and print cancellation 6. Evidence: see paths in `BUILD-STATUS.md`.
- [x] Build portable v6 and inspect the frozen original PDF with its packaged worker; source fixture hash remains unchanged. ZIP checksum and inventory: `artifacts/package-verification-20261013-v6/PACKAGE-VERIFICATION.json`.
- [ ] Launch the packaged v6 desktop and verify its start screen/close using native window controls; this session's computer-use surface has no native app/window API.
- [ ] Complete clean-machine installed P0 acceptance; the current artifact is a portable ZIP and no MSIX/compiled installer exists.
- [ ] Complete physical native dialogs, drag/drop, assistive technology, scaling and printer acceptance.
- [ ] Complete broader P0 implementation, upgrade/repair, redistribution review and production icon cleanup.

## October 13, 2026 current integrated checkpoint — portable v5

- [x] Complete current source build with zero warnings/errors and fresh worker proof: 280 checks, including 82 page organization checks; `artifacts/worker-b9fce19e7dc643adb9f3f7486733c015/`.
- [x] Complete current UI run: desktop state 41, shell 69, workflow UI, decoder 10, print-cancellation 6. See current evidence paths in `BUILD-STATUS.md`.
- [x] Publish v5 portable Windows x64 package; smoke its packaged worker on the frozen one-page input, verify its hash stayed unchanged, compare all 21 carried notice files against v4, inventory 629 files, and verify the final ZIP sidecar. `artifacts/package-verification-20261013-v5/PACKAGE-VERIFICATION.json`.
- [ ] Launch v5 desktop and verify its packaged start screen/close through the native window control; this session's computer-use surface has no native app/window API.
- [ ] Complete clean-machine install and installed P0 acceptance.
- [ ] Complete physical device, accessibility, drag/drop, display-scale and printer acceptance.
- [ ] Complete broader P0 implementation, upgrade/repair, redistribution review and production icon cleanup.

## October 13, 2026 continuation

BUILD-STATUS.md and CAPABILITIES.md describe the current integrated checkpoint. Historical sections below record milestone-specific evidence; unchecked historical prerequisites that were subsequently completed do not supersede newer evidence.

- [x] Re-run final worker, session/save/recovery, text-preview, markup, image-edit and page-organization gates: 32 + 70 + 22 + 15 + 20 + 28 + 38 checks under `artifacts/worker-7c5306a9bca64bbe966fabfc6aa3d5b3/`.
- [x] Independently verify page operation output and numeric-destination refusal/rollback using pypdf; source hashes unchanged.
- [x] Verify WPF text/Combine workflow and 10 image-decoder checks from a temporary test-assembly copy; shell isolated run passed 69 checks and desktop state passed 40.
- [x] Complete the fresh full UI run after dispatcher-idle startup stabilization: desktop 41, shell 69, workflow UI and decoder 10 checks passed via the temporary-copy runner; signature status persists after a realized viewport redraw.
- [ ] Complete full text/image editing, markup and page organization beyond the currently implemented subset.
- [ ] Complete physical printer fidelity/cancellation, native dialogs, physical drag/drop, accessibility and 100–200% scaling acceptance.
- [x] Publish v2 package with latest zero-page/signature-status fixes; verify ZIP sidecar, host-only desktop/worker smoke, and disposable uninstaller safety. See `../artifacts/package-verification-20261013/PACKAGE-VERIFICATION.json`. v1 is preserved as historical.
- [x] Publish v3 with the guarded delete-page UI. Verify the packaged desktop shell opens and closes, the packaged worker inspects the frozen one-page PDF, the frozen fixture hash is unchanged, and the ZIP sidecar matches. See `../artifacts/package-verification-20261013-v3/PACKAGE-VERIFICATION.json`.
- [ ] Resolve the blocked test sentinel residue through an approved cleanup path; no alternate deletion was attempted after automatic review blocked it.
- [ ] Complete clean-machine installed acceptance and remaining full-P0, redistribution, form/print and physical accessibility gates.
- [ ] Complete clean-machine Windows 11 x64 installation, launch, save/reopen/recovery and icon acceptance; establish upgrade/repair behavior.
- [ ] Complete dependency redistribution review and production icon cleanup.

The existing per-user install/uninstall scripts are available; a compiled installer/MSIX and upgrade/repair workflow remain incomplete. Do not mark installed P0 acceptance complete from a source build or offscreen UI check.

## Milestone 5 shell

- [x] Open, switch and close multiple PDF tabs with independent state.
- [x] Save/close inactive documents correctly; Cancel preserves the workspace.
- [x] Deliver persistent recent files and Clear history.
- [x] Deliver collapsible/resizable navigation and properties panels, canvas and operation/status footer.
- [x] Handle PDF drop batches and keyboard open/save/undo/redo/close/tab actions.
- [x] Implement light/dark palettes and Windows system-color high-contrast override.
- [x] Verify explicit automation names, dirty-state announcements, tab roles and keyboard focus styles.
- [x] Inspect dark/light/start/compact WPF renders.
- [ ] Exercise native dialogs, physical drag gestures, screen-reader devices and live Windows high-contrast toggling.

Evidence: MILESTONE-5-COMPLETION.md. Milestone 6 viewer functionality and installed P0 acceptance remain separate.

## Milestone 4 checkpoint

- [x] Track original/saved paths, fingerprints, session revisions and dirty state.
- [x] Restore actual PDF content/page metadata with Undo/Redo and retain history for recovery.
- [x] Validate sibling save candidates; protect originals on worker, validation, lock and write failures.
- [x] Require overwrite consent and retain the prior PDF as a backup.
- [x] Detect changed destinations and originals.
- [x] Recover committed edits/history after abnormal termination; reject corrupt recovery data.
- [x] Require signed-copy consent and protect signed originals.
- [x] Verify WPF command/cancellation/dirty-state flows and inspect an offscreen control-tree render.
- [ ] Verify interactive native Windows dialogs and clean-machine behavior.

Evidence: MILESTONE-4-COMPLETION.md. Full installed P0 release acceptance remains open.

## Milestone 3 checkpoint

- [x] Build the five-project solution with zero warnings/errors.
- [x] Pass worker request/revision, persistence, cancellation and crash/restart integration checks.
- [x] Verify desktop output includes the worker and pinned native engine.
- [ ] Manually smoke-test first-page display and worker crash recovery in the WPF UI.
- [ ] Complete milestone 4 safe saving and recovery before exposing general editing.

Evidence: MILESTONE-3-COMPLETION.md. These checks do not establish installed P0 release acceptance.

Updated October 12, 2026. Checked engine items are backed by NESTED-REPLACEMENT.md, VIEWER-VERIFICATION.md and the nested-acceptance-003 reports. Release packaging work remains open.

## Windows icon and portable package

- [x] Export transparent PNGs at exact sizes and a multi-resolution ICO from the selected artwork without changing its design: see `design/logos/windows/README.md`.
- [x] Set the WPF executable icon, provide its runtime Window resource, declare the Windows 10 compatibility identity used by Windows 11, and guard startup on Windows 11 build 22000 or later.
- [ ] Build and run a clean portable Windows x64 package with both the desktop and self-contained worker, including PDFium and its notices. Use `development/tools/Publish-Portable.ps1` with a new output directory.
- [ ] Complete redistribution review and clean-machine launch/PDFium smoke test. No MSIX package or installer exists.

Icon visual inspection found colored fringe on transparent silhouette edges in the supplied master, and the PDF lettering/nib are difficult to distinguish at 16-24 px. Exports retain the exact artwork; production cleanup and small-size review remain open.

## Dependency and build

- [x] Pin .NET 10 servicing version, target architecture, PDFium binary, and C# wrapper.
- [x] Confirm required API symbols against the pinned artifacts.
- [ ] Review redistribution terms and populate third-party notices.
- [ ] Build the harness from a clean checkout with documented steps.

## Proof scenarios

- [x] Open and render a real fixture; enumerate text and image objects.
- [x] Replace text with a longer string and with characters absent from the subset font.
- [x] Move, resize, replace, and delete an image.
- [x] Add and modify highlight and ink annotations.
- [x] Reorder and merge pages.
- [x] Save as a new file, close handles, reopen, and inspect in an independent viewer.
- [x] Confirm edits persist and unrelated content remains intact.
- [x] Record outcomes for embedded/subset fonts, rotation, shared images, and reusable/nested objects.

## Evidence and decision gate

- [ ] Record exact dependency/build identity, fixture and output hashes, steps, and results.
- [x] Document unsupported cases and reproducible failures.
- [x] Review whether every required operation passes the acceptance gate.
- [x] Resolve engine/wrapper gaps before authorizing the next milestone.

A checked item requires recorded evidence. Do not claim a successful gate from a build alone.

Python/pypdf support the historical nested-edit prototype harness and independent verification; they are not current desktop runtime dependencies. Full clean-machine setup, complete release evidence packaging and redistribution review remain unchecked; historical milestone notes do not supersede the current evidence above.


