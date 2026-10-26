# Third-party notices and runtime inventory

Status: factual package inventory only. Redistribution and legal review remain pending. This document does not make a legal determination about redistribution rights or compliance.

## Last audited portable Windows x64 package: v6

The last audited artifact is `artifacts/portable-win-x64-20261013-paperwork-done-faster-v6/` and its ZIP; its verification record is `artifacts/package-verification-20261013-v6/PACKAGE-VERIFICATION.json`. V6 contains 629 ZIP entries and has the checksum recorded in that JSON. Earlier v2–v5 records remain historical. The source build recipe is `tools/Publish-Portable.ps1`; the package was published as two self-contained `win-x64` .NET applications (desktop and worker), with `pdfium.dll` copied into the `worker/` directory. The focused `docs/REDISTRIBUTION-REVIEW.md` records unresolved questions and does not grant legal clearance. Any newer package requires another inventory and notice review.

### Components observed in the package

| Included component | Version/source evidence | Notice files carried in the package |
|---|---|---|
| PDFium native engine (`pdfium.dll`, 64-bit Windows) | `native/VERSION` and `native/PDFiumConfig.cmake` identify 157.0.8086.0; the retained archive is `native/pdfium-win-x64.tgz`, SHA-256 `1fd8af952832dbb0eb16d9249f68fe09e5f5ebf7c3dd9f6066ea2720cc28487d`. Project notes identify the binary provider as bblanchon/pdfium-binaries, release `chromium/8086`. The package binary matches `native/bin/pdfium.dll` by SHA-256 `1f2aee4286b8cf0413ab4562b7121aa454f9cfd9afd6ca8609bcef80927b3f80`. `native/args.gn` records a Windows x64, standalone, non-component build with V8/XFA disabled. | `licenses/pdfium/LICENSE`, `VERSION`, and every file from `native/licenses/`: `abseil.txt`, `agg23.txt`, `dragonbox-Apache2-LLVM.txt`, `dragonbox-Boost.txt`, `fast_float.txt`, `freetype.txt`, `harfbuzz.txt`, `icu.txt`, `lcms.txt`, `libjpeg_turbo.ijg`, `libjpeg_turbo.md`, `libopenjpeg.txt`, `libpng.txt`, `llvm-libc.txt`, `pdfium.txt`, `simdutf.txt`, `zlib.txt`. These are the files carried; exact component-to-notice mapping and any additional notice requirements have not been independently established. |
| .NET runtime and Windows Desktop/WPF runtime | `tools/dotnet` contains SDK 10.0.401; its `shared/Microsoft.NETCore.App` and `shared/Microsoft.WindowsDesktop.App` runtime directories are 10.0.12. The package contains .NET host/runtime files and Windows Desktop/WPF assemblies/localized resources. Both app projects target .NET 10; package `*.deps.json` files identify app assemblies and the framework targets, with no third-party managed package dependency entries. | `licenses/dotnet/LICENSE.txt` and `licenses/dotnet/ThirdPartyNotices.txt`, copied from `tools/dotnet`. The SDK-level notice file is broad/overinclusive relative to this specific runtime payload; it is carried as-is, and this inventory does not assert that every listed item is present or that every applicable upstream notice is covered. |
| Application managed code and direct PDFium interop | `App.Desktop`, `App.Worker`, `App.Core`, and `App.Pdfium`; `App.Pdfium.csproj` compiles the linked `EngineProof/Pdf.cs` and copies the native DLL. No third-party managed PDFium wrapper is declared in the project files; interop is direct. | No separate third-party wrapper notice was found or identified for the app projects. This is an observation from the project/dependency manifests, not a legal conclusion. |

The packages were inspected at the file level: the `licenses/` trees contain the PDFium notice files above and the two .NET notice files. The package `App.Desktop.deps.json` and `App.Worker.deps.json` target entries were inspected for runtime dependency identities. V3 was compared against v2, v4 against v3, v5 against v4, and v6 against v5 by relative path and SHA-256 for all 21 files under `licenses/`; all names and hashes match in each comparison. V6 package identity/hash and worker host smoke are recorded in `artifacts/package-verification-20261013-v6/PACKAGE-VERIFICATION.json`; packaged desktop UI smoke remains pending. This review did not map every runtime binary to every notice entry or validate every notice against the originating upstream revision.

## Development, test, and fixture materials (not the portable app runtime)

- The edge corpus includes a pinned upstream PDFium test fixture at source revision `e644f8cba5313005b89c16d83d453151a2c8fad5`. The unmodified source PDF, its full upstream license, provenance, and hash are retained in `fixtures/public`. Reproduce the fixture's upstream notices if distributing that fixture. Source: https://pdfium.googlesource.com/pdfium/+/e644f8cba5313005b89c16d83d453151a2c8fad5/testing/resources/hello_world.pdf .
- Signature corpus development uses pyHanko 0.31.0 and its pinned dependencies under `tools/signing-packages`; exact pins are in `tools/requirements-signed.txt`. This is separate from the desktop runtime.
- Compatibility corpus and independent verification tools use pypdf 6.1.3, ReportLab 4.4.4, Pillow 12.0.0, cryptography 46.0.3, cffi 2.1.1, charset-normalizer 3.5.2, and pycparser 3.11, pinned in `tools/compatibility-requirements.txt`; package metadata/license files are retained under `tools/python-packages`. They are development/test tools, not current desktop runtime dependencies.
- The historical `EngineProof` nested-edit harness may invoke Python/pypdf 6.1.3 and carries source modules and license in that harness output; its core-font metric module retains Adobe AFM notices. Poppler was used for independent visual QA; no Poppler binaries are included in the portable package.
- The portable package contains no Python/pypdf runtime. Development SDK files under `tools/dotnet` are not themselves copied as a directory into the portable package; the package carries the self-contained .NET runtime payload and the two .NET notice files described above.

## Outstanding review

This inventory records what is present in the reviewed package, not a complete legal audit. Public distribution remains undecided. Before any release or other redistribution, the responsible reviewer must verify the exact binaries and provenance, map all included components to their upstream license/notice requirements, assess required attribution and distribution materials, and confirm that the package carries all required notices. Review scope must include PDFium and its bundled native dependencies as well as the self-contained .NET and Windows Desktop runtime content. No legal conclusion is made here.




