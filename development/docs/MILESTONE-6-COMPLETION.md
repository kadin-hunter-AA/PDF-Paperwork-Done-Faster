# Milestone 6 — viewer implementation and acceptance

Implementation and interaction acceptance completed October 12, 2026. The full six-project solution builds with zero warnings and errors. The regression suite passed 184 checks: 17 worker, 70 session, 40 desktop-state, and 57 shell checks. Manual WPF checks used the 500-page corpus fixture and covered page display, navigation, zoom, selection, rotation, display scaling, worker recovery, and viewer cache limits.

## Delivered

- Page number entry and navigation, continuous vertical page layout, current-page status, and per-tab position and zoom.
- Fit width and fit page controls, plus page-aware zoom using PDF points and WPF device-independent units.
- Thumbnail and bookmark navigation. The worker reads page sizes and internal bookmark destinations from PDFium.
- Search across extracted page text, including repeated Find navigation and page-level match highlighting.
- Text selection from PDFium character bounds. Dragging paints a selection overlay over the rendered page; Ctrl+C copies selected text.
- Visible-page-first rendering. Full page rasters are limited to 4096 pixels per side and 7.9 million pixels, with an eight-page LRU cache. Neighboring pages are prefetched. Render requests are canceled when the viewport changes.
- Small thumbnail renders are generated asynchronously for the current page neighborhood, and the thumbnail bitmap cache is limited to 32 pages.

## Acceptance evidence

- Opened the 500-page corpus PDF in the WPF app and visually confirmed the first page. Navigated to page 500, used Fit Width, and zoomed while verifying the current page stayed at 500. Verified fit/zoom selection alignment at multiple zoom levels.
- Drag-selected page text and visually checked that the highlight follows the glyphs at rotations 0°, 90°, 180°, and 270°. At 90° and 270°, the selection was also checked at changed zoom levels. Display scaling was checked at Windows 100% and 125%; the first-page text highlight remained aligned at both. Restored Windows display scaling to its original 100% setting after the check.
- Terminated the document worker during the earlier WPF smoke check. The app reported the stopped worker; retrying navigation restarted it and rendered page 250 of 500.
- Checked large-document viewer cache limits in the implementation: at most eight full-page rasters and 32 thumbnail bitmaps are retained. Visible pages render before neighbors are prefetched.
- After the final rotation and first-page selection fixes, the solution built with zero warnings and errors and all 184 regression checks passed: 17 worker, 70 session, 40 desktop-state, and 57 shell checks.

The worker caps detailed character geometry at 10,000 characters per page; longer pages remain searchable, while selection geometry is available for the first 10,000 characters.
