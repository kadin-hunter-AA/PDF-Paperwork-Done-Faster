# Remediation first-wave checkpoint — October 13, 2026

The remediation plan was revised and its first implementation wave verified in the writable `development/` tree. This is development evidence, not completed remediation or installed-release acceptance. No commit, branch change, package publication, source relocation, historical-art deletion, or backup-policy change was performed.

## Changes and package status

| Package | Status | Result |
|---|---|---|
| Plan / WP0 | Done | Added focused reproduction, explicit shared-file sequencing, transactional cleanup requirements, preview lifecycle acceptance, split refactoring/correctness work, and separate release gates. |
| WP1 | Done for development acceptance | Fixed missing JSON import, mapped form-type call to existing native declaration, replaced unresolved insertion-path helper with explicit empty-path rejection before existing path/existence checks, corrected WPF print-event imports/delegates and supplied the missing submission callback. Cancel stays available during submission. |
| WP2 | Done | PDFium path/password parameters explicitly marshal as UTF-8. New proof covers Unicode paths, non-ASCII AES-256 password, edit/Save As/reopen, ordered combine and unchanged input/fixture hashes. Test encryption uses pinned pypdf 6.1.3; no product Python dependency was added. |
| WP10 | Partial | Pinned official SDK installer identity, added verified archive input, refused bad/malformed sidecars and traversal/symlink ZIP entries, preserved directory-source install, and added immediate uninstall-root/descendant link checks. Disposable install/uninstall gates pass. Optional recovery-data deletion remains pending; recovery is retained. |
| WP3–WP9 / WP11 | Pending | No completion claim. Timeout policy, transactional retention, crash handling, preview lifecycle, broader page-operation evidence, deduplication, theme cleanup and full documentation alignment remain. |

WP1 edited `App.Core/DocumentSessionPages.cs`, `App.Pdfium/EngineForms.cs`, `App.Pdfium/EnginePages.cs`, `App.Desktop/PrintPipeline.cs`, `App.Desktop/PrintPreviewWindow.cs`, and added `App.UiTests/PrintCancellationProof.cs`. WP2 edited `EngineProof/Pdf.cs` and added `App.Tests/Utf8PathProof.cs`. Orchestrator registered proofs in both test runners. WP10 edited `tools/setup.ps1`, `tools/Install-PerUser.ps1`, `tools/Uninstall-PerUser.ps1`, `tools/Test-PerUserUninstall.ps1` and added `tools/Test-PerUserInstall.ps1`.

## Verified gates

Commands were run from the workspace root with the bundled .NET SDK 10.0.401. Evidence below is relative to `development/artifacts/`.

| Gate | Result | Evidence |
|---|---|---|
| `tools/dotnet/dotnet.exe build HunterPdf.slnx --nologo --no-restore` | Zero warnings/errors | `remediation-wave1-555882d1341d431498f18c9ee03cd9e9/build-final.log` |
| App.Tests (`run --no-build --project development/App.Tests -- development`) | Worker 32, session 70, text 22, preview 15, markup 20, images 28, pages 38, UTF-8 11 | `worker-959d62a4f58b48978bb3ec9342d5eeff/`, including `utf8-paths/results.json` |
| `tools/run-ui-checks.ps1` | Desktop 41, shell 69, workflow passed, decoder 10, print cancellation 6 | `remediation-wave1-555882d1341d431498f18c9ee03cd9e9/ui-tests-final.log`; individual report paths are in that log |
| `run-nested-checks.ps1` | PASS: 13 positive / 15 rejected cases; independent Poppler comparisons, unchanged sources and retained form hierarchy | `nested-02bd17e1355a440bb3f9051c9d4d211f/` |
| `run-milestone2.ps1` without BuildCorpus | PASS: 61 PDF hashes, 43 scenarios, 14 categories | `remediation-wave1-555882d1341d431498f18c9ee03cd9e9/corpus-checks.log` |
| `tools/Test-PerUserInstall.ps1` | PASS: checksum, malformed sidecar, traversal/symlink rejection; archive/directory install and existing-destination refusal | `remediation-wave1-555882d1341d431498f18c9ee03cd9e9/install-test-final.log` |
| `tools/Test-PerUserUninstall.ps1` | PASS: unsafe inventory refusal, owned-file removal, unknown/user-data preservation | `remediation-wave1-555882d1341d431498f18c9ee03cd9e9/uninstall-test.log` |
| PowerShell Parser.ParseFile | All five changed scripts parse without errors | Orchestrator tool output; no SDK bootstrap executed |

The baseline failed with four compile errors. A subsequent build exposed missing print-event types. The first UTF-8 proof run failed Save As because the effective linked adapter still had ANSI marshaling; it was corrected without weakening the proof. A build overlapped UI worker usage and produced four file-copy retry warnings; after the UI runner exited, the final build above passed cleanly. Initial failed/superseded logs are retained alongside final logs.

## Limits and next work

Print tests use real raster preparation and an injected sink; they do not verify native XPS cancellation or a physical printer. Existing form-rendering source was repaired for compilation, not newly accepted as a feature. Nested editing remains harness-only. Script tests use fake executables in disposable folders and skip shortcuts; no real per-user install, upgrade, clean-machine or shell acceptance was performed. Frozen fixtures were verified without rebuilding or modifying them. Existing packages remain historical and do not contain these changes.

The SDK script is pinned to official dotnet/install-scripts commit `e5cf1dd2d1540ed05ac84f8eb8c5cdec2807621e` and SHA-256 `3BB07BC8025211836C1E4F9D3F6A044E55B1FB6EEC518A6C78851D04E210442B`. Automatic approval review rejected the orchestrator's separate download/hash verification command with reason `blocked by policy`; it was not retried. The full bootstrap and mismatch branch were not executed. Optional recovery-data removal is pending and no recovery copies were deleted by the uninstaller.

Next: WP3 command-level crash handling and typed open errors, then explicitly sequenced WP4 timeout/temp-path design and WP6 transactional retention. Keep current backup behavior while unrelated fixes proceed. Clean-machine, physical-device and installed-release gates remain open.
