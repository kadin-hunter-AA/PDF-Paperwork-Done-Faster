# PDF - Paperwork Done Faster

A Windows 11 x64 desktop PDF viewer and editor (WPF, PDFium) that keeps your original files safe. Every change is an undoable checkpoint, edits run in an isolated worker process, recovery survives crashes, and output is written create-only so nothing is silently overwritten.

## What it does

- **View:** tabs, continuous pages, thumbnails, bookmarks, search, selectable text, fit and zoom (Ctrl+wheel, Ctrl+Plus/Minus, Ctrl+0, Ctrl+1), keyboard page navigation and Go to page (Ctrl+G).
- **Edit on the page:** retype existing text, add text in any embeddable installed font, editable text boxes, highlight/underline/strike out, sticky notes, pen, line, arrow, rectangle and ellipse, including on rotated pages.
- **Fill & Sign:** form fields panel, visual signatures (drawn, typed or image), initials, date and check stamps, flatten to a new copy, and certificate (.pfx) signing as a separate command.
- **Pages:** rotate, move, duplicate, delete, insert blank, import from another PDF, extract and split, combine several PDFs.
- **Quick tools:** compress, password protect or remove a password, watermark, header/footer and page numbers, Bates numbering, crop, export pages as images, extract images, document properties.
- **Other:** local OCR for scanned pages (English bundled; other languages downloaded only with your consent), restricted permanent redaction with review, printing with no page limit.

Each tool works on a documented supported subset and refuses what it cannot do safely. See [capabilities](development/docs/CAPABILITIES.md), [known limitations](development/docs/KNOWN-LIMITATIONS.md) and [build status](development/docs/BUILD-STATUS.md) for exactly what is verified.

## Download and install

No public release has been published yet. Releases will be a portable ZIP with a `SHA256SUMS` file. Extract it anywhere outside a synced folder and run `App.Desktop.exe`, or run `Install-PerUser.ps1` from the package for a per-user Start menu install (add `-AssociatePdf` to list the app in Explorer's Open with menu for PDFs). Windows 11 x64 is required.

## Build from source

Requires Windows 11 x64 and PowerShell 7. From the repository root:

```powershell
./development/tools/setup.ps1                  # pinned .NET SDK 10.0.401 and PDFium (SHA-256 checked)
& ./development/tools/dotnet/dotnet.exe build ./development/HunterPdf.slnx --nologo
& ./development/tools/dotnet/dotnet.exe run --no-build --project ./development/App.Tests -- ./development
./development/tools/run-ui-checks.ps1
./development/run-app.ps1
```

The optional companion features (mapped text, form filling, OCR, signing, redaction, quick tools) need the isolated Python runtime: see [development/README.md](development/README.md) and `development/tools/Setup-AdvancedPython.ps1`. To produce a self-contained package, run `./development/tools/Publish-Portable.ps1 -OutputDirectory <new-directory>`.

## Project layout

| Path | Contents |
|---|---|
| `development/App.Desktop` | WPF shell |
| `development/App.Core` | Document session, checkpoints, recovery, worker clients |
| `development/App.Worker`, `App.Pdfium` | Isolated PDFium worker |
| `development/App.Advanced` | Python companion (pypdf, PyMuPDF, pyHanko, Tesseract) |
| `development/App.Tests`, `App.UiTests` | Regression proofs |
| `development/fixtures` | Frozen test corpus |
| `design/` | Windows 11 design rules, brand tokens, icon artwork |
| `development/docs` | Specification, status, evidence and release notes |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and [ROADMAP.md](ROADMAP.md).

## License

[GNU Affero General Public License v3.0](LICENSE). Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](development/docs/THIRD-PARTY-NOTICES.md) and [ADVANCED-RUNTIME-NOTICES.md](development/docs/ADVANCED-RUNTIME-NOTICES.md). PyMuPDF is used under the AGPL.
