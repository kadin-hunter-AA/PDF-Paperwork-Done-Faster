# Structural redaction proof (development only)

This isolated proof exercises a narrow PyMuPDF redaction path against generated synthetic PDFs. It is not product code, does not enable a user-facing feature, and does not establish general-purpose or production redaction safety. The script writes only to a new `development/artifacts/redaction-proof-*` directory; it does not modify fixtures or its input PDFs.

Run `proof.py` with the project's configured Python runtime and its existing local verification dependencies available under `development/tools/python-packages`. PyMuPDF 1.28.2 is staged under `development/tools/redaction-pymupdf`; `proof.py` adds both local dependency directories to `sys.path`. No global package installation is needed. A successful run prints the number of checks and artifact directory; inspect that run's `results.json` for the detailed outcomes.

The proof accepts only unencrypted, untagged PDFs without signatures/permissions, forms or widgets, optional-content groups, attachments, links, existing annotations, or page Form XObjects. It rejects those structures rather than claiming they are safely redacted. Its generated cases exercise visible and invisible OCR-like text, partial image-region redaction, a partially intersecting vector path, rotated/cropped pages, and nested/shared form refusal. The redaction mode removes any vector path touched by the region as a whole, which can remove a larger path than the selected area. Standard document metadata and XMP are cleared on every output. It checks output with PyMuPDF and independent pypdf object/stream inspection, plus Poppler rendering. Its checks are finite adversarial examples, not an exhaustive security audit.

The save path applies native redaction and writes a fresh, non-incremental, garbage-collected output while retaining unrelated page text and vector content. It does not flatten pages. Publication uses an atomic create-only operation; an output path that appears during processing is preserved. The proof checks source bytes again before publication, honors cancellation before editing and publication, and bounds region count and total area. This proof's options and subset are deliberately narrow; no compatibility or safety claim should be inferred for arbitrary PDFs.

## Dependency and license boundary

The staged binary is the official PyPI Windows x64 CPython abi3 wheel for PyMuPDF 1.28.2. `requirements.lock` records the wheel filename and SHA-256. The wheel's `COPYING` file identifies dual licensing; it is a short license notice, not the full AGPL text. PyMuPDF/MuPDF is offered under AGPL-3.0-or-later or a commercial Artifex license. This internal prototype has not received legal clearance; AGPL obligations, including consequences of distribution or network use, require review before any integration or distribution. No commercial license is included or implied.

## References

- [PyMuPDF Page redaction API](https://pymupdf.readthedocs.io/en/latest/page.html#Page.apply_redactions)
- [PyMuPDF license information](https://pymupdf.readthedocs.io/en/latest/about.html#license-and-copyright)
- [Official PyPI release files and hashes](https://pypi.org/project/PyMuPDF/1.28.2/)
