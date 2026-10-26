# Release acceptance harness

The final v11 disposable **host** archive lifecycle passes **16 checks**, including fresh v10 install, v11 upgrade, repair, exact inventory, shortcut/hidden launch, 54 repaired-installed-runtime checks and ownership-preserving uninstall/cleanup. Evidence: `artifacts/package-lifecycle-20261016-v11-zip-r2/`. The first attempt's synced-verifier access failure is retained; the accepted retry executes a hash-verified copy of the verifier in its disposable local root. This result does not close the clean-machine or physical steps below.

This checklist covers the internal v11 package and preserves the preceding v10 audit. The exact v11 archive `artifacts/portable-win-x64-20261016-internal-v11.zip` has SHA-256 `d90cc5c5802de27c4b8efe06c578b1d94a9d6c7e0be1348d247e8fb327cae6ac` (155,878,930 bytes). V10 is the earlier internal checkpoint (`8e5709a765dd8c0882be16e54231c93a634c51e2b1a2ca8cbc70d162a29b3763`; 155,862,603 bytes). V11's package and source/test evidence remain checkpoint evidence; they do not establish clean-machine UI, installed-shell or physical printer acceptance. Re-audit any package made from later source changes.

## Host capability snapshot (October 16, 2026)

Read-only inspection found Windows 11 Business x64, version `10.0.26300` / build `26300`; the Hyper-V VM Management service (`vmms`) was running and the hypervisor was present. Three printers are registered: a Kyocera CS 3051ci KX network queue, Microsoft Print to PDF, and OneNote. Queue registration does not establish current reachability or that a physical device is available. The Windows optional-feature query requires elevation and therefore returned no feature states; Windows Sandbox availability remains undetermined. The user reports no clean VM or printer available for acceptance. No VM was created and no print job was sent.

Use `tools/Inspect-ReleaseHost.ps1` to create a full host/package file-hash snapshot:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\Inspect-ReleaseHost.ps1 `
  -PackageDirectory .\artifacts\<exact-release-folder> `
  -EvidencePath .\artifacts\<run-id>\release-host-evidence.json
```

Run it from `development/`. It is read-only against the package. Evidence is create-only and the script rejects evidence paths inside the package tree. Preserve the JSON with test evidence. It records host state and hashes every package file; it does not test the application.

To inventory the exact v11 ZIP, extract it into a fresh temporary folder outside the development tree, then run both evidence scripts. Example:

```powershell
$run = Join-Path $env:TEMP ('pdf-editor-v11-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $run 'package'
New-Item -ItemType Directory -Path $payload | Out-Null
Expand-Archive -LiteralPath .\artifacts\portable-win-x64-20261016-internal-v11.zip -DestinationPath $payload
& .\tools\Inspect-ReleaseHost.ps1 -PackageDirectory $payload `
  -EvidencePath (Join-Path $run 'release-host-evidence.json')
Get-FileHash .\artifacts\portable-win-x64-20261016-internal-v11.zip -Algorithm SHA256
```

Generate the dependency/notice manifest from that extracted copy with the bundled Python standard library:

```powershell
& .\tools\advanced-python-3.14.8\python.exe -I -B .\tools\audit-package-licenses.py `
  --package-root $payload `
  --archive .\artifacts\portable-win-x64-20261016-internal-v11.zip `
  --output (Join-Path $run 'license-manifest.json')
```

The manifest ties staged wheel `METADATA` and `RECORD` rows to files, hashes license/notice files and recorded native binaries, checks wheel RECORD hashes, inventories .NET manifests/notices, Python identity, PDFium and OCR assets, and explicitly reports unresolved native owners. It fails for an archive checksum mismatch or corrupt ZIP. It is an inventory aid; it does not assess applicability or compliance.

V10's manifest is preserved in `artifacts/release-acceptance-20261016-v10/license-manifest.json`. V11's manifest is in `artifacts/release-acceptance-20261016-v11/license-manifest.json`; results for both are recorded below. Both packages carry 19 Python distributions and 15 owned native executable/library files, with all present RECORD hashes matching and no unowned binaries under `advanced/packages`. Each reports 592 absent `__pycache__` entries, four console-script rows outside `packages/` and three absent PyMuPDF `fitz` compatibility alias source files. Current app helper source imports `pymupdf`, not `fitz`. Review these exclusions if the runtime changes. In both packages, the PyMuPDF `COPYING` is a 64-byte license identifier string, not the full AGPL text; see `REDISTRIBUTION-REVIEW.md` for this blocker.

Compare the calculated ZIP hash to the adjacent `.zip.sha256`. Keep evidence JSON outside the extracted package. The v10 full inventory hashed 2,301 extracted files; its evidence JSON SHA-256 is `44dbcb69c59c8f991f6bd4269c16d58d2aa8ce2ce9eba02d7d8d65924cd28254`. V11's full inventory hashed 2,302 extracted files; its evidence JSON SHA-256 is `1b3a44d98bbacf4decdfebef76203ed3b731f84f9e2323acdb2abd672439d72b`. V11's ZIP sidecar matches `d90cc5c5802de27c4b8efe06c578b1d94a9d6c7e0be1348d247e8fb327cae6ac`, its ZIP contains 2,308 entries, and the integrity check found no corrupt entry. Its license manifest SHA-256 is `8eed7cda0894e5a2ba180080ef2fef715170c86bb42d5b44e2e601f7de804604`. These inventories describe package bytes; they do not test the application.

## Required clean-machine run

Use a disposable Windows 11 x64 VM with a fresh local standard-user account. Record VM image/build, VM configuration, package ZIP SHA-256, and tester/date. Ensure the VM has no .NET SDK, Python, PyMuPDF, Tesseract or development tools installed. Copy the ZIP into the VM through a controlled shared folder or mounted ISO, then disconnect networking for the offline pass. The user reports no clean VM is available, so these steps are pending.

1. Verify the ZIP against its adjacent `.zip.sha256`; extract it to a path containing spaces and non-ASCII characters. Confirm all files extract and `licenses/`, `advanced/`, `worker/`, and root documentation are present.
2. From the extracted package folder, run `Install-PerUser.ps1 -PackageArchive ..\portable-win-x64-20261016-internal-v11.zip` in an ordinary PowerShell session. This uses the checksummed archive path. Confirm it creates the current-user app folder and Start Menu shortcut without elevation or file association changes. Launch from the shortcut; verify window title, product icon, main window, and first-run state.
3. Open a supplied non-sensitive test PDF. Verify page navigation, zoom, rotation, search, save/reopen, and one supported existing-text edit. Confirm unsupported toolbar/actions are not presented as working features. Keep the source PDF hash and compare after edit/cancel flows; only an explicit save should create changed output.
4. Exercise one available form-fill, OCR, signing, redaction and annotation workflow only when the corresponding command is visibly available in this build. For redaction, independently search/extract the output and verify removed text is absent. For signing, verify integrity separately from trust. Do not treat test certificates as trusted signatures.
5. Test cancellation and failure paths: cancel an open/save dialog, try a read-only destination, and open a deliberately malformed copy. Confirm the original source remains byte-identical and that errors are shown without corrupting the working document.
6. **Physical printing is pending on a real printer.** Before submitting jobs, use print preview and a test page with no sensitive content. Record printer model/driver, PDF hash, app version, settings, and observed sheets/pages. Run each row of this matrix and inspect the paper output, not only the dialog:

   | Case | Document content | Settings to check | Observe on paper |
   |---|---|---|---|
   | Text | Text-only page with headings, small text and Unicode | Single page, portrait, 100% | Missing/clipped text, glyph substitution, margins, legibility |
   | Image | Raster/photo and vector shapes | Single page, landscape, fit to printable area | Rotation, crop, scale, color/grayscale behavior, image sharpness |
   | Markup | Page with each supported visible annotation/markup type | Portrait and landscape; fit and actual size | Whether markup appears, position, opacity and clipping |
   | Form | Filled fields plus checkbox/radio selections | Page range covering form page | Values, field appearance, widget clipping and page order |
   | Range and copies | Multi-page test document with labeled page numbers | Range first/last/subrange, reverse order if exposed, copies if exposed | Correct pages, ordering, count and no duplicates |
   | Cancellation | Any test document | Open print dialog, then Cancel | No pages emitted; document remains open and unchanged |

   For every supported orientation/scaling choice, test portrait/landscape and actual size/fit. Record unexposed options as unavailable. Capture the dialog and observe actual printed output. Do not count a generated preview, PDF printer output or an accepted spool request as hardware acceptance. This host has a registered network Kyocera queue, but the user reports no printer available for testing; no print job has been sent and no physical output is accepted.
7. Reboot the VM offline and relaunch from Start Menu. Confirm the app and bundled worker/Python functions still run with no global runtime installed.
8. Run `Uninstall.ps1` from the installed program folder. Confirm the shortcut and package-owned install files are removed while user documents/settings and unrelated files remain intact.
9. Reconnect the VM to networking only if update/network behavior is in scope. Capture installed file hashes and test outputs before destroying the VM.

## Evidence bundle and release gate

Keep the ZIP SHA-256, `release-host-evidence.json`, VM image/build, screenshots for visible UI and print dialog, step-by-step pass/fail notes, source/output hashes for document-preservation checks, and install/uninstall residue findings together under a unique run folder. Do not include customer PDFs or secrets.

Acceptance remains blocked until every applicable step has an observed result on a clean VM, all failures are resolved or explicitly accepted by the release owner, and the redistribution blockers in `REDISTRIBUTION-REVIEW.md` are resolved. This harness intentionally does not send print jobs or grant legal clearance.
