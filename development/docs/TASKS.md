# Tasks and milestone status — PDF - Paperwork Done Faster

## Later October 16 release-check continuation

Internal v11 (1.2.2) adds bounded Arrow markup, small-printable-area fit and raster-overflow fixes, and conservative detached-alpha icon cleanup. Current regression passes **452 native/session + 142 advanced checks**; final drawing proofs pass **51 native and 20 UI checks**, and print passes **21 output + 6 cancellation checks**. Exact v11 ZIP identity passes for **2,302 files**, and clean-extraction advanced smoke passes **1,702 checks** including mapped text and annotation management; completion smoke passes 54 checks. The first host lifecycle attempt failed when the installed Python could not execute a synced verifier script; an isolated byte-identical verifier retry is separately recorded in BUILD-STATUS.md. The full dependency inventory and prepared physical/clean-machine checks are complete as reviews/harnesses; those acceptance gates and redistribution clearance remain open. Earlier sections retain their dated evidence.

## October 16, 2026 — latest completion-tracker follow-through

Current native/session regression passes 428 checks; advanced regression passes 142, including 20 multipage OCR checks. Direct hit geometry matches PDFium in 48 native comparisons; 16 controlled stale-target/failure UI cases pass. Direct editor preview/cancel/rejected-draft/apply/Undo/Redo/recovery/save/reopen passes at `ui-direct-edit-c4e064e0678a4a8b861ae011fd162a51`. A rejected-edit reload idle-state race is fixed. **Add drawing → Place on page** now supports selecting bounds in an unrotated rendered page preview, returning them to editable PDF coordinates before a separate commit. This is bounded shape/diagonal-line placement, not freehand pen. Nested text remains on the explicit mapped-text route. Physical pointer/device/clean-machine checks and the broader implementation scope remain open; BUILD-STATUS.md and COMPLETION-TRACKER.md retain the exact evidence and package cutoff. Older sections below describe their dated checkpoints.

The final source additionally refuses offset visible page origins for direct clicks/drawings, with 16 coordinate-safety and four UI checks; the supported-page drawing repeat passes 43 checks and the full/final-focused UI runs pass as recorded in BUILD-STATUS.md. Internal v10 (1.2.1) has verified ZIP identity for 2,301 files, 1,693 package inventory/advanced checks, 51 installed runtime checks and 15 archive-based install/upgrade/repair/launch/uninstall checks. Its first directory-source upgrade failed on a transient OneDrive entry; that path is not accepted. ZIP-based host acceptance does not complete clean-machine/device acceptance or the remaining implementation scope.

## October 15, 2026 — expanded completion scope

The user requested completion of the application, including OCR, filling existing forms, verified permanent redaction and certificate signatures. The first release is for personal/internal use. Cloud document processing is permitted only after explicit consent for the individual operation; this is not permission to upload documents automatically. The implementation and acceptance sequence is in `ADVANCED-IMPLEMENTATION-PLAN.md`. Earlier statements that advanced features are later scope describe the previous checkpoint, not the current target.

October 15 integrated checkpoint: restricted local form filling, OCR, signing and redaction now have passing product workflows; see BUILD-STATUS.md. Remaining implementation includes annotation modification/deletion and drawing tools, broader existing-text/font editing, wider form types and authoring, multipage/mixed-document OCR, broader redaction compatibility, printing and installed acceptance. The restricted first workflows do not silently remove these from the completion request. No complete-release claim is made.

## October 16, 2026 — current-source continuation

The October 16 current-source continuation passed 537 native/session/advanced checks at `worker-69d277415213481d882d23bb7d5a88f7`. Expanded OCR integration passed 20 multipage plus 13 existing OCR checks at `worker-ccc2d62036644cb4ae62fba773e579ce/ocr-integration`. The corrected drawing/print run passed 43 worker checks with independent pypdf/MuPDF verification and per-tool print-render checks at `worker-03d5ac7e699343e7b5622bb77f1073df`; markup print correction passed 31 worker checks at `worker-ab8f9adbb67a43fa95c4abc58685f492`. UI stages passed state 41, shell 69, workflow, decoder 10, print cancellation 6, form UI 25, advanced dialogs 17, annotation manager 15, and mapped text 11. Focused continuation passed DirectEditingUiProof, DrawingUiProof (9), and PdfOpenErrorUiProof; final direct-edit and drawing UI artifacts are `ui-direct-edit-9bd8f30d01e64e1b9dbc068173c497e1` and `ui-drawing-08c0ba4592154419b9c0688b3860fc38`. Direct edit was exercised by invoking the real route with test coordinates and injected consent, not physical mouse clicks. The corrected drawing dialog's 620-pixel visual repeat passed. The solution build passed with zero warnings/errors. Internal portable v9 (1.2.0) passed 1,733 exact-package inventory/smoke checks plus hidden desktop launch. The Oct 15 v8 package/checkpoint remains historical; retain narrow feature limits and do not claim complete release acceptance.

Mapped-text support is bounded: supported embedded or subset-font mappings and metrics only; Type0 CID support requires Identity-H plus ToUnicode; ambiguous/missing glyphs and widths, tagged/ActualText, encrypted/signed inputs, clipping modes, reverse/zero advance and unsupported structures are refused. The helper caps input at 128 MiB and inspection at 2,000 forms/runs, 200,000 operations, and 4,000 characters per run. This is text-run replacement with preview, not paragraph reflow, arbitrary-font editing, or secure redaction. That earlier drawing checkpoint used PDF-point coordinate fields. The latest continuation adds rendered-page bounds selection before the separate coordinate-editor commit; freehand page gestures remain unavailable. Form support remains one plain-text field/widget or one checkbox/widget at a time; radio, choices, XFA, field authoring and JavaScript remain unsupported. OCR accepts only all-eligible scan-only documents, up to 50 pages and 24 million aggregate scan pixels; mixed-content OCR remains unsupported.

The current P0 order remains: validate direct clicking with physical mouse input across zoom/rotation/scaling; continue remaining drawing/markup interactions; broaden supported text/form/redaction/print cases without weakening refusal rules; and complete installed and device release checks. Current multipage OCR transactional undo/recovery/save/reopen is verified within the reported 20+13 run, while the upper-bound 50-page/three-minute workload remains a performance limit rather than a completion promise. Physical Windows interaction/accessibility/printing, clean-machine install lifecycle, licensing/redistribution review and branding review remain release gates. The new evidence does not complete the app or remove previously requested scope.

Complete the existing core workflows and release acceptance alongside these additions. A planned feature, engine proof or read-only inspection backend must not be described as a finished editor workflow. Existing original-preservation, signed-copy and saved-output gates still apply. The user chose free dependencies only, English OCR and text/vector-preserving redaction; an image-only flattened redaction workflow is not acceptable for this target.

Status updated October 13, 2026. Historical milestone records remain as written. For the current integrated source, use BUILD-STATUS.md and CAPABILITIES.md: selective page import and independently checked extraction/split, plus tested AcroForm widget appearance rendering, are now integrated. The v6 portable package has fresh worker/UI proof, packaged worker smoke and notice inventory verified; packaged desktop UI smoke, clean-machine/device acceptance, broader P0 implementation and redistribution review remain open.

## Milestone 1 - Prove the PDF engine

**Status: Engine proof acceptance gate met for the documented supported cases.**

The existing basic, image, annotation, page, font, rotation and shared-image checks pass. The old single-text-form replacement was removed. The replacement adapter preserves nested form hierarchies and isolates the selected invocation of shared forms. The expanded suite passes 28 cases plus overwrite/cleanup checks; independent Poppler renders match expected output exactly for the ten graphics cases. See `NESTED-REPLACEMENT.md` for evidence and remaining explicit rejections. Direct PDFium nested setters remain a negative test; supported edits must use `Pdf.ReplaceNestedText`.

Run `./development/run-nested-checks.ps1 -OpenResults` to reproduce the combined engine and nested-edit gate. This is engine acceptance, not a finished editor or installer.

## Milestone 2 — Build the representative corpus

**Status: Complete — corpus acceptance gate met.**

Every P0 feature has a fixture, steps and concrete expected results in `fixtures/corpus-v1/scenarios.json`. All references exist. `manifest.json` records PDF hashes, sizes, page counts, expected behavior, categories, credentials, provenance and auxiliary-file hashes. Native/integrity/parser checks pass; the altered-copy negative test is rejected while the baseline stays intact. Final markup and styled-language previews were inspected; the user verified preceding corpus checkpoints. See `MILESTONE-2-COMPLETION.md` for final evidence. This is corpus readiness, not application feature acceptance.

The synthetic corpus currently contains 19 hashed fixtures. The user visually verified the corpus and initial/expanded test outputs. Existing corpus evidence: `development/artifacts/corpus-6ed02a3cfc3d4596ab5aaabe06c6a857/corpus.json`.

Additional compatibility coverage now passes: password-protected AES-256 PDFs; AcroForms, bookmarks, links, attachments, optional layers and an unsigned signature field; transparency, clipping and page boxes. Native save/reopen produces identical rendered pixels. The independent pypdf parser confirms field/widget values and appearances, bookmark, link, attachment bytes, layer and box structures remain intact after saving. Encrypted output remains encrypted and retains the password. Evidence: `development/artifacts/compatibility-6a30bd37363f4857a227d45f8cb087b9/independent-compatibility.json`. Poppler visual inspection confirms the form value displays, unlike the prototype's PDFium page-only renderer, which omits widget text.

## Next work

- [x] Establish application, core, adapter, worker and integration-test projects.
- [x] Verify worker crash/restart, cancellation, revision checks and save/reopen through the protocol.
- [x] Manually smoke-test first-page display and worker-failure recovery in the WPF checkpoint.
- [x] Milestone 4: implement document state, undo/redo, safe saving and recovery for supported commands.
- [x] Milestone 5: implement the application shell, tabs, navigation/properties panels and multi-document UX.
- [x] Milestone 6 implementation: page navigation, scrolling, fit modes, thumbnails, bookmarks, search, selection and bounded viewer raster caches.
- [x] Milestone 6 acceptance: visually verify selection alignment at different zoom levels, rotations and display scaling; check large-document navigation and cache bounds.

- [x] Resolve general nested-object editing with isolated form copies, preserved graphics state, failure-path tests and independent rendering evidence.
- [x] Add encrypted, interactive-structure, graphics and page-box fixtures.
- [x] Verify preserved structures using an independent parser and inspect rendered pages.
- [x] Add signed-certificate, ligature/missing-Unicode and licensed public fixtures.
- [x] Map every P0 feature to a fixture and expected result; validate all references.
- [x] Freeze a versioned snapshot and verify hashes, negative integrity cases and final visual layouts.

Edge checkpoint implemented and executed: mapped ligature/Unicode, absent-ToUnicode, rasterized readable text, pinned licensed public PDFium fixture and actual certificate-signed document. Native save/reopen preserves appearance; independent parser validates mappings and scan classification. pyHanko detects covered-byte tampering and native rewrite invalidation while the signed source hash stays unchanged. The throwaway certificate is trusted only in the isolated test context; no system trust or revocation claim. Evidence is consolidated in the completed snapshot. The 43 scenario definitions now complete P0 corpus coverage; future app acceptance remains untested.

## Later milestones

Milestones 3–6 pass their documented implementation and acceptance gates. WPF shell tests use actual controls, command routing and PDFs with injected dialog choices; interactive device/native-dialog verification remains a release check. Milestone 6 interaction acceptance is complete; see `MILESTONE-6-COMPLETION.md`. Milestones 7–12 remain incomplete; limited portions of text editing, combine, printing and packaging are now implemented. P1/P2 remain not started. Complete installed P0 acceptance remains open.

### October 12 integration checkpoint — combine/text backend

The worker now provides stateless `InspectCombineInput` and `Combine` operations. Combine validates ordered inputs/ranges, refuses encrypted and signed PDFs, rejects destination collisions, hashes inputs without loading each whole file into memory, caps output at 50,000 pages, saves to a unique sibling candidate, reopens and validates it, then publishes without overwrite. Cancellation terminates the worker; if final publication has already occurred, check the output path. The Combine UI and user-facing metadata-limit disclosure are integrated; bookmarks, forms, attachments, and source document metadata are not preserved.

The worker also exposes real top-level text object contents/font details and `ReplaceText` through the existing durable session checkpoint path. The initial supported set is intentionally narrow: standard PDF base fonts, printable basic ASCII, unrotated/unscaled, unclipped top-level text, with verified page bounds and no overlap. It does not cover nested text, arbitrary embedded/subset fonts, OCR, tagged/ActualText content, paragraph reflow, or redaction. The UI must refresh object IDs after a rejected edit because the session reopens its last committed checkpoint. Evidence is recorded by the current `App.Tests` worker/session/text proof; see `BUILD-STATUS.md` for integrated status. This checkpoint does not mark later milestones or installed P0 acceptance complete.


## October 13 continuation order

1. [x] Add ordered selective source-page import and independent pypdf verification for extraction/split outputs.
2. [x] Verify the pinned PDFium form-fill rendering lifecycle against the native header and prove widget appearance through viewer, print render, save and reopen; broader form fidelity remains a release limit.
3. Accept native import/save/folder dialogs, physical drag/drop, accessibility devices, 100–200% scaling and physical printer behavior.
4. Complete redistribution review, icon cleanup, clean-machine Windows 11 x64 acceptance and upgrade/repair before claiming release readiness.
This sequence resumes unfinished implementation while keeping release acceptance separate. Neither engine proofs nor a local portable ZIP complete milestones 7–12. P1/P2 remain later work. The selected display name is PDF - Paperwork Done Faster; internal identity and recovery paths remain stable.




