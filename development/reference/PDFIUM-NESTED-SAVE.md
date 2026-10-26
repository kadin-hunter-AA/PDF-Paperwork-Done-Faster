# PDFium nested save investigation

Pinned upstream: refs/heads/chromium/8086, corresponding to native/VERSION. Retrieved October 12, 2026. Runtime negative reproduction: ComplexProof.Run, complex-direct-edit.pdf.

- [ProcessForm](https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8086/core/fpdfapi/edit/cpdf_pagecontentgenerator.cpp) regenerates a form only when its holder HasDirtyStreams returns true.
- [Page object holder](https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8086/core/fpdfapi/page/cpdf_pageobjectholder.cpp) marks content streams dirty during object removal.
- [Form removal API](https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8086/fpdfsdk/fpdf_editpage.cpp) marks the parent object dirty as well, unlike an ordinary nested text setter.
- [pypdf 6.1.3 content objects](https://pypdf.readthedocs.io/en/6.1.3/modules/generic.html) document the stream/object interfaces used by the structural adapter. Font-map helpers are private and version-pinned.

The implementation does not remove dummy objects or depend on native memory layout. See ../docs/NESTED-REPLACEMENT.md.
