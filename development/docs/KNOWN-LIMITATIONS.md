# Known limitations

Updated October 15, 2026. The desktop runtime uses .NET 10 and the pinned PDFium Windows x64 chromium/8086 library through direct C# interop. Current source additionally bundles an isolated Python 3.14.8 companion for restricted form filling, English OCR, certificate signatures and structural redaction. Historical v6 packages do not contain it. See CAPABILITIES.md for each command's exact subset. The historical nested-text adapter is still not a product workflow.

## October 22 roadmap additions — limits

- Visual signatures are images. They are not digital signatures and prove nothing cryptographically.
- The Fields panel never runs form JavaScript or calculations; XFA forms are refused; signature and button fields are not editable there.
- FreeText text boxes support WinAnsi characters only; use Add text with an installed font for other scripts. Kerning and ligatures are not applied to embedded-font text. Missing-glyph fallback for existing text and paragraph reflow are not implemented.
- Vector (EMF) printing draws text as outlines and may omit form widget values; pages with form fields default to image output. No physical printer has been tested.
- OCR orientation uses trial recognition, not Tesseract OSD; sideways scans get a correctly oriented text layer but the page itself is not rotated. Deskew handles up to 5 degrees.
- Undo history keeps at most 50 steps by default; older steps are merged into the opened version.
- Watermark, header/footer and Bates text are Helvetica, Latin-1 only. Compare two PDFs is not implemented.
- The coordinate-entry drawing dialog and the image editor window still refuse rotated pages; the on-page tools support them.

## Nested text

The general nesting/persistence limitation is resolved by the occurrence-based structural adapter. Multi-object forms, deep hierarchies, shared instances, inherited state, clipping and transparency are covered by the passing regression suite. Direct PDFium nested text setters still fail to persist and must not be used as the application's nested edit path. See `NESTED-REPLACEMENT.md` for the API, evidence and precise supported boundaries.

Nested editing explicitly rejects absent/ambiguous glyphs, unsupported encodings or metrics, Type3 and vertical fonts, text used as clipping, tagged/ActualText content, encrypted files and populated signature fields. A separate workflow is needed for each of those cases. These rejections publish no edited PDF and leave the source unchanged. This is not redaction or paragraph reflow.

## Fonts and rendering

Direct edits cannot add absent characters to the tested subset. Replacing the actual top-level text object with a newly embedded full font works for the tested Greek/Cyrillic case. Nested re-embedding is not yet implemented; existing mapped glyphs are supported.

The worker uses the pinned PDFium form-fill lifecycle for AcroForm page rendering. Regression proof with the frozen interactive fixture confirms its sample widget value appears in both viewer and raster-print output before and after ordinary Save As/reopen; independent pypdf checks the saved value and unsigned signature field, and source hashes remain unchanged. This covers that fixture and widget appearance path only. Form authoring, XFA, broader field types and general viewer/print fidelity are not accepted features; unsupported form types are refused where the worker requires it. Physical print fidelity remains open.

## Application scope

The multi-document WPF shell and Milestone 6 viewer are complete in the development checkpoint: tabs, recent files, collapsible/resizable panels, themes, page navigation, scrolling, thumbnails, bookmarks, search, selection and bounded rendering caches. Native dialog/physical drag/screen-reader and actual Windows high-contrast device checks remain release work; offscreen tests use injected dialog choices and a high-contrast signal.

Snapshot-based undo/redo, validated saving with backups and recovery preserve committed edits across worker loss. Uncommitted text-entry drafts are per-tab UI state and are not included in recovery. One worker remains per open document. Existing text replacement currently supports only top-level text objects using the standard PDF base fonts, printable basic ASCII, and an unrotated/unscaled, unclipped text matrix. The operation rejects page overflow and bounding-box overlap; it does not handle nested text, arbitrary/subset fonts, OCR, tagged or ActualText semantics, or paragraph reflow. Text in a graphics clipping context that is not exposed on the object itself may not be detectable, so this is limited prototype support and not a claim of general safe editing. Edits still require signed-copy consent and use durable save/reopen checkpoints. Object IDs expire when the worker document revision changes or the session reloads.

The annotation subset creates highlight, underline, strikeout and sticky-note annotations from page-space rectangles. Sticky notes support Unicode/multiline contents and a current-page reader; oversized or aggregate-limited reads are explicitly marked unavailable. The manager updates a supported note comment or deletes one unlocked note/highlight/underline/strikeout with undo/recovery. Locked/read-only/content-locked annotations, links and widgets are protected; document-wide popup/reply detection refuses linked annotation graphs. Lists show the first 1,000 annotations, management scans at most 1,000 pages/10,000 annotations, and replacement comments are limited to 4,000 characters. Creation on rotated pages, movement/restyling, pen strokes and shapes remain incomplete. Permanent redaction is a separate restricted preview/new-copy workflow; annotation overlays do not redact content.

Viewer/print rendering and form-inspection initialization use isolated native document copies because PDFium can generate appearances and normalize note rectangles during reads. Saved snapshots and editable state remain separate. Uncommitted native text previews may require an in-memory clone, currently bounded to 128 MiB; regular committed-document rendering reopens the saved snapshot instead. V7 predates this isolation fix and remains historical. See BUILD-STATUS.md for current-source regression evidence.

The worker offers a combine backend with page ranges, source hash checks, signed/encrypted input refusal, a 50,000-page ceiling, same-folder temporary output validation, and no-overwrite publication. The dedicated Combine UI is integrated. Bookmarks, forms, attachments, signatures, and source metadata are not preserved; signed inputs are rejected. Killing the worker during native combine prevents publication but may leave its uniquely named hidden temporary candidate in the output folder. Inspect and remove that candidate only after confirming it is not in active use. Page organization supports move, blank insertion, safe page duplication, single-page deletion (the final page is protected), and ordered range import such as `1,3-5` (blank imports all pages) from an eligible separate PDF after the selected page. Import is capped at 5,000 source pages per action and 50,000 result pages. Import sources with encryption, signatures, forms, tags, bookmarks, named destinations, attachments, custom labels, annotations, or marked content are refused. Extraction writes selected pages to one new PDF in the requested order (up to 5,000 pages). Split creates one PDF per source page in a new folder (up to 50,000). Independent pypdf checks reopen extraction/split output and compare order, text, page boxes, rotation, filenames and source hash. Both reject encrypted sources and documents with forms, bookmarks, named destinations, attachments, tagged structures, custom labels, annotations, or marked content, validate output page count and geometry before publication, never replace existing output, and leave the open source unchanged. Physical dialog interaction is still unverified. Navigation preflight/postflight and serialized candidate validation conservatively reject targets that cannot be safely remapped; custom page labels are refused.

The installer and installed P0 acceptance remain incomplete. Snapshot history has no quota, and power-loss/cloud-sync/network-filesystem guarantees remain untested. A competing rename can race the final destination-hash check; backups retain replaced versions. See MILESTONE-4-COMPLETION.md, MILESTONE-5-COMPLETION.md, and `BUILD-STATUS.md` for the current integrated gate. Release dependency/licensing review remains open.



