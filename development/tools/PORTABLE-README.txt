PDF - Paperwork Done Faster — portable Windows x64 build

Requirements: Windows 11 x64. This package contains the self-contained desktop app and worker, PDFium, PDFium notices, and the bundled .NET runtime notices. The .NET notices come from the SDK's full ThirdPartyNotices file and can include components not used by this app.

Current development builds also contain an isolated local Python companion for restricted form filling, English OCR, certificate signatures and structural redaction. See CAPABILITIES.md for exact supported cases and limits. No document-processing cloud service is configured. Python and library notices remain with the companion under advanced/; English traineddata attribution is under advanced/tessdata/. See ADVANCED-RUNTIME-NOTICES.md. This internal checkpoint has not cleared redistribution review.

To install for the current Windows user, extract the ZIP, then run Install-PerUser.ps1. It installs under LocalAppData\Programs\PDF - Paperwork Done Faster and creates a matching Start menu shortcut. It does not register PDF file associations or require administrator rights.

To upgrade an install created by the current schema-2 installer, provide a newer package and run Install-PerUser.ps1 -Upgrade -PackageVersion <numeric-version>. To repair missing files, use the same package version and Install-PerUser.ps1 -Repair -PackageVersion <numeric-version>. Both operations verify the install inventory and file hashes. Modified, legacy, or unexpected install contents are refused without being overwritten. Full instructions and limits are in PER-USER-INSTALL.md.

To remove the per-user program, run Uninstall.ps1 inside the installed program folder, or remove the extracted portable folder. Uninstall removes the installed program and Start menu shortcut; it preserves user data and PDF file associations.

This is a portable ZIP handoff with optional PowerShell install/uninstall/upgrade/repair scripts. It is not an MSIX package or a compiled installer, and it is not a commercial-release claim. Review REDISTRIBUTION-REVIEW.md before redistribution; it records unresolved license and notice checks. Clean-machine installation and installed shell acceptance are separate checks. Icon export review is recorded with the project build evidence; small-size lettering remains intricate.

Verify the downloaded ZIP against the adjacent .zip.sha256 file with Get-FileHash -Algorithm SHA256.
