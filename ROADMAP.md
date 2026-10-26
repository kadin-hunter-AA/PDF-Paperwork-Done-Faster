# Roadmap

Generated from [updates.md](updates.md) (gap analysis, October 22, 2026). Status is kept current in [development/docs/BUILD-STATUS.md](development/docs/BUILD-STATUS.md).

Guiding policy: verified operations keep the strict path; other cases run as a **best-effort new copy** with a preview, never over the open document. Hard refusals stay only for signed documents without consent, encrypted documents without the password, and redaction.

| # | Area | Main items | Status (Oct 26) |
|---|---|---|---|
| 1 | Repo basics | AGPL-3.0 license, `.gitignore`, first commit, CI, README, contributor files | Done; CI green on GitHub Actions (Oct 26); releases and code signing open |
| 2 | Viewer | Ctrl+wheel and keyboard zoom, Home/End/PageUp/PageDown, Go to page, print/rotate/delete shortcuts, prefetching render cache | Done |
| 3 | Fill & Sign | Form fields panel, visual signatures (draw/type/image), initials/date/check stamps, flatten to new copy | Done |
| 4 | Quick tools | Compress, password protect/remove, watermark, header/footer/page numbers, Bates, bookmarks, links, crop, export images, extract images, properties, compare | Done (Compare: page-by-page text diff and pixel overlay, read-only) |
| 5 | Text tools | Editable FreeText boxes, Unicode/system fonts, missing-glyph fallback, simple reflow | FreeText and fonts done; fallback and reflow open |
| 6 | Storage | Undo history cap and compaction, per-operation worker timeouts | Done; diff-based snapshots open |
| 7 | Pages | Multi-select thumbnails, drag reorder, rotate all/range, insert at any position, relaxed import, combine with bookmarks | Done; cross-tab drag open |
| 8 | Rotated/cropped pages | Annotations, drawing and placement on rotated and offset pages | Done for on-page tools |
| 9 | Printing | PDFium GDI vector printing, no 10-page limit, scaling, duplex, N-up | Done; physical printer untested |
| 10 | OCR | Mixed documents, language packs, page-by-page progress, deskew | Done |
| 11 | Engineering | Split MainWindow, XAML dialogs, `[LibraryImport]` interop | Partly (partial classes); XAML dialogs and LibraryImport open |
| 12 | Workspace (design mock) | Hand tool, find with match case/whole word/counts, Annotations and On this page panels, panel shortcuts | Done (Oct 26) |
