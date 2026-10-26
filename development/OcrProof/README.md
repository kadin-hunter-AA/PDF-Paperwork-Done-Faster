# Local OCR proof

The proof uses PyMuPDF/MuPDF's native OCR API with the pinned English traineddata file and no Tesseract executable. It runs only on the frozen one-page, image-only reference scan. The transform is intentionally narrow: one opaque, full-page RGB or grayscale image at a clean integer native DPI; no vectors, text, forms, annotations, links, attachments, signatures, layers, tags, XMP, or non-default page structures. The source's PDF Info dictionary is copied exactly, and the original scan's decoded image pixels are embedded without resampling.

Run the Windows-only acceptance harness from the workspace root:

```powershell
development/tools/advanced-python-3.14.8/python.exe -I development/OcrProof/test_ocr.py
```

It validates extraction and geometry with pypdf, checks MuPDF text spans use invisible text rendering mode, compares PDFium render pixels from the input and candidate, and tests rejection, no-overwrite, source identity, cancellation, invalid-input, metadata, and raster-size behavior. The reusable app-side transform is in `development/App.Advanced/ocr.py`; it creates only a private candidate file and leaves commit, undo, recovery, and user-facing output to the existing document session. Product packaging and PyMuPDF licensing review are still release gates; this proof does not connect a UI command.

The English model came from upstream `tesseract-ocr/tessdata_fast` commit `87416418657359cb625c412a48b6e1d6d41c29bd`. SHA-256 is `7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2` (4,113,088 bytes). Upstream identifies the traineddata as Apache-2.0 licensed in its [LICENSE](https://github.com/tesseract-ocr/tessdata_fast/blob/87416418657359cb625c412a48b6e1d6d41c29bd/LICENSE).

On the frozen fixture, OCR recognized `Synthetic scanned text`, `Invoice number 12345`, `Total amount 678.90`, and `No hidden searchable text`, with 13 word boxes. The source and output are 612 x 792 points with the same 1224 x 1584 decoded image pixels at native 144 dpi. PDFium produced identical page pixels from both files; pypdf independently extracted the text and confirmed page dimensions and Info metadata. The saved evidence is in `development/artifacts/ocr-validation-f6ac3d6f-67b3-4d15-98ab-8082265e2434/` (`result.json`, `recognized.txt`, `word-boxes.json`, `candidate.pdf`).

This proof does not establish accuracy across general scans, performance for larger batches, password-protected or signed-file support, mixed/digital-content handling, cross-platform runtime packaging, or release licensing clearance. Unsupported PDFs are refused without writing a candidate. OCR can be canceled before candidate acceptance; cancellation during the native OCR call depends on the outer companion-process cancellation and must be exercised in the application session gate before release.
