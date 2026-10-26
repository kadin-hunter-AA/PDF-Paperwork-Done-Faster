# PDF - Paperwork Done Faster development

The current development checkpoint and verified limits are recorded in [`docs/BUILD-STATUS.md`](docs/BUILD-STATUS.md), [`docs/CAPABILITIES.md`](docs/CAPABILITIES.md), and [`docs/TASKS.md`](docs/TASKS.md). The app targets Windows 11 x64. Build with the bundled SDK, then run the worker regression suite and UI suite:

```powershell
& ./development/tools/dotnet/dotnet.exe build ./development/HunterPdf.slnx --nologo --no-restore
& ./development/tools/dotnet/dotnet.exe run --no-build --project ./development/App.Tests -- ./development
& ./development/tools/run-ui-checks.ps1
```

The UI helper copies the already-built test output to a unique temporary directory before launch because this host can deny loading the test assembly directly from the workspace. It writes evidence under `development/artifacts/`, uses the writable development source/fixtures, and removes only its own temporary copy. A pass is development evidence; it does not establish clean-machine or installed P0 acceptance.

Milestone 5's application shell is complete in the six-project `HunterPdf.slnx` solution. Run `./development/run-milestone5.ps1` to build and reproduce 184 worker, recovery, desktop and shell checks. Run `./development/run-app.ps1` to launch the tabbed desktop with recent files, panels, light/dark/high-contrast behavior, Save As, Undo/Redo and recovery. See `docs/MILESTONE-5-COMPLETION.md` for evidence, shortcuts and UI previews.

Milestone 6's viewer implementation is in place: continuous page display, navigation, fit modes, bounded rendering and thumbnail caches, bookmarks, text search and selectable text. The six-project solution builds cleanly. Interaction-level acceptance remains to be checked; see `docs/MILESTONE-6-COMPLETION.md`.

Milestone 2 corpus is complete. Run `./development/run-milestone2.ps1` to verify the frozen `fixtures/corpus-v1` snapshot: 61 PDFs, 14 fixture categories and 43 P0 scenarios. Add `-OpenResults` to inspect the markup gallery and multilingual sample. To rebuild into a new baseline, pass `-BuildCorpus -OutputPath <new-folder>`; existing snapshots are never overwritten. This completes test-corpus preparation, not the editor or installer. See `docs/MILESTONE-2-COMPLETION.md`.

Milestone 1 general nested text editing is resolved. Run `./development/run-nested-checks.ps1 -OpenResults` for the complete engine proof, 28 nested regression cases, and exact independent Poppler rendering comparisons. It opens the sharing sample after checks pass. Optional arguments: `-OutputPath <new-folder>` and `-PopplerPath <pdftoppm.exe>`. Read `docs/NESTED-REPLACEMENT.md` for supported boundaries and dependency setup. Direct PDFium nested setters remain a negative test; the adapter preserves the form hierarchy and isolates shared instances.

This is an engine proof harness, not the desktop editor.
It replaces a real existing PDF text object; no cover-up rectangles are used.

Requirements: Windows x64, .NET SDK 10.0.401, PDFium chromium/8086, Python and pinned pypdf 6.1.3 (the nested-edit adapter).
For a fresh setup, run `./development/tools/setup.ps1`; the PDFium download is checked against a pinned SHA-256.
The local SDK is in `tools/dotnet`; native headers, DLL and complete license notices are in `native`.

From the project root, run:

```powershell
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& ./development/tools/dotnet/dotnet.exe run --project development/EngineProof -- ./development/artifacts/my-run
```

Use a new output directory for each run. The harness refuses to overwrite existing PDFs.
Outputs: `original.pdf`, `edited.pdf`, a PPM page render and `proof.json` with passed/pending checks.
All native declarations stay in the prototype adapter. Document, page, text and bitmap handles are closed deterministically.
The harness process is isolated, but the application worker protocol is not implemented yet.

The combined Milestone 1 gate now passes for documented supported cases. Later application milestones have not begun.
The full required project documents are in `docs`.

For the corpus checkpoint, run `./development/run-corpus.ps1 -OpenResults`. This creates 19 synthetic fixtures and a hash/expectation manifest; it checks layout extraction, image-only classification, mixed pages, 500-page random access and malformed-file rejection. Full corpus coverage remains pending.

For encrypted and interactive/graphics compatibility cases, run `./development/run-compatibility.ps1 -OpenResults`. Python is required; the current workstation has the pinned tooling installed locally. On a fresh machine, install those tools with `python -m pip install --target development/tools/python-packages -r development/tools/compatibility-requirements.txt`. Test-only passwords are `corpus-user` and owner `corpus-owner`. The signature field is unsigned; no cryptographic-signature support is claimed.

For font-mapping, scanned-text, public fixture and real signed-document checks, run `./development/run-edge-checks.ps1 -OpenResults`. On a fresh machine also install `python -m pip install --target development/tools/signing-packages -r development/tools/requirements-signed.txt`. The signing helper creates a disposable private key in memory and an explicitly test-trusted certificate; it never installs certificates or keys. Original signed PDF is preserved. Rewritten and tampered PDFs are negative tests and are not opened by the runner.
