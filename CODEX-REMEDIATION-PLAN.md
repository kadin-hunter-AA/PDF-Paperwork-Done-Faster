# Remediation plan — audit of October 13, 2026

Hand-off brief for a Codex orchestrator. It turns the read-only audit into bounded work packages. Read `AGENTS.md` first; it remains authoritative where this file is silent. Work only in `development/` (and the docs named below). Never touch the read-only mirror named in `AGENTS.md`. Treat `development/fixtures/` as frozen.

---

## 1. Model routing (token budget)

| Role | Model | Owns |
|---|---|---|
| **Orchestrator** | gpt-6.1 sol (you) | Wave planning, approvals with the user, document-safety designs (WP4, WP6), reviewing every diff, merging test registrations, running the full gates, final report. |
| **Worker** | cheapest available model (`AGENTS.md` names `gpt-6-luna`) | Every work package below unless marked otherwise. One package per subagent, narrow file ownership. |
| **Escalation** | gpt-6.1 sol subagent | Only when a worker fails the same acceptance check twice, or hits an unresolved design/correctness question. Never for routine edits. |

Token rules for the orchestrator:

- Give each subagent **only** its package text, its owned files, and the specific line references. Do not paste the whole repo or this whole plan.
- Require each subagent to return: a ≤300-word summary, the list of files changed, the exact test commands run with pass/fail counts. Review the **diff**, not whole files.
- Subagents run builds/tests only for their own scope; the orchestrator runs the complete gates once per wave.
- Never rebuild worker output while application/UI gates are using it. Await their completion before the next build to avoid locked binaries and mixed-version evidence.
- Use focused reproductions for the listed findings and confirm each fix with its acceptance evidence. Do not repeat the full October 13 audit unless a focused check reveals a new discrepancy.
- Never reduce verification to save tokens, and never mark something complete without evidence (per `AGENTS.md`).

### Subagent prompt template

```
You own ONLY these files: <list>. Do not edit anything else; if another file must change, stop and report.
Project rules: Windows 11 x64 WPF PDF editor. Keep original PDFs intact; failure/cancel paths must be source-preserving; do not expose unfinished features; no claims of redaction/digital signing. Fixtures are frozen.
Task: <package text>
Acceptance: <package acceptance>
Build: & ./development/tools/dotnet/dotnet.exe build ./development/HunterPdf.slnx --nologo --no-restore
Return: ≤300-word summary, changed files, commands run with results. Do not update docs/BUILD-STATUS.md.
```

---

## 2. Ground rules

1. **Ask the user before**: any `git commit`, deleting or moving any file/folder not created in this run, changing the product name, changing where backups or recovery data are stored, or deleting anything in user-visible folders. If the user is unavailable, skip that item and list it in the final report.
2. Keep the WPF + worker-process architecture and the checkpoint/save/recovery design (D-006, D-007, D-009). Fix edges; do not redesign.
3. New tests go in **new** proof files (one per package) to avoid merge conflicts. Only the orchestrator edits `App.Tests/Program.cs` and `App.UiTests/Program.cs` to register them.
4. UI tests must run via `./development/tools/run-ui-checks.ps1` (direct `App.UiTests` launch fails from the OneDrive path).
5. Every package must leave the solution building with zero warnings and zero errors.

---

## 3. Waves and file ownership

Only packages with disjoint, explicitly assigned files may run in parallel. Serialize shared-file phases within a wave; release ownership before another worker edits the file. All paths below are relative to `development/` unless explicitly rooted at the workspace.

| Wave | Packages | Notes |
|---|---|---|
| 0 | WP0 (orchestrator) | Baseline, approvals. |
| 1 | WP1 (orchestrator), WP2, WP10 | WP1 must land before WP3. |
| 2 | WP3, WP4, WP6 | WP4 = orchestrator designs, worker implements. WP6 = same. |
| 3 | WP5, WP7, WP9 | WP5 after WP4 (shares `EnginePages.cs`); WP7 after WP6 (shares `DocumentSession.cs`); WP9 after WP3 (shares `MainWindow.xaml.cs`). |
| 4 | WP8 | Cross-cutting dedup; run alone. |
| 5 | Full gates, WP11 docs, final report | Docs only after code is verified. |

---

## 4. Work packages

### WP0 — Baseline and approvals (orchestrator)

1. Record the baseline build and focused reproduction status. The initial baseline failed with four compile errors: missing `JsonElement` import in `DocumentSessionPages.cs` (two errors), undefined `PdfFormType` in `EngineForms.cs`, and undefined `ValidateSourcePath` in `EnginePages.cs`. The next build exposed missing WPF print serialization event types. WP1 repairs those narrow source-helper/import/delegate mappings and the print submission callback. Optional repository housekeeping does not block code fixes.

Baseline decision: no commit/branch/cleanup/OneDrive relocation is required before remediation. Preserve repository history and historical artwork. OneDrive is a hypothesis for the UI-test `FileLoadException`, not an established cause; leftover test sentinel cleanup is optional housekeeping.

### WP1 — Fix the compile break (orchestrator, tiny)

- `App.Desktop/PrintPreviewWindow.cs:134` calls `PrintPipeline.SubmitAsync(sink, document, ticket, status, token)`; `App.Desktop/PrintPipeline.cs:150` requires `(sink, document, ticket, status, Action submissionStarted, cancellation)`.
- **Owns:** `App.Desktop/PrintPreviewWindow.cs`, `App.Desktop/PrintPipeline.cs`, new `App.UiTests/PrintCancellationProof.cs`, and the baseline-only import/helper repairs in `App.Core/DocumentSessionPages.cs`, `App.Pdfium/EngineForms.cs`, `App.Pdfium/EnginePages.cs`. Orchestrator owns registrations.
- Pass a `submissionStarted` callback that updates submission state/status and keeps Cancel available while XPS submission is pending. Explain that accepted pages may still print. Printer-free sink tests do not establish physical XPS/printer acceptance.
- **Acceptance:** clean build; existing print-helper shell checks pass; focused checks cover preparation cancellation, cancellation while submission callback updates state/status, and completion racing with cancellation.

### WP2 — UTF-8 paths and passwords (worker)

- **Owns:** `EngineProof/Pdf.cs` (linked into App.Pdfium — confirm via the csproj) and new `App.Tests/Utf8PathProof.cs`. Report any other path/password interop owners without editing them; coordinate explicit ownership with WP3/WP4 if follow-up changes are required.
- `FPDF_LoadDocument` is `CharSet.Ansi` (`Pdf.cs:95`); PDFium expects UTF-8. Marshal path and password as UTF-8 (`[MarshalAs(UnmanagedType.LPUTF8Str)]` or `LibraryImport` with `StringMarshalling.Utf8`). Check every other string import (e.g. `FPDFPageObj_NewTextObj` font name is ASCII-only and fine; `FPDF_SaveAsCopy`-style writers use callbacks — verify).
- Tests (copies of fixtures in a temp dir, never the fixture folder): open, edit, Save As and reopen with names like `Café ü 日本.pdf`; non-ASCII password on an encrypted copy (generate one in the test or reuse an existing test-encrypted fixture copy); Combine with a non-ASCII input and output.
- **Acceptance:** new proof passes; `run-nested-checks.ps1` engine proof still passes (the file is shared with the harness).

### WP3 — Crash safety and UI honesty (worker)

- **Owns:** `App.Desktop/MainWindow.xaml.cs`, `App.Desktop/App.xaml`, `App.Desktop/App.xaml.cs`, `App.Desktop/CombineWindow.cs`, `App.Desktop/PrintPreviewWindow.cs` (after WP1), `App.Core/Protocol.cs`, `EngineProof/Pdf.cs` (after WP2), `App.Worker/Program.cs`, new `App.UiTests/CrashSafetyProof.cs`. Explicitly allocate any additional engine/core error propagation files before dispatch. Release startup/protocol ownership to WP4 after this phase.
- Add global handlers only where recovery is proven: log and show a non-technical message, mark a specifically identified affected tab as needing reopen, and continue only when recoverability is established. Otherwise retain recovery evidence and terminate safely. An unobserved task exception cannot be assumed to belong to the active tab.
- Wrap `Search_Click`, `Search_KeyDown` (~703–744) and `Selection_MouseDown` (~746–758) in try/catch for `WorkerUnavailableException`/`OperationCanceledException`, and respect the existing `busy` guard so they cannot read `tab.Pages` mid-rebuild.
- Open path (~247): only prompt for a password when the worker reports an encryption/password error; show a damaged/unsupported message otherwise. Add typed PDF-open error plumbing across explicit owners `App.Core/Protocol.cs`, `EngineProof/Pdf.cs`, and `App.Worker/Program.cs`; serialize shared ownership with WP2 and WP4 before edits to shared files.
- Print: default the range to the current page (or first ≤10 pages) when the document exceeds the 10-page limit, and state the limit before the user submits.
- `CombineWindow.cs:93-95`: set `OverwritePrompt = false` and reject existing files in the dialog flow with the existing message.
- Add Text: label the coordinates "PDF points from the bottom-left corner" and show the fixed font/size (Helvetica 18 pt). Do not add new font features.
- **Acceptance:** UI tests via `run-ui-checks.ps1` pass, including new checks: a killed worker during search does not crash the app; a corrupt file does not show a password prompt; Combine dialog flag.

### WP4 — Worker timeouts and leftover temp files (orchestrator designs, worker implements)

- **Owns:** `App.Core/WorkerClient.cs`, `App.Pdfium/Engine.cs`, `App.Pdfium/EnginePages.cs` (Duplicate/Split parts only), `App.Core/Protocol.cs`, `App.Worker/Program.cs`, `App.Desktop/App.xaml.cs`, `App.Desktop/CombineWindow.cs` (shared files only after WP3 releases them), new `App.Tests/TimeoutCleanupProof.cs`. Allocate any new app-owned temp helper explicitly. Serialize shared protocol/startup/UI phases with WP3.
- Replace the fixed 30 s timeout (`WorkerClient.cs:22`) with named, measured per-operation budgets: short for queries/render and scaled for Save/Combine/Split/edits. Report timeout distinctly from user cancellation; cancellation still kills the worker per D-006. Use deterministic stalled-peer coverage and a representative large document to establish the budgets.
- Define an explicit startup owner in `App.xaml.cs` after WP3. Define the temp-path contract before dispatch: either the parent chooses and passes the exact candidate path and the worker validates it, or a documented pre-dispatch contract makes that exact path known to the parent. Verify ownership, candidate links/reparse points, and active leases before cleanup. Make temp names traceable and clean them up safely:
  - Duplicate pages writes a full copy to `%TEMP%` (`EnginePages.cs:108`): use an app-owned subfolder (e.g. `%TEMP%\HunterOpsPdfEditor\<guid>`); on desktop startup, delete only entries in that app-owned folder older than 24 h and not locked.
  - Combine candidates (`Engine.cs:53`, `.pdfeditor-combine-*.tmp`) and Split staging dirs (`EnginePages.cs:191`) live in **user** folders: do **not** auto-delete. After a timeout/cancel kill, the parent should check for the exact candidate path it was told about and tell the user where it is (extend the existing message in `CombineWindow.cs:103` only via the orchestrator if a UI change is needed).
- **Acceptance:** deterministic stalled peer distinguishes timeout from cancellation; representative large-document operation completes within measured budget; forced kill leaves source untouched and reports the exact leftover path; startup sweep removes only eligible app-owned entries after ownership/link/active-lease checks.

### WP5 — Unused page operations (worker, after WP4)

- **Owns:** `App.Core/DocumentSessionPages.cs`, `App.Pdfium/EnginePages.cs` (Extract/Split/Delete/Duplicate/Insert), new `App.Tests/PageExportProof.cs`.
- `ExtractPagesAsync`/`SplitPagesAsync` (61–80): call `EnsureWorkerAsync` like the edit path, so they never export from a state flagged untrustworthy after `ReplaceImage` (`DocumentSession.cs:257`).
- Extract and Split: verify candidate page count, geometry and that each page loads before the final move, matching D-009. Current `SavePagesToNewPdf` already reopens and checks these; preserve that implementation and add missing evidence rather than duplicating validation.
- Move the page-count checks at `DocumentSessionPages.cs:31,41` inside the session lock.
- Add tests for Delete, Duplicate, Insert-from-file, Extract and Split: success, undo/save/reopen where applicable, rejection paths, source unchanged.
- Keep these operations **hidden from the UI** (the organizer text correctly says they are unavailable). Do not add UI.

### WP6 — Recovery and backup growth (orchestrator designs, worker implements)

- **Owns:** `App.Core/RecoveryStore.cs`, `App.Core/DocumentSession.cs` (history/backup parts), `App.Desktop/DocumentDialogs.cs`, recovery-choice branches in `App.Desktop/MainWindow.xaml.cs` (only after WP3 releases it), new `App.Tests/RecoveryRetentionProof.cs`, new `App.UiTests/RecoveryChoiceProof.cs`. Orchestrator integrates dialog interface changes in existing test doubles and owns runner registrations. Serialize recovery UI work with WP3.
- Delete redo snapshot files when a new edit truncates redo (`DocumentSession.cs:249`), only after the new checkpoint is committed.
- Cap undo history (choose count and/or bytes); prune oldest snapshots only after the manifest is committed and outside the rollback catch; never prune the committed current snapshot. Preserve required current/history references and saved identity semantics. Add fault injection around commit/pruning.
- Remove crash-orphaned recovery directories that have no `session.json` (`RecoveryStore.cs:111`) only when they are older than 24 h and inside the app recovery root; verify leases, links/reparse points, and orphan protection.
- Replace the current Yes/No/Cancel recovery result contract with explicit Recover / Not now / Discard / Cancel choices. Discard requires confirmation; declining confirmation and Cancel retain the entry. Add UI proof for all four choices, confirmation rejection and source preservation. Do not invent a fourth native MessageBox button; use an accessible custom dialog with explicit choices.
- Backups next to user files (`DocumentSession.cs:347`, `name.pdf.<guid>.backup.pdf`): default behavior remains in place; no retention-policy approval blocks unrelated fixes. If pruning is pursued, use exact provenance/manifest data and preserve saved identity semantics; do not rely on a broad filename glob. Any policy change requires separate approval.
- **Acceptance:** tests prove truncation cleanup, cap, orphan sweep boundaries (non-app folders untouched), Discard, and that recovery after a simulated crash still restores the last committed edit with intact undo history up to the cap.

### WP7 — Text preview performance (worker, after WP6)

- **Owns:** `App.Core/DocumentSession.cs` (preview method ~194), `App.Desktop/ObjectEditorWindow.cs`, new `App.Tests/PreviewLifecycleProof.cs`, new `App.UiTests/PreviewResponsivenessProof.cs`, and an explicitly allocated preview-lifecycle helper if needed. Orchestrator owns runner registrations.
- Define each editor preview lifecycle explicitly: checkpoint, lease, and revision reset. Debounce input (~300 ms), cancel the previous request, reuse one preview worker per editor window, and dispose it on close. Cancellation must kill the preview worker and permit recreation. Do not hold the session lock while rendering the candidate. Behavioral checks must prove the latest result wins, the UI stays responsive, and preview changes neither history nor source. Add a new proof for these behaviors.
- **Acceptance:** retain the existing 15 text-preview checks; behavioral preview proof demonstrates latest-result wins, responsiveness, source/history immutability, and supported worker recreation after cancellation.

### WP8 — Shared contracts and dedup (worker, alone in wave 4)

- Divide into WP8a (mechanical refactoring) and WP8b (correctness fixes), reviewed and gated separately. Before dispatch enumerate exact files and new helpers from the current source; no blanket ownership. Include affected project files explicitly when linking shared source. Orchestrator owns test registrations.
- WP8a: centralize operation/revision metadata, operation-specific limits, equivalent page-range parsing and path comparison. Keep revision-bumping and detailed-error policies separate. Consolidate bitmap conversion and native imports only with equivalent signatures/behavior.
- Refactor shared contracts only where equivalence is demonstrated. Preserve operation-specific limits, including the existing 8M render and 2M print pixel limits; do not replace them with a blanket 4M limit. Compare parser behavior on edge cases before consolidation.
- One helper for JSON render result → `BitmapSource` (7 copies: MainWindow ~590/617, ObjectEditor 74/111, ImageEditor 94, PageOrganizer 155, PrintPreview ~358).
- Remove duplicate PDFium imports (`FPDFPageObj_GetMatrix`, `GetClipPath`, `CountMarks`, `FPDF_GetFormType`) and the duplicated image-eligibility check (`EngineImages.cs:92-117` vs `119-131`).
- WP8b: fix missing `AddText` detailed-error reporting, text-page handle cleanup (`Engine.cs`, use try/finally), and double-dispose guards on `Pdf` and `PageHandle` (`Pdf.cs`). Add focused negative-path evidence; these are correctness changes, not behavior-neutral deduplication.
- Separate mechanical refactoring from safety fixes so behavior changes remain reviewable. Preserve existing meaningful assertions and check coverage; exact historical counts need not remain identical when assertions are clarified, but no coverage may be lost.

### WP9 — Brand-token cleanup (worker, after WP3)

- **Owns:** `App.Desktop/ShellTheme.cs`, `App.Desktop/MainWindow.xaml`, the color lines in `MainWindow.xaml.cs` (~546, ~809) and `PrintPreviewWindow.cs:63`.
- Replace `#555`, `Brushes.DimGray`/`LightGray` and the blue selection overlay `ARGB(95,60,135,235)` with theme resources derived from `design/brand-tokens.json` (selection: Reverse purple `#9279FB` at translucency; secondary text: Muted ink / Muted lavender per theme). Keep high-contrast system-color overrides. Check text contrast ≥ 4.5:1.
- Optional, only if cheap: generate the token values from `brand-tokens.json` at build time instead of hardcoding them in `ShellTheme.cs:30-31`.
- **Acceptance:** shell UI checks pass; light/dark/high-contrast renders inspected by orchestrator.

### WP10 — Script hardening (worker)

- **Owns:** `tools/setup.ps1`, `tools/Install-PerUser.ps1`, `tools/Uninstall-PerUser.ps1`, `tools/Test-PerUserUninstall.ps1`, new `tools/Test-PerUserInstall.ps1`.
- `setup.ps1:8-9`: pin `dotnet-install.ps1` to a SHA-256 (or verify its Authenticode signature) before running it, as PDFium already is.
- Install: when a ZIP and its `.sha256` sidecar are supplied, verify before staging; refuse on mismatch.
- Uninstall: tell the user that `%LOCALAPPDATA%\HunterOps\PdfEditor\Recovery` holds document copies and is kept; offer an explicit `-RemoveRecoveryData` switch (default off). Re-check reparse points immediately before each deletion to narrow the existing race.
- Add an install test in disposable temp folders only (mirror the existing uninstall test). Never run install/uninstall against the real `%LOCALAPPDATA%\Programs` path.

### WP11 — Documentation alignment (worker, wave 5, orchestrator reviews every claim)

- Create `development/docs/ROADMAP.md` as the single roadmap: P0 feature list (from `fixtures/corpus-v1/scenarios.json` categories), Milestones 7–12 defined in this workspace, the design-order sequence from `design/WINDOWS11-DESIGN.md`, and a **Deferred** section (below).
- `development/docs/CAPABILITIES.md`: update capability status from verified evidence. `docs/BUILD-STATUS.md`: retain historical evidence and add a clear latest-source status without conflating older package evidence with current source. Keep dated changelog and reconcile the `20261012-final-v2` vs `20261013-…-v2` distinction and superseded missing-JSON note accurately. Do not invent results.
- `docs/DECISIONS.md`: order D-001…D-010 numerically; move "Open decisions" to the end; correct D-005 (pypdf adapter is harness-only, not shipped). Add D-011 for the WP4 timeout policy and D-012 for WP6 retention.
- Tick finished items in `docs/RELEASE-CHECKLIST.md` and `docs/TASKS.md` only where evidence exists.
- Root `README.md`: replace the direct `App.UiTests` command with `tools/run-ui-checks.ps1`.
- `development/README.md`: remove stale lines ("engine proof harness, not the desktop editor", "worker protocol is not implemented yet", "later application milestones have not begun", Milestone 6 "acceptance remains").
- `AGENTS.md`: reconcile feature wording with current verified capabilities; keep routing guidance. The selected product name is already established in the design baseline; do not rename the product or ask the user to reconfirm it.
- `design/logos/SELECTED-DESIGN.md` and `design/README.md`: preserve established name/design decisions and update stale implementation status only when supported by current evidence.

---

## 5. Deferred — record in ROADMAP.md, do not implement

- Nested text editing in the product: the occurrence-based adapter exists only in `EngineProof` (Python/pypdf, not in the solution or package). Needs a decision: port to C# or remove from P0.
- MVVM split of `MainWindow.xaml.cs` (render cache, search/selection, tab management) and moving code-built windows to XAML.
- Form-aware rendering via a validated `FPDF_FORMFILLINFO` implementation (fixes print fidelity).
- MSIX or signed installer, code signing, upgrade/repair, PDF file association.
- CI on a Windows runner; clean-machine acceptance using Windows Sandbox.
- Device acceptance still open since Milestones 3–5 (native dialogs, physical drag/drop, screen reader, 100–200% scaling, physical printer); release gate outside this remediation outcome.
- Install-path usability review may be deferred; do not rename the product as part of this remediation.
- `LibraryImport` source-generated interop across the adapter.

---

## 6. Final verification (orchestrator)

Run, in order, and record counts and evidence folders:

```powershell
& ./development/tools/dotnet/dotnet.exe build ./development/HunterPdf.slnx --nologo --no-restore
& ./development/tools/dotnet/dotnet.exe run --no-build --project ./development/App.Tests -- ./development
& ./development/tools/run-ui-checks.ps1
& ./development/run-nested-checks.ps1
& ./development/run-milestone2.ps1
```

Preserve every meaningful existing assertion and run all new proofs. Baseline minima: worker 32, session 70, text 22, preview 15, markup 20, image 28, pages 38; desktop 41, shell 69, workflow, decoder 10. Do not remove checks to meet a budget. If refactoring changes how assertions are counted, supply an explicit old-to-new coverage mapping and explain the difference; counts alone do not prove equivalence. Do **not** publish a new package unless the user asks; if they do, use a fresh folder with `tools/Publish-Portable.ps1`.

## 7. Execution status — October 13, 2026

- Plan revision and WP0: complete. Baseline compile errors and subsequent WPF print-event integration errors were repaired narrowly.
- WP1: complete for development acceptance. Cancel remains available during submission; six injected-sink checks pass. Physical printer/XPS cancellation remains a release gate.
- WP2: complete. Eleven UTF-8 path/password checks pass, including encrypted-copy edit/save/reopen, ordered combine and source hashes; nested-engine gate also passes.
- WP10: partial. SDK installer identity pin, authenticated archive installation, traversal/link rejection and immediate uninstall link checks are implemented; disposable script gates pass. Optional recovery-data removal remains unimplemented; normal uninstall preserves recovery. Independent orchestrator download/hash verification of the SDK script was rejected by automatic approval review (`blocked by policy`), so that command was not retried. Full SDK bootstrap was not exercised.
- Final first-wave solution build: zero warnings/errors. Core gates: 32/70/22/15/20/28/38 plus 11 UTF-8 checks. UI: 41/69, workflow, decoder 10, print cancellation 6. Nested: 13 positive/15 rejected cases independently checked; corpus: 61 hashes/43 scenarios/14 categories. Details: `development/docs/REMEDIATION-WAVE1.md`.
- WP3–WP9 and WP11 remain pending; this is a first-wave checkpoint, not complete remediation or installed-release acceptance. No package was published.
- Optional baseline commit/branch, stray artifact cleanup, and moving the working copy out of OneDrive are deferred and do not block fixes. Preserve repository history and historical artwork. OneDrive remains a hypothesis for the UI-test `FileLoadException`, not an established cause.
- Clean-machine and device acceptance remain release gates outside this remediation outcome.

## 8. Final report format

1. Per package: done / partial / skipped (with reason), files changed, new checks and counts.
2. Items waiting on user approval or manual action.
3. Gate results with evidence paths.
4. Remaining open items, quoted from ROADMAP.md, with no completion claims beyond the evidence.
