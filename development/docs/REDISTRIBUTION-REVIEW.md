# Redistribution review — portable Windows x64

> **Current release gate (October 16, 2026): BLOCKED for redistribution beyond the developer's personal/internal checkpoint.** The v6 analysis below is historical. Exact v10 and v11 package payloads have now been inventoried; their separate evidence is under `artifacts/release-acceptance-20261016-v10/` and `artifacts/release-acceptance-20261016-v11/`. PyMuPDF 1.28.2 is explicitly offered under AGPL or a commercial license by its official documentation; no commercial license is documented, and both audited packages carry only a 64-byte license identifier file rather than the full AGPL text. Public or third-party distribution must wait for a documented licensing path and complete notice/source review. This review is not legal advice or legal clearance.

## October 16 current-source decision

The writable source stages Python 3.14.8, pypdf 6.1.3, pyHanko 0.31.0, cryptography 50.0.2 and pinned dependencies, PyMuPDF 1.28.2, and English OCR data. The source inventory is `tools/advanced-python-3.14.8/runtime-inventory.json`; the publish script verifies those staged hashes and performs isolated import checks before packaging. V10 and v11 are internal checkpoint packages, not public-release clearance. V11's SHA-256 is `d90cc5c5802de27c4b8efe06c578b1d94a9d6c7e0be1348d247e8fb327cae6ac`; its license and full package inventories are recorded in `artifacts/release-acceptance-20261016-v11/`.

The PyMuPDF project's official 1.28.2 documentation says PyMuPDF and MuPDF are available under AGPL and commercial license agreements and directs users who cannot meet AGPL requirements to Artifex for commercial licensing. The exact versioned `COPYING` file is AGPL-3.0. Therefore, because the bundled wheel includes PyMuPDF and MuPDF native code, distribution is blocked until the release owner/counsel chooses and documents an applicable licensing path, and completes an assessment of how the app's relationship to that code affects obligations. The presence of a `COPYING` file alone does not settle those questions. Retain the full source and notices needed to assess the applicable path; do not represent this package as commercially cleared.

The rest of the advanced dependency set is also not cleared by the earlier v6 review. Reproducible v10 and v11 manifests map wheel metadata and `RECORD` entries to staged files, hash the recorded binaries/notices, and check wheel record hashes. Both found 19 Python distributions, 15 owned native executable/library files, no unowned binary under `advanced/packages`, zero wheel RECORD hash mismatches, 592 unshipped generated cache entries, four console-script RECORD rows outside the packages tree, and three unshipped non-cache PyMuPDF compatibility alias source files (`fitz/__init__.py`, `fitz/table.py`, `fitz/utils.py`). The application source uses `pymupdf` imports; it does not currently import the `fitz` compatibility alias. These are package facts, not legal clearance. The OCR `tessdata_fast` Apache-2.0 notice is retained. Python's PSF license and .NET notices still require version-matched applicability review. No third-party or public redistribution approval is recorded.

The manifests expose an additional concrete notice gap: both audited wheels' `advanced/packages/pymupdf-1.28.2.dist-info/COPYING` files are only 64 bytes and contain “Dual Licensed - GNU AFFERO GPL 3.0 or Artifex Commercial License”; this is not the full AGPL text. The official PyMuPDF 1.28.2 repository `COPYING` source is the full license text, but neither package carries that full file within the PyMuPDF distribution. The release owner should preserve the exact upstream full license/required notices for the selected distribution path or establish that an executed commercial license and its terms provide another applicable notice package. The 64-byte marker is not a substitute for reviewing and fulfilling the chosen path.

The October 16 inventory read found 19 distributions. Their staged metadata declares the following license identifiers/strings; this is a metadata inventory, not a verification that all notices are complete or that the terms have been satisfied:

| Distribution | Version | Staged metadata license |
|---|---:|---|
| asn1crypto | 1.5.1 | MIT |
| certifi | 2026.7.22 | MPL-2.0 |
| cffi | 2.1.1 | MIT-0 |
| charset-normalizer | 3.5.2 | MIT |
| cryptography | 50.0.2 | Apache-2.0 OR BSD-3-Clause |
| idna | 3.20 | BSD-3-Clause |
| lxml | 6.1.3 | BSD-3-Clause |
| oscrypto | 1.3.0 | MIT |
| pycparser | 3.11 | BSD-3-Clause |
| pyhanko-certvalidator | 0.29.1 | MIT |
| pyHanko | 0.31.0 | MIT |
| PyMuPDF | 1.28.2 | GNU AGPL 3.0 or Artifex Commercial License |
| pypdf | 6.1.3 | BSD-3-Clause |
| PyYAML | 6.0.3 | MIT |
| requests | 2.34.2 | Apache-2.0 |
| tzdata | 2026.5 | Apache-2.0 |
| tzlocal | 5.4.4 | MIT |
| uritools | 6.1.3 | MIT |
| urllib3 | 2.8.0 | MIT |

V10 and v11 package evidence are in their matching `artifacts/release-acceptance-20261016-v10/` and `...-v11/` folders: each contains `release-host-evidence.json` with every extracted file hashed and `license-manifest.json` with 19 distributions, 15 mapped Python-package binaries, license-file hashes, runtime identities and unresolved ownership findings. Both ZIP hashes match their adjacent sidecars and CRC checks found no corrupt entries. The fixed file inventory pins package file hashes but is not itself an SBOM/license-fulfillment record. Resolve the full PyMuPDF notice gap and create a release-specific binary-to-license and native dependency map before broader distribution. Particular attention is due to PyMuPDF's native MuPDF contents, cryptography's native extension and OpenSSL linkage, PDFium's static component ownership, and the Python/.NET runtime notice sets.

### Primary sources checked October 16

- [Official PyMuPDF 1.28.2 licensing documentation](https://pymupdf.readthedocs.io/en/latest/about.html#license-and-copyright) — states AGPL and commercial license availability and identifies Artifex as the commercial licensing agent.
- [PyMuPDF 1.28.2 `COPYING`](https://github.com/pymupdf/PyMuPDF/blob/1.28.2/COPYING) — exact version tag's AGPL-3.0 license text.
- [Artifex AGPL v3 text](https://artifex.com/licensing/gnu-agpl-v3) — primary copy of the license terms.

These sources establish the upstream licensing alternatives and the supplied license identity, not whether any particular distribution satisfies the license or whether a commercial license is required. Ask qualified counsel and/or Artifex for that determination.

### Current distribution blockers

1. Select a documented PyMuPDF/MuPDF path (AGPL compliance for the relevant work, or an executed commercial license covering this product and distribution). No such decision or purchase is evidenced here.
2. Produce a release-specific SBOM/notice map for every shipped Python wheel, native library, OCR asset, .NET runtime and PDFium DLL. Verify licenses, copyrights, source availability obligations, notice retention and license texts against upstream tagged sources and the exact release artifact.
3. Resolve the existing PDFium provider/release provenance and .NET runtime notice/version questions in the historical review below.
4. Re-run package inventory and install/runtime acceptance against the exact release ZIP; attach hashes and test evidence. The source directory and previous v6 package are not substitutes.
5. Obtain release-owner/counsel sign-off before any broader redistribution. No legal clearance is claimed by this document.

**October 15 historical source update:** The working source at that time additionally carried an isolated Python 3.14.8 runtime, pypdf, pyHanko/cryptography and dependencies, PyMuPDF 1.28.2, and pinned English OCR data. The v6 audit below does not cover that payload; subsequent v10/v11 evidence is documented above. The supplied PyMuPDF notice identifies AGPL/commercial licensing; corresponding source, complete notices and applicability to any broader distribution remain an explicit release gate. No paid license or redistribution clearance is claimed. The first target remains personal/internal use.

**Review date:** 2026-10-13  
**Status:** evidence-based source and package audit; **not legal clearance**  
**Artifact:** `artifacts/portable-win-x64-20261013-paperwork-done-faster-v6/` and its ZIP  
**Package evidence:** `artifacts/package-verification-20261013-v6/PACKAGE-VERIFICATION.json` (629 ZIP entries; ZIP SHA-256 `0e801e710b94dac514660823e6a6f2aff07450331bf0a886b54b5a06405b2221`).

## Historical v6 review (October 13, 2026)

The review below applies only to the v6 artifact identified in its package evidence. It is retained as historical evidence and does not describe or clear the current advanced-runtime source or a later package.

## Scope and method

Inspected the v6 extracted package, its carried `licenses/` tree, the publish script, PDFium source archive/metadata and native DLL, the .NET SDK payload and project manifests. Compared the package DLL byte-for-byte with the pinned native source DLL and checked that every notice copied by the publish script is present. The package is two self-contained `win-x64` .NET 10 applications (desktop and worker), with `worker/pdfium.dll`; it contains no third-party managed package entries in either app's `.deps.json`. Package verification also records the DLL's packaged worker smoke. No binaries, source or frozen fixtures were changed for this review.

## Package inventory and evidence

| Payload | Observed source/version | Carried package files | Review finding |
|---|---|---|---|
| PDFium native engine | `native/VERSION` and `native/PDFiumConfig.cmake`: 157.0.8086.0 (`chromium/8086`). `native/args.gn`: Windows x64, standalone, non-component, V8/XFA disabled. Download recipe pins provider URL to the `chromium/8086` release. Retained archive SHA-256 `1fd8af952832dbb0eb16d9249f68fe09e5f5ebf7c3dd9f6066ea2720cc28487d`; packaged and source `pdfium.dll` SHA-256 both `1f2aee4286b8cf0413ab4562b7121aa454f9cfd9afd6ca8609bcef80927b3f80`. | `licenses/pdfium/LICENSE`, `VERSION`, and all 17 files in `native/licenses/`: `abseil.txt`, `agg23.txt`, `dragonbox-Apache2-LLVM.txt`, `dragonbox-Boost.txt`, `fast_float.txt`, `freetype.txt`, `harfbuzz.txt`, `icu.txt`, `lcms.txt`, `libjpeg_turbo.ijg`, `libjpeg_turbo.md`, `libopenjpeg.txt`, `libpng.txt`, `llvm-libc.txt`, `pdfium.txt`, `simdutf.txt`, `zlib.txt`. | The package reproduces the provider's root license and the full supplied notice directory. The DLL identity is confirmed. These facts do not settle which notices correspond to linked code in this particular build, whether the provider's stated license covers this release asset, or whether its release provenance is sufficient for public redistribution. |
| .NET / Windows Desktop runtime | `tools/dotnet` SDK 10.0.401; `shared/Microsoft.NETCore.App` and `shared/Microsoft.WindowsDesktop.App` 10.0.12. Package includes self-contained hosts/runtime and WPF assemblies/localized resources. Desktop and worker target `net10.0`; no third-party managed dependency entries appear in their dependency manifests. | `licenses/dotnet/LICENSE.txt` (9,519 bytes) and `licenses/dotnet/ThirdPartyNotices.txt` (78,887 bytes), copied verbatim from the SDK root by `tools/Publish-Portable.ps1`. | The notices are the full SDK-level set, which is broader than the runtime payload. Package inventory does not demonstrate that every notice applies, nor that the SDK-level file is the exact version-matched notice set for the .NET and Windows Desktop 10.0.12 runtime files shipped. Compare with the official runtime and WPF 10.0.12 sources before release. |
| Product code | `App.Desktop`, `App.Worker`, `App.Core`, and `App.Pdfium`; direct native interop; no third-party PDFium wrapper/package is declared. | No separate third-party wrapper notice identified. | This is a manifest-based observation, not a determination that every source or generated asset is original or free of notice obligations. |

The package's PDFium license subtree has 19 files (17 upstream-supplied component notices plus the provider `LICENSE` and `VERSION`); the .NET subtree has two files. All 21 carried files match the preceding v5 package by relative path and SHA-256, as recorded by v6 verification. This comparison proves package-to-package continuity, not legal completeness.

## License and notice review

### PDFium and the provider archive

The copied `native/LICENSE` is a permissive MIT-style grant with copyright/permission-notice retention and disclaimer terms; the binary package includes it at `licenses/pdfium/LICENSE`. The copied `licenses/pdfium/pdfium.txt` contains the PDFium Authors' BSD-style notice/disclaimer and additional license text. Other included files contain notices for PDFium's bundled components, including Abseil, AGG, Dragonbox, fast_float, FreeType, HarfBuzz, ICU, Little CMS, libjpeg-turbo, OpenJPEG, libpng, LLVM libc, simdutf and zlib. Keeping these texts intact in the ZIP is a concrete compliance action already present. The final component-to-license mapping for this compiled DLL has not been established from the build graph or an authoritative release manifest.

There is an upstream licensing detail to resolve: the provider's release archive carries its MIT `LICENSE`, while the provider's Win32 NuGet manifest declares `Apache-2.0` for that package. These are different artifacts and the NuGet declaration does not by itself establish the terms attached to the standalone `pdfium-win-x64.tgz`. Confirm in writing or from release-specific authoritative metadata that the MIT notice and included upstream PDFium/component terms are the applicable terms for release `chromium/8086`, and retain that evidence. Do not remove either license set while resolving the scope.

### .NET and Windows Desktop runtime

The package includes the Microsoft .NET Library License text and a broad third-party notices file from the local SDK. Microsoft's .NET licensing guidance distinguishes Windows product distributions (which use the .NET Library License) and says binary distributions should carry the correct license and third-party notices. The copied license's distributable-code section contains conditions about adding primary functionality, passing on protective terms, displaying copyright, and indemnification. Because this package contains a self-contained Windows runtime, have counsel/release owner confirm how those conditions apply to the publisher's distribution and user terms. The package is not a compiled installer and currently has no installer license screen; that fact alone does not answer whether the carried file and distribution terms satisfy Microsoft's requirements.

A focused version alignment check remains: source the official `.NET Runtime` and `WPF` third-party notices and licensing assets for 10.0.12, compare them to the exact Windows x64 framework files present (including any Windows Desktop-specific notices), and document the result. The present SDK-level file can be retained while validating; it should not be represented as a verified runtime-only notice manifest.

## Concrete follow-up actions

1. **Resolve PDFium provider license scope.** Archive authoritative release-specific evidence for `chromium/8086` that states redistribution terms for the downloaded `.tgz`/DLL and clarifies the provider MIT license versus its NuGet Apache-2.0 declaration. If the evidence is insufficient, ask the provider or counsel; do not claim the provider asset is cleared from the NuGet metadata alone.
2. **Map PDFium notices to the actual build.** Use the exact PDFium revision/build configuration and its third-party manifest to tie each native dependency and license to the delivered `pdfium.dll`; identify any notice absent from `native/licenses/`. Continue to ship the existing full notice set intact until the mapping is complete.
3. **Version-match .NET notices.** Compare the carried SDK-level texts with upstream .NET Runtime and WPF 10.0.12 Windows licensing assets. Confirm required attribution/notice material for this precise self-contained deployment, then make future package generation deterministic from those versioned files.
4. **Review Microsoft terms with counsel/release owner.** Specifically interpret the .NET Library License distribution requirements (user terms, copyright display and indemnity) and decide what the product/package must provide. Record the decision before public redistribution.
5. **Regenerate and re-review the final release artifact.** The reviewed artifact is portable v6 only. Any later runtime, PDFium, installer, icon or package change requires inventory/hash and notice recheck. A passing build or package smoke is not a legal clearance decision.

## Counsel questions (not answered here)

- Does the provider's `LICENSE` cover this exact prebuilt DLL release, and how does that coexist with the provider's NuGet package's `Apache-2.0` declaration?
- Do the supplied notices fully satisfy obligations for the precise static dependencies compiled into this PDFium DLL, and are any distribution form, source-offer, modification or attribution requirements triggered by how this application is shipped?
- Which .NET Library License clauses apply when the runtime is included in a self-contained application ZIP, and what end-user terms, copyright notice and indemnity arrangements are required?
- Is the SDK-root `ThirdPartyNotices.txt` sufficient for both .NETCore.App and WindowsDesktop.App 10.0.12, or should the release use version-specific notices from multiple .NET repositories?

## Upstream sources reviewed

- [PDFium source repository, pinned `chromium/8086` branch](https://pdfium.googlesource.com/pdfium/+/refs/heads/chromium/8086/)
- [PDFium binaries release `chromium/8086`](https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium/8086)
- [PDFium binaries Win32 NuGet manifest](https://github.com/bblanchon/pdfium-binaries/blob/master/nuget/bblanchon.PDFium.Win32.nuspec) (declares `Apache-2.0`; not proof of the standalone release archive's terms)
- [.NET 10.0.12 runtime license](https://github.com/dotnet/runtime/blob/v10.0.12/LICENSE.TXT)
- [.NET 10.0.12 runtime third-party notices](https://github.com/dotnet/runtime/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT)
- [.NET 10.0.12 runtime asset licensing guidance](https://github.com/dotnet/runtime/blob/v10.0.12/docs/project/licensing-assets.md)
- [WPF 10.0.12 third-party notices](https://github.com/dotnet/wpf/blob/v10.0.12/THIRD-PARTY-NOTICES.TXT)
- [.NET 10.0.12 release notes; SDK 10.0.401 includes runtime 10.0.12](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.12/10.0.12.md)

**Release disposition:** Redistribution review remains open. This review identifies retained notices and follow-up checks; it does not grant legal clearance or recommend public release yet.
