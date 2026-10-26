"""Build a standard-library-only inventory of bundled runtime owners and notices.

This records package evidence; it does not decide license applicability or compliance.
"""
from __future__ import annotations

import argparse
import base64
import csv
import email.parser
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile

BINARY_SUFFIXES = {".dll", ".pyd", ".exe", ".so", ".dylib"}
LICENSE_NAME = re.compile(r"(license|licence|copying|notice|patent)", re.I)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def record_path(base: Path, raw: str) -> Path | None:
    target = (base / Path(raw.replace("/", os.sep))).resolve()
    return target if inside(target, base.resolve()) else None


def file_record(path: Path, root: Path) -> dict:
    return {"Path": path.relative_to(root).as_posix(), "Size": path.stat().st_size, "SHA256": sha256(path)}


def parse_record(record: Path) -> list[list[str]]:
    with record.open(newline="", encoding="utf-8") as stream:
        return list(csv.reader(stream))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", required=True, type=Path, help="Extracted package root")
    parser.add_argument("--archive", type=Path, help="Exact ZIP; adjacent .sha256 sidecar is verified when present")
    parser.add_argument("--output", required=True, type=Path, help="Create-only JSON output, outside package root")
    args = parser.parse_args()

    root = args.package_root.resolve(strict=True)
    output = args.output.resolve(strict=False)
    if not root.is_dir():
        parser.error("--package-root must be a directory")
    if inside(output, root):
        parser.error("--output must be outside the package tree")
    if output.exists():
        parser.error("--output already exists; choose a fresh evidence path")

    advanced = root / "advanced"
    packages = advanced / "packages"
    if not packages.is_dir():
        parser.error("package root is missing advanced/packages")

    package_info: list[dict] = []
    owned_binaries: dict[str, list[str]] = {}
    owned_files: dict[str, list[str]] = {}
    for dist_info in sorted(packages.glob("*.dist-info")):
        metadata_path = dist_info / "METADATA"
        record = dist_info / "RECORD"
        if not metadata_path.is_file() or not record.is_file():
            package_info.append({"DistInfo": dist_info.name, "Error": "METADATA or RECORD missing"})
            continue
        metadata = email.parser.Parser().parsestr(metadata_path.read_text(encoding="utf-8", errors="replace"))
        files, binaries, license_files, missing, outside_records, generated_cache_records, bad_record_hashes = [], [], [], [], [], [], []
        for row in parse_record(record):
            if not row:
                continue
            path = record_path(packages, row[0])
            if path is None:
                outside_records.append(row[0])
                continue
            if not path.is_file():
                if "__pycache__/" in row[0].replace("\\", "/"):
                    generated_cache_records.append(row[0])
                else:
                    missing.append({"RecordPath": row[0], "Reason": "RECORD path is not present in staged package"})
                continue
            rel = path.relative_to(root).as_posix()
            files.append(file_record(path, root))
            owned_files[rel] = [metadata.get("Name", dist_info.name)]
            if path.suffix.lower() in BINARY_SUFFIXES:
                binaries.append(file_record(path, root))
                owned_binaries[rel] = [metadata.get("Name", dist_info.name)]
            if LICENSE_NAME.search(path.name) or "licenses/" in path.as_posix().lower():
                license_files.append(file_record(path, root))
            if len(row) > 1 and row[1].startswith("sha256="):
                expected = row[1][len("sha256="):]
                actual = base64.urlsafe_b64encode(bytes.fromhex(sha256(path))).rstrip(b"=").decode("ascii")
                if actual != expected:
                    bad_record_hashes.append({"Path": rel, "ExpectedRecordSHA256": expected, "ActualRecordSHA256": actual})
        declared_license_files = metadata.get_all("License-File", [])
        for declared in declared_license_files:
            path = record_path(dist_info, declared)
            if path and path.is_file() and not any(item["Path"] == path.relative_to(root).as_posix() for item in license_files):
                license_files.append(file_record(path, root))
        package_info.append({
            "Name": metadata.get("Name"), "Version": metadata.get("Version"),
            "License": metadata.get("License"), "LicenseExpression": metadata.get("License-Expression"),
            "LicenseClassifier": [v for v in metadata.get_all("Classifier", []) if v.lower().startswith("license ::")],
            "LicenseFiles": sorted(license_files, key=lambda item: item["Path"].lower()),
            "Binaries": sorted(binaries, key=lambda item: item["Path"].lower()),
            "RecordFileCount": len(files), "GeneratedCacheEntriesNotShipped": generated_cache_records,
            "RecordEntriesOutsidePackagesTree": outside_records, "UnshippedNonCacheRecordFiles": missing,
            "RecordHashMismatches": bad_record_hashes,
        })

    unowned_binaries = []
    for path in sorted(packages.rglob("*")):
        if path.is_file() and path.suffix.lower() in BINARY_SUFFIXES:
            rel = path.relative_to(root).as_posix()
            if rel not in owned_binaries:
                unowned_binaries.append(file_record(path, root))

    archive_info = None
    if args.archive:
        archive = args.archive.resolve(strict=True)
        actual = sha256(archive)
        sidecar = Path(str(archive) + ".sha256")
        expected = None
        sidecar_matches = None
        if sidecar.is_file():
            match = re.match(r"\s*([0-9a-fA-F]{64})\s+", sidecar.read_text(encoding="ascii", errors="replace"))
            expected = match.group(1).lower() if match else None
            sidecar_matches = expected == actual
        with zipfile.ZipFile(archive) as bundle:
            corrupt_entry = bundle.testzip()
            entry_count = len(bundle.infolist())
        archive_info = {"Path": str(archive), "Size": archive.stat().st_size, "SHA256": actual,
                        "SidecarPath": str(sidecar) if sidecar.is_file() else None,
                        "SidecarSHA256": expected, "SidecarMatches": sidecar_matches,
                        "ZipEntryCount": entry_count, "FirstCorruptEntry": corrupt_entry}

    python_exe = advanced / "python.exe"
    python_runtime = {"Path": python_exe.relative_to(root).as_posix(), "Present": python_exe.is_file()}
    if python_exe.is_file():
        try:
            run = subprocess.run([str(python_exe), "-I", "-B", "--version"], capture_output=True, text=True, timeout=15)
            python_runtime.update({"VersionOutput": (run.stdout or run.stderr).strip(), "ExitCode": run.returncode})
        except (OSError, subprocess.SubprocessError) as error:
            python_runtime["VersionProbeError"] = str(error)
    python_runtime["LicenseFiles"] = [file_record(p, root) for p in sorted(advanced.glob("LICENSE*")) if p.is_file()]
    runtime_inventory = advanced / "runtime-inventory.json"
    if runtime_inventory.is_file():
        inv = json.loads(runtime_inventory.read_text(encoding="utf-8-sig"))
        python_runtime["InventoryVersion"] = inv.get("Version")
        python_runtime["InventoryArchiveSHA256"] = inv.get("PythonArchiveSHA256")
        python_runtime["InventoryFileCount"] = inv.get("FileCount")

    framework_ids = []
    for config in sorted(root.glob("*.runtimeconfig.json")):
        try:
            config_data = json.loads(config.read_text(encoding="utf-8-sig"))
            options = config_data.get("runtimeOptions", {})
            framework_ids.append({"Path": config.relative_to(root).as_posix(), "Framework": options.get("framework"),
                                  "Frameworks": options.get("frameworks"), "IncludedFrameworks": options.get("includedFrameworks"),
                                  "TargetFramework": options.get("tfm")})
        except (OSError, json.JSONDecodeError):
            framework_ids.append({"Path": config.relative_to(root).as_posix(), "ParseError": True})
    deps_files = []
    for deps in sorted(root.glob("*.deps.json")):
        try:
            data = json.loads(deps.read_text(encoding="utf-8-sig"))
            deps_files.append({"Path": deps.relative_to(root).as_posix(), "RuntimeTarget": data.get("runtimeTarget"),
                               "LibraryCount": len(data.get("libraries", {})),
                               "Libraries": sorted(data.get("libraries", {}).keys())})
        except (OSError, json.JSONDecodeError):
            deps_files.append({"Path": deps.relative_to(root).as_posix(), "ParseError": True})
    dotnet_notices = [file_record(p, root) for p in sorted((root / "licenses" / "dotnet").rglob("*"))
                      if p.is_file() and LICENSE_NAME.search(p.name)] if (root / "licenses" / "dotnet").is_dir() else []

    pdfium_dir = root / "licenses" / "pdfium"
    pdfium = {"VersionText": (pdfium_dir / "VERSION").read_text(encoding="utf-8", errors="replace").strip()
              if (pdfium_dir / "VERSION").is_file() else None,
              "Binary": file_record(root / "worker" / "pdfium.dll", root)
              if (root / "worker" / "pdfium.dll").is_file() else None,
              "NoticeFiles": [file_record(p, root) for p in sorted(pdfium_dir.rglob("*"))
                              if p.is_file() and LICENSE_NAME.search(p.name)] if pdfium_dir.is_dir() else []}
    tessdata = advanced / "tessdata"
    ocr = {"EnglishModel": file_record(tessdata / "eng.traineddata", root)
           if (tessdata / "eng.traineddata").is_file() else None,
           "NoticeFiles": [file_record(p, root) for p in sorted(tessdata.rglob("*"))
                           if p.is_file() and LICENSE_NAME.search(p.name)] if tessdata.is_dir() else []}

    result = {
        "Schema": "pdf-editor-package-license-inventory-v1",
        "GeneratedUtc": __import__("datetime").datetime.now(__import__("datetime").timezone.utc).isoformat(),
        "PackageRoot": str(root), "Archive": archive_info,
        "PythonRuntime": python_runtime,
        "PythonDistributions": sorted(package_info, key=lambda item: str(item.get("Name", "")).lower()),
        "DotNet": {"RuntimeConfigs": framework_ids, "DependencyManifests": deps_files, "CarriedNoticeFiles": dotnet_notices,
                   "Unresolved": "Per-file .NET/WPF binary ownership and exact notice applicability are not established by these package manifests."},
        "PDFium": {**pdfium, "Unresolved": "Upstream notice texts are inventoried; mapping each notice to code linked into this exact DLL remains unresolved."},
        "OCR": {**ocr, "Unresolved": "The trained-data file and adjacent notices are inventoried; corpus provenance/applicability is not independently assessed."},
        "UnownedBinariesUnderPythonPackages": unowned_binaries,
        "UnresolvedNativeOwnership": [
            "PyMuPDF/MuPDF native components and their transitive third-party owners are not mapped from wheel build manifests here.",
            "cryptography native extension/OpenSSL linkage and bundled component notice scope require upstream wheel/build evidence.",
            "PDFium static dependency-to-notice mapping requires the exact upstream build graph or release manifest.",
            "Python runtime and .NET runtime notices require version-specific source manifests and release-owner/counsel review.",
        ],
        "Scope": "Reproducible package inventory only. Metadata, file hashes and notice presence do not determine license obligations, compliance, or legal clearance.",
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        parser.error("--output appeared during inventory; refusing to overwrite")
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {output}; Python distributions={len(package_info)}, unowned package binaries={len(unowned_binaries)}")
    if archive_info and archive_info["SidecarMatches"] is False:
        print("ERROR: ZIP SHA-256 does not match the adjacent sidecar", file=sys.stderr)
        return 2
    if archive_info and archive_info["FirstCorruptEntry"]:
        print(f"ERROR: corrupt ZIP entry: {archive_info['FirstCorruptEntry']}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
