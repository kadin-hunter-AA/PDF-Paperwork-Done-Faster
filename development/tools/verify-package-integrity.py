"""Verify an exact portable ZIP/extracted payload and retain build-source hashes.

Uses only the standard library. This inventories bytes and notices, not redistribution rights.
"""
from pathlib import Path
import hashlib
import json
import os
import sys
import zipfile


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    if len(sys.argv) not in (4, 5):
        raise SystemExit("usage: verify-package-integrity.py DEVELOPMENT PACKAGE FRESH_OUTPUT [ARCHIVE]")
    root, package, output = (Path(value).resolve() for value in sys.argv[1:4])
    archive = Path(sys.argv[4]).resolve() if len(sys.argv) == 5 else Path(str(package) + ".zip")
    if output == package or output.is_relative_to(package) or output == archive:
        raise RuntimeError("Evidence output must be outside the package and ZIP being verified")
    output.mkdir(parents=True, exist_ok=False)
    archive_hash = sha(archive)
    if Path(str(archive) + ".sha256").read_text(encoding="ascii").split()[0].lower() != archive_hash:
        raise RuntimeError("Portable ZIP checksum sidecar does not match")
    payload = {str(path.relative_to(package)).replace("\\", "/"): path
               for path in package.rglob("*") if path.is_file()}
    inventory = []
    with zipfile.ZipFile(archive) as zipped:
        entries = [item for item in zipped.infolist() if not item.is_dir()]
        names = [entry.filename.replace("\\", "/") for entry in entries]
        if len(names) != len(set(names)) or set(names) != set(payload):
            raise RuntimeError("ZIP entries differ from extracted package inventory or contain duplicates")
        for entry, name in zip(entries, names):
            expected = sha(payload[name])
            with zipped.open(entry) as stream:
                actual = hashlib.file_digest(stream, "sha256").hexdigest()
            if actual != expected:
                raise RuntimeError(f"ZIP entry differs from extracted package: {name}")
            inventory.append({"Path": name, "Bytes": entry.file_size, "SHA256": expected})

    notices = []
    notice_pairs = [(root / "native" / "LICENSE", package / "licenses/pdfium/LICENSE"),
                    (root / "native" / "VERSION", package / "licenses/pdfium/VERSION"),
                    (root / "tools/dotnet/LICENSE.txt", package / "licenses/dotnet/LICENSE.txt"),
                    (root / "tools/dotnet/ThirdPartyNotices.txt", package / "licenses/dotnet/ThirdPartyNotices.txt")]
    notice_pairs += [(path, package / "licenses/pdfium" / path.relative_to(root / "native/licenses"))
                     for path in (root / "native/licenses").rglob("*") if path.is_file()]
    for source, copied in notice_pairs:
        if not copied.is_file() or sha(source) != sha(copied):
            raise RuntimeError(f"Supplied notice missing or changed: {copied}")
        notices.append(str(copied.relative_to(package)).replace("\\", "/"))

    sources = []
    source_paths = []
    for folder in ("App.Core", "App.Desktop", "App.Pdfium", "App.Worker", "App.Advanced",
                   "App.Tests", "App.UiTests", "EngineProof", "docs"):
        for directory, subdirectories, filenames in os.walk(root / folder):
            subdirectories[:] = [name for name in subdirectories if name not in {"bin", "obj", "__pycache__"}]
            source_paths.extend(Path(directory) / name for name in filenames if (Path(directory) / name).is_file())
    source_paths.extend(root.glob("*.ps1"))
    source_paths.extend(root.glob("*.slnx"))
    source_paths.extend(root.glob("*.props"))
    source_paths.extend(root.glob("*.targets"))
    source_paths.extend(root.glob("*.json"))
    source_paths.extend((root / "tools").glob("*.ps1"))
    source_paths.extend((root / "tools").glob("*.py"))
    source_paths.extend([root.parent / "design/brand-tokens.json", root.parent / "design/WINDOWS11-DESIGN.md",
                         root.parent / "design/logos/selected-pdf-quill.png"])
    for path in sorted(set(source_paths)):
        sources.append({"Path": str(path.relative_to(root) if path.is_relative_to(root) else Path("..") / path.relative_to(root.parent)).replace("\\", "/"),
                        "SHA256": sha(path), "Bytes": path.stat().st_size})
    (output / "source-inventory.json").write_text(json.dumps(sources, indent=2), encoding="utf-8")
    (output / "package-inventory.json").write_text(json.dumps(inventory, indent=2), encoding="utf-8")
    report = {"Status": "PASS", "Package": str(package), "ZIP": str(archive),
              "ZIPBytes": archive.stat().st_size, "ZIPSHA256": archive_hash,
              "ExactPayloadFiles": len(inventory), "SourceFiles": len(sources),
              "SuppliedNoticesMatched": notices,
              "Scope": "ZIP/extracted payload byte identity, retained source inventory, and supplied notice copying. Runtime smoke is separate; redistribution, physical and clean-machine acceptance remain open."}
    (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"PASS ZIP identity for {len(inventory)} files; {len(notices)} supplied notices; {len(sources)} source hashes. Evidence: {output}")


if __name__ == "__main__":
    main()
