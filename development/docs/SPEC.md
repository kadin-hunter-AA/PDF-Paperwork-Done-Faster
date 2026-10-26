# PDF Editor proof-of-engine requirements

## Scope

Milestone 1 is a small native C#/.NET 10 harness that evaluates PDFium and its selected wrapper for the editing operations needed by the roadmap. It is an engineering proof, not the end-user application. No implementation or successful test result is asserted by this document.

## Required behavior

The harness must demonstrate, against real PDF files and persisted output:

1. Open and render a PDF.
2. Enumerate text and image objects.
3. Replace existing text with a longer string, then with characters absent from the original font subset.
4. Move, resize, replace, and delete an existing image.
5. Add and modify a highlight and an ink annotation.
6. Reorder and merge pages.
7. Save to a new PDF, close all engine handles, reopen the output, and inspect it in an independent viewer.

## Acceptance

For every operation, saved and reopened output must retain the requested change and leave unrelated content intact. Record supported and unsupported cases, including observed errors and reproduction details. Cover embedded and subset fonts, rotated pages, shared images, and content inside reusable PDF objects. A capability is not considered proven by an in-memory preview, mock, API declaration, or successful build alone.

## Boundaries

- Existing text editing means modifying the selected text object; a white cover plus replacement text does not qualify.
- This milestone does not implement a full UI, worker process, undo/redo, installer, OCR, or production recovery flow.
- Do not begin later product milestones until this acceptance gate is met.
- All `sources/` files are read-only references.
