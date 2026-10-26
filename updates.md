# Recommended updates — PDF - Paperwork Done Faster

Source: gap analysis of October 22, 2026. Read `AGENTS.md` first; its document-safety rules still apply to everything here. All paths are relative to `development/` unless noted.

## How this list was produced

- Evidence: the source code, the project docs (`CAPABILITIES.md`, `KNOWN-LIMITATIONS.md`, `COMPLETION-TRACKER.md`, `CODEX-REMEDIATION-PLAN.md`) and a fresh build. The build (`dotnet build HunterPdf.slnx`) passed with 0 warnings and 0 errors. The test suites were not rerun.
- Each item says whether it was **confirmed in code** or comes **from the project docs**.
- Goal: make this a practical daily replacement for a paid PDF editor while keeping the existing protections (worker isolation, checkpoint undo, recovery, create-only output).

## Guiding change: run on more PDFs instead of refusing them

Most commands refuse anything outside a narrow supported subset. That is the main reason the app does not yet feel like a working editor. Recommended policy:

- **Tier 1 (verified):** keep the current strict path for cases that are fully proven.
- **Tier 2 (best effort):** for other cases, allow the operation, show a rendered before/after preview, and always write the result to a **new copy**. Never write over the open document in this tier. Label it clearly, for example "Best effort: review the result."
- Keep hard refusals only for real integrity risks: signed documents without consent, encrypted documents without the password, and anything described as redaction.

The create-only output and recovery design already protects originals, so a best-effort tier keeps those protections.

---

## Priority 1 — Fill & Sign (the main subscription use case)

### 1.1 Form-fields panel
- **Now (from docs):** one text field or one checkbox per action, standard Type1 fonts only. Refused: radio groups, choice fields, multiple widgets, multiline and calculated fields, custom fonts.
- **Do:**
  - Add a side panel that lists **every** field in the document, grouped by page.
  - Support editing text, multiline text, checkboxes, radio groups and combo/list boxes.
  - Clicking a field on the page focuses its entry in the panel.
  - Use the PDFium form-fill environment that is already initialized for rendering (`App.Pdfium/EngineForms.cs`, `EngineFormInspection.cs`): `FORM_OnLButtonDown`, `FORM_ReplaceSelection`, `FORM_SetIndexSelected`, `FPDFAnnot_SetFormFieldFlags`. Alternatively use pypdf `PdfWriter.update_page_form_field_values(..., auto_regenerate=True)` in `App.Advanced/forms.py`.
  - Set `NeedAppearances` or regenerate appearances so values display in other viewers.
- **Keep:** refuse XFA-only forms; ask for consent before changing a signed document.
- **Verify:** fill, then save/reopen in PDFium and pypdf, then check the value shows in the viewer and in print rendering.

### 1.2 Visual signatures (drawn, typed or image)
- **Now (from docs):** certificate (.pfx) signing only. Confirmed in code: `SigningWindow.cs` has no drawn-signature path.
- **Do:**
  - Add a **Signature** tool with three sources: draw (reuse the pen capture in `DrawingPlacementWindow.cs`), type (pick a script font, rendered to an image) or import an image (reuse `WicImageDecoder.cs`).
  - Save signatures under the user's profile as PNG or SVG, kept separate from recovery data.
  - Place the signature as an image or Stamp annotation. Optionally flatten it into page content on export.
  - Label it in the UI as **"Visual signature (not a digital signature)"**, and keep the existing certificate signing as a separate command.
- **Also:** an **Initials** variant, and quick **Date** and **✓ / ✗** stamps.

### 1.3 Flatten forms and annotations
- **Confirmed in code:** no flatten feature exists.
- **Do:** add a "Flatten to new copy" command using PDFium `FPDFPage_Flatten(page, FLAT_PRINT)` or PyMuPDF `doc.bake()`. Output goes to a new file only.

---

## Priority 2 — Real text tools

### 2.1 Editable text boxes (FreeText)
- **Now (from docs):** "Text box" is fixed 12 pt Helvetica printable ASCII page text plus a border, and cannot be edited afterwards. Add text is 18 pt Helvetica, basic Latin only.
- **Do:**
  - Create real **FreeText annotations** (`FPDFPage_CreateAnnot(page, FPDF_ANNOT_FREETEXT)`) with `/DA` font, size and color, and generate the appearance stream.
  - Make them editable later: double-click to edit, drag to move, handles to resize.
  - Add font family, size, color, bold/italic, alignment and multiline wrapping.

### 2.2 Unicode and system fonts for added text
- **Do:**
  - Use `FPDFText_LoadFont` with TrueType bytes read from `C:\Windows\Fonts`, and embed as CID/Identity-H so full Unicode works.
  - Show a font picker filtered to fonts whose license allows embedding (check the OS/2 `fsType` bit).
- **Also fix:** `EngineProof/Pdf.cs:148` declares `FPDFPageObj_NewTextObj` with `CharSet.Ansi`. That is fine for base-14 font names; use the loaded-font path for everything else.

### 2.3 Editing existing text: reflow and missing glyphs
- **Now (from docs):** only characters present in the embedded subset font are allowed; no reflow; wider text needs Ctrl+Enter.
- **Do:**
  - When a glyph is missing, offer a best-effort fallback (Tier 2 copy): replace the text object with a newly embedded matching system font. This path is already proven for top-level Greek/Cyrillic (`KNOWN-LIMITATIONS.md`).
  - Add simple paragraph reflow for runs of left-aligned lines that share one font, size and left edge.

---

## Priority 3 — Rotated and cropped pages

- **Now (from docs):** annotations, drawings, click-to-edit and placement are refused on rotated pages and pages with a nonzero CropBox/MediaBox origin.
- **Do:**
  - The page-to-device mapping is already verified against `FPDF_DeviceToPage` in 48 comparisons (`EnginePageCoordinates.cs`, `PageObjectHitTest.cs`). Apply it to annotation creation, drawing and placement instead of refusing.
  - Account for the `/Rotate` value when writing annotation rectangles and appearance matrices.
- **Verify:** for each of the 4 rotations and for an offset CropBox, create, render, save/reopen and compare bounds with pypdf.

---

## Priority 4 — Quick tools menu (missing features)

Confirmed in code: none of these exist in the app source (`App.*` `.cs`/`.py`, excluding bundled packages). All of them can run through the existing advanced helper (`App.Advanced/worker.py`) using the bundled pypdf and PyMuPDF, write create-only output, and reuse the job, cancellation and timeout plumbing in `AdvancedEngineClient.cs`.

| Tool | Suggested implementation |
|---|---|
| **Compress / optimize** | PyMuPDF `doc.save(garbage=4, deflate=True, clean=True)`, plus an optional image downsample (`page.replace_image` with a lower-resolution re-encode). Show the before/after size. |
| **Password protect** | pypdf `writer.encrypt(user_pw, owner_pw, algorithm="AES-256")` with permission checkboxes (print, copy, edit). Send passwords over stdin as the signing workflow already does. |
| **Remove password** | Open with the password, then write an unencrypted copy (the user must supply the password). |
| **Watermark** | Text or image, with opacity, rotation and page range. Use PyMuPDF `page.insert_text` / `insert_image` with `overlay` and opacity. |
| **Header / footer / page numbers** | Templates such as `Page {n} of {total}`, `{date}`, `{filename}`; choose position, font, margin and range. |
| **Bates numbering** | Prefix, start number, digit count, suffix and position; usable across a Combine batch. |
| **Bookmarks editor** | Add, rename, reorder, nest and delete; "bookmark current page". pypdf `add_outline_item` or PyMuPDF `set_toc`. |
| **Links** | Add, edit and remove URL and page links. PDFium `FPDFPage_CreateAnnot(FPDF_ANNOT_LINK)` or PyMuPDF `insert_link`. |
| **Crop pages** | Drag a crop rectangle on the page and apply it to the current page, a range or all pages (`FPDFPage_SetCropBox`). |
| **Export as images** | PNG or JPEG per page at a chosen DPI. The existing PDFium render plus `PngBitmapEncoder` is enough. |
| **Extract images** | Save the embedded images from the selected pages. |
| **Document properties** | View and edit title, author, subject and keywords; option to strip metadata. |
| **Compare two PDFs** | Text diff per page plus a pixel-difference overlay. (Lower priority.) |

---

## Priority 5 — Page organization

- **Now (from docs):** rotate applies to the current page only; delete removes one page at a time; importing pages refuses sources that have bookmarks, annotations, forms, tags, attachments or labels.
- **Do:**
  - **Multi-select thumbnails** (Ctrl/Shift-click) for rotate, delete, extract and duplicate on a selection.
  - **Drag to reorder** in the thumbnail rail, and drag pages in from another tab or from Explorer.
  - **Rotate all / rotate range.**
  - **Insert from file at any position**, not only after the selected page.
  - **Relax import refusals (Tier 2):** import page content, drop or merge bookmarks and annotations with a clear notice, and write the result as a new copy.
  - **Combine:** offer an option to keep bookmarks (pypdf `PdfWriter.append(..., import_outline=True)`).

---

## Priority 6 — Viewer and usability

Confirmed in code: the only shortcuts are Ctrl+O, S, Shift+S, Z, Y, F, C, W and Ctrl+(Shift+)Tab (`App.Desktop/MainWindow.xaml:352-363`). There is no mouse-wheel handler anywhere in `App.Desktop`.

- **Zoom:** Ctrl+mouse wheel, Ctrl+Plus/Minus, Ctrl+0 (fit page), Ctrl+1 (100%), plus touchpad pinch.
- **Navigation:** Home/End, PageUp/PageDown, Ctrl+G (go to page).
- **Missing commands:** Ctrl+P (print), Ctrl+Shift+Z (redo alias), Ctrl+R / Ctrl+Shift+R (rotate), Delete (delete the selected page or annotation), Esc (exit the current tool).
- **Render cache:** only 8 pages are cached (`MainWindow.xaml.cs:671`). Pre-render the pages next to the visible ones in the background, and size the cache by memory rather than page count.
- **Layout:** two-page and cover modes; continuous vs single page.
- **Context menus:** right-click on text (copy, highlight), on annotations (edit, delete, properties) and on thumbnails (rotate, delete, extract).
- **Tool memory:** remember the last color, width and font per tool.
- **File association:** an optional "Open PDFs with this app" item during install.

---

## Priority 7 — Printing

- **Now (confirmed in code):** raster only, `MaximumPagesPerJob = 10`, 4096-pixel maximum edge, 2,000,000 pixels per page (`App.Desktop/PrintPipeline.cs:77-80`, about 150 DPI).
- **Do:**
  - Print through PDFium's GDI path (`FPDF_RenderPage` onto a printer HDC with `FPDF_PRINTING | FPDF_ANNOT`). This keeps text and vector graphics sharp.
  - Alternatively, stream raster pages one at a time at 300 DPI to remove the 10-page limit.
  - Add page scaling (fit / actual / custom), duplex, N-up and "print current view".
  - Keep the existing cancellation proofs.

---

## Priority 8 — OCR

- **Now (from docs and code):** English only; pure-scan documents of 1–50 pages; any mixed page refuses the whole document; 3-minute limit (`AdvancedEngineClient.cs:26`).
- **Do:**
  - **Mixed documents:** skip pages that already have text, and OCR only image-only pages.
  - **Languages:** a dropdown backed by Tesseract `tessdata` packs that can be downloaded with consent.
  - **Long jobs:** process page by page with progress, and scale the timeout with page count instead of a fixed 3 minutes.
  - **Deskew/rotate:** detect the scan rotation automatically before recognition.

---

## Priority 9 — Core engineering

### 9.1 Limit undo/recovery storage (confirmed in code)
- **Now:** every edit appends a full-PDF snapshot to `History` with no limit (`App.Core/DocumentSession.cs:298-300`). A 50 MB file edited 100 times uses about 5 GB.
- **Do:**
  - Add a configurable cap, such as the last 50 steps or 2 GB per document, and prune the oldest snapshots, keeping the opened and last-saved checkpoints.
  - After a successful save, offer to compact history.
  - Show the space used in settings.
  - Later: store differences between snapshots (incremental updates) instead of full copies.

### 9.2 Worker timeout
- **Now (confirmed in code):** a 30-second request timeout kills the worker (`App.Core/WorkerClient.cs:22`). Large documents can hit this during normal operations.
- **Do:** scale the timeout per operation (render vs combine vs save) and by file size/page count, and show progress with a Cancel button for long operations.

### 9.3 Split up the main window code
- **Now:** `MainWindow.xaml.cs` is 1,241 lines and `MainWindow.PageTools.cs` is 912. The dialogs are built in C# code rather than XAML.
- **Do:** move tools into view models or services (rendering, search, tabs, tools), and move the dialogs to XAML using the brand tokens from `design/brand-tokens.json`. This is already listed as deferred in `CODEX-REMEDIATION-PLAN.md` §5.

### 9.4 Interop
- Move the P/Invokes to source-generated `[LibraryImport]` with UTF-8 string marshalling (deferred item in the remediation plan).

### 9.5 CI
- Add a GitHub Actions `windows-latest` workflow that runs the build, `App.Tests` and the UI checks on each push.
- Upload the evidence folders as build artifacts.

---

## Priority 10 — Open-source release basics

- [ ] **Choose a license.** Recommended: **AGPL-3.0**. It satisfies PyMuPDF's AGPL terms, which removes the "redistribution blocker" in `REDISTRIBUTION-REVIEW.md`. PDFium (BSD-3), pypdf (BSD-3), pyHanko (MIT) and Tesseract (Apache-2.0) are all compatible. Alternative: replace the PyMuPDF features with pypdf or pikepdf and use MIT/Apache.
- [ ] **Add a top-level `.gitignore`.** The folder is about **13 GB**. Ignore at least: `artifacts/`, `**/bin/`, `**/obj/`, `**/__pycache__/`, `development/tools/dotnet/`, `development/tools/*-packages/`, `development/native/`, `design/logos/**/*.zip`.
- [ ] **Make the first commit.** The repo currently has no commits.
- [ ] **Clean up old builds.** Remove or archive the old portable builds (v7–v12, each with vendored PyMuPDF) under `development/artifacts/` and the per-run `worker-*` evidence folders. Keep only the latest evidence in the repo, or publish it as release assets.
- [ ] **Write a user-facing `README.md`:** what it does, a screenshot, download/install, supported features, limits, license, and how to build. Move agent/process notes to `docs/`.
- [ ] **Publish releases.** A GitHub Release with the portable ZIP plus `SHA256SUMS`. Optionally publish to winget or Scoop later.
- [ ] **Add contributor files:** `CONTRIBUTING.md`, `THIRD-PARTY-NOTICES.md` (already exists; link it), issue templates, and a short `ROADMAP.md` generated from this file.
- [ ] **Code signing (optional):** use a free option such as SignPath for open-source projects to avoid SmartScreen warnings.

---

## Suggested implementation order

1. Priority 10: license, `.gitignore`, first commit and CI (protects the work, quick)
2. Priority 6: viewer shortcuts and Ctrl+wheel zoom (quick win)
3. Priority 1: Fill & Sign panel and visual signatures
4. Priority 4: quick tools (compress, password, flatten, watermark, page numbers, crop, export images)
5. Priority 2: FreeText boxes, plus Unicode and system fonts
6. Priority 9.1: undo storage cap
7. Priority 5: multi-select and drag-reorder pages
8. Priority 3: rotated and cropped page support
9. Priority 7: vector printing
10. Priority 8: OCR improvements
11. Remaining parts of Priority 9: refactor and interop

## Acceptance standard for each item

Each item reuses the project's existing checks:

- Undo/redo, recovery, and save/reopen work.
- PDFium plus an independent check (pypdf or MuPDF) confirm the result.
- The source file is unchanged after a failure or cancellation.
- The UI states honestly what is and isn't supported.

Add a new proof file per feature (as `CODEX-REMEDIATION-PLAN.md` §2 requires) and record the results in `docs/BUILD-STATUS.md`.
