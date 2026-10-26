"""Independent pypdf checks for PDFium page organization and page exports."""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path
from uuid import uuid4

# Keep this proof on the project's pinned pure-Python pypdf build.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "python-packages"))
from pypdf import PdfReader, PdfWriter
from pypdf import __version__ as pypdf_version
from pypdf.annotations import Link
from pypdf.generic import ArrayObject, NameObject


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


class Worker:
    def __init__(self, executable: Path) -> None:
        self.process = subprocess.Popen(
            [str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1,
        )
        self.document = str(uuid4())
        self.revision = 0

    def send(self, operation: str, arguments: dict) -> dict:
        assert self.process.stdin is not None and self.process.stdout is not None
        request_id = str(uuid4())
        request = {
            "RequestId": request_id,
            "DocumentId": self.document,
            "Revision": self.revision,
            "Operation": operation,
            "Arguments": arguments,
        }
        self.process.stdin.write(json.dumps(request, separators=(",", ":")) + "\n")
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            stderr = self.process.stderr.read() if self.process.stderr else ""
            raise RuntimeError(f"Worker exited before responding: {stderr}")
        response = json.loads(line)
        if response["RequestId"] != request_id or response["DocumentId"] != self.document:
            raise RuntimeError("Worker returned a mismatched response")
        if response["Success"]:
            self.revision = response["Revision"]
        return response

    def close(self) -> None:
        if self.process.poll() is None:
            try:
                if self.process.stdin:
                    self.process.stdin.close()
                self.process.terminate()
                self.process.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                self.process.kill()
                self.process.wait(timeout=5)


def create_navigation_fixture(path: Path) -> None:
    writer = PdfWriter()
    writer.add_blank_page(width=612, height=792)  # A
    writer.add_blank_page(width=700, height=792)  # B
    writer.add_blank_page(width=800, height=792)  # C
    writer.add_outline_item("Bookmark to C", 2)
    link_ref = writer.add_annotation(0, Link(rect=(20, 20, 80, 60), target_page_index=2))
    # Link's convenience constructor writes an integer page number. Use the
    # normative indirect page reference so the independent parser can verify
    # that PDFium preserves the actual target object through reordering.
    link_ref.get_object()[NameObject("/Dest")] = ArrayObject([
        writer.pages[2].indirect_reference, NameObject("/Fit")
    ])
    writer.add_named_destination("named-target-C", 2)
    writer.add_attachment("reference.txt", b"keep document-level data")
    with path.open("wb") as output:
        writer.write(output)


def create_labeled_fixture(path: Path) -> None:
    writer = PdfWriter()
    writer.add_blank_page(width=612, height=792)
    writer.add_blank_page(width=612, height=792)
    writer.set_page_label(0, 1, prefix="Section-", start=1)
    with path.open("wb") as output:
        writer.write(output)


def create_numeric_navigation_fixture(path: Path) -> None:
    writer = PdfWriter()
    writer.add_blank_page(width=612, height=792)  # A
    writer.add_blank_page(width=700, height=792)  # B
    writer.add_blank_page(width=800, height=792)  # C
    writer.add_annotation(0, Link(rect=(20, 20, 80, 60), target_page_index=2))
    # Keep pypdf's numeric ordinal destination as a deliberately malformed
    # compatibility case. Its intended pre-move target resolves to C (index 2).
    with path.open("wb") as output:
        writer.write(output)


def reorder_and_insert(worker_path: Path, root: Path, output: Path) -> dict:
    source = output / "navigation-source.pdf"
    saved = output / "navigation-result.pdf"
    create_navigation_fixture(source)
    source_hash = sha256(source)
    worker = Worker(worker_path)
    try:
        opened = worker.send("Open", {"Path": str(source), "AllowSignedCopy": False})
        assert opened["Success"] and opened["Data"]["PageCount"] == 3
        moved = worker.send("MovePages", {"Pages": [2], "Destination": 0})
        assert moved["Success"], moved.get("Error")
        inserted = worker.send("InsertBlankPage", {"Page": 1, "Width": 612, "Height": 792})
        assert inserted["Success"] and inserted["Data"]["PageCount"] == 4, inserted.get("Error")
        candidate = worker.send("SaveCandidate", {"Path": str(saved)})
        assert candidate["Success"], candidate.get("Error")
        validated = worker.send("ValidateCandidate", {"Path": str(saved)})
        assert validated["Success"] and validated["Data"]["PageCount"] == 4, validated.get("Error")
    finally:
        worker.close()

    reader = PdfReader(str(saved), strict=True)
    assert len(reader.pages) == 4
    widths = [float(page.mediabox.width) for page in reader.pages]
    assert widths == [800, 612, 612, 700], widths

    # The new blank page is at page 2. The original A page and its link moved to page 3.
    assert not reader.pages[1].get("/Annots")
    links = [a.get_object() for a in reader.pages[2].get("/Annots", [])]
    link = next(a for a in links if a.get("/Subtype") == "/Link")
    assert reader.get_page_number(link["/Dest"][0]) == 0

    outline = reader.outline
    bookmark = next(item for item in outline if not isinstance(item, list) and item.title == "Bookmark to C")
    assert reader.get_destination_page_number(bookmark) == 0
    named = reader.named_destinations["named-target-C"]
    assert reader.get_destination_page_number(named) == 0
    assert reader.attachments["reference.txt"][0] == b"keep document-level data"
    assert sha256(source) == source_hash
    result = {
        "saved": str(saved), "pageWidths": widths,
        "bookmarkTarget": reader.get_destination_page_number(bookmark),
        "namedDestinationTarget": reader.get_destination_page_number(named),
        "linkTarget": reader.get_page_number(link["/Dest"][0]),
        "attachmentRetained": True, "sourceHashUnchanged": True,
    }
    interactive = output / "page-organization" / "interactive-inserted.pdf"
    if interactive.is_file():
        reader = PdfReader(str(interactive), strict=True)
        assert len(reader.pages) == 2
        bookmark = next(item for item in reader.outline if not isinstance(item, list) and item.title == "Corpus bookmark")
        assert reader.get_destination_page_number(bookmark) == 1
        fields = reader.get_fields() or {}
        assert {"sample_name", "unsigned_signature"}.issubset(fields)
        assert fields["sample_name"].get("/V") == "Corpus value"
        assert fields["sample_name"].get("/FT") == "/Tx"
        assert fields["unsigned_signature"].get("/FT") == "/Sig"
        assert "fixture-note.txt" in reader.attachments
        layers = reader.root_object.get("/OCProperties")
        assert layers is not None and len(layers.get("/OCGs", [])) == 1
        page = reader.pages[1]
        assert [float(v) for v in page.mediabox] == [0, 0, 612, 792]
        assert [float(v) for v in page.cropbox] == [0, 0, 612, 792]
        assert page.get("/Rotate", 0) == 0
        annotations = [a.get_object() for a in page.get("/Annots", [])]
        text_widget = next(a for a in annotations if a.get("/T") == "sample_name")
        appearance = text_widget.get("/AP", {}).get("/N")
        assert appearance is not None and len(appearance.get_object().get_data()) > 0
        result["interactive"] = {
            "bookmarkTarget": 1, "fields": sorted(fields), "textFieldValue": fields["sample_name"]["/V"],
            "widgetAppearanceBytes": len(appearance.get_object().get_data()), "attachmentRetained": True,
            "optionalContentGroupCount": len(layers.get("/OCGs", [])),
            "originalPageBoxAndRotationRetained": True, "originalPageAnnotationsRetained": True,
        }
    return result


def reject_custom_labels(worker_path: Path, output: Path) -> dict:
    source = output / "custom-label-source.pdf"
    create_labeled_fixture(source)
    source_hash = sha256(source)
    worker = Worker(worker_path)
    try:
        opened = worker.send("Open", {"Path": str(source), "AllowSignedCopy": False})
        assert opened["Success"] and opened["Data"]["PageCount"] == 2
        reorder = worker.send("MovePages", {"Pages": [1], "Destination": 0})
        assert not reorder["Success"] and "page labels" in reorder["Error"].lower(), reorder
        insert = worker.send("InsertBlankPage", {"Page": 1, "Width": 612, "Height": 792})
        assert not insert["Success"] and "page labels" in insert["Error"].lower(), insert
        info = worker.send("PageInfo", {})
        assert info["Success"] and info["Data"]["Pages"].__len__() == 2
    finally:
        worker.close()
    assert sha256(source) == source_hash
    return {"customLabelsRefused": True, "sourceHashUnchanged": True}


def reject_unpreservable_numeric_destination(worker_path: Path, output: Path) -> dict:
    source = output / "numeric-destination-source.pdf"
    create_numeric_navigation_fixture(source)
    source_hash = sha256(source)
    worker = Worker(worker_path)
    try:
        opened = worker.send("Open", {"Path": str(source), "AllowSignedCopy": False})
        assert opened["Success"] and opened["Data"]["PageCount"] == 3
        result = worker.send("MovePages", {"Pages": [2], "Destination": 0})
        assert not result["Success"], "A move that retargets an ordinal link destination must be refused"
        assert "navigation" in result["Error"].lower() or "destination" in result["Error"].lower(), result
        # Do not save the mutated worker after refusal. The session layer must
        # discard it and retain its last durable checkpoint.
    finally:
        worker.close()
    assert sha256(source) == source_hash
    return {"numericDestinationMoveRefused": True, "sourceHashUnchanged": True}


def verify_page_exports(page_organization: Path, root: Path) -> dict:
    if pypdf_version != "6.1.3":
        raise RuntimeError(f"Expected pinned pypdf 6.1.3, got {pypdf_version}")
    source = page_organization / "export-source.pdf"
    extracted = page_organization / "extracted.pdf"
    split_dir = page_organization / "split-parent" / "pages"
    fixture = root / "fixtures" / "corpus-v1" / "engine" / "page-source.pdf"
    if not all(path.is_file() for path in (source, extracted, fixture)):
        raise FileNotFoundError("Worker page export proof outputs are incomplete")
    source_hash = sha256(source)
    if source_hash != sha256(fixture):
        raise AssertionError("Export source copy differs from the frozen page-source fixture")

    source_reader = PdfReader(str(source), strict=True)
    extracted_reader = PdfReader(str(extracted), strict=True)
    expected_text = [source_reader.pages[i].extract_text() for i in (1, 0)]
    expected_boxes = [
        (
            [float(v) for v in source_reader.pages[i].mediabox],
            [float(v) for v in source_reader.pages[i].cropbox],
            int(source_reader.pages[i].get("/Rotate", 0)),
        )
        for i in (1, 0)
    ]
    if len(extracted_reader.pages) != 2:
        raise AssertionError(f"Extraction has {len(extracted_reader.pages)} pages, expected 2")
    actual_text = [page.extract_text() for page in extracted_reader.pages]
    if actual_text != expected_text:
        raise AssertionError(f"Extracted page order/content differs: {actual_text!r}")
    actual_boxes = [
        (
            [float(v) for v in page.mediabox],
            [float(v) for v in page.cropbox],
            int(page.get("/Rotate", 0)),
        )
        for page in extracted_reader.pages
    ]
    if actual_boxes != expected_boxes:
        raise AssertionError(f"Extracted page geometry differs: {actual_boxes!r}")

    expected_names = ["page-0001.pdf", "page-0002.pdf"]
    actual_names = sorted(path.name for path in split_dir.glob("*.pdf"))
    if actual_names != expected_names:
        raise AssertionError(f"Unexpected split filenames: {actual_names!r}")
    split_text = []
    for index, name in enumerate(expected_names):
        reader = PdfReader(str(split_dir / name), strict=True)
        if len(reader.pages) != 1:
            raise AssertionError(f"{name} has {len(reader.pages)} pages, expected one")
        page = reader.pages[0]
        source_page = source_reader.pages[index]
        text = page.extract_text()
        split_text.append(text)
        geometry = (
            [float(v) for v in page.mediabox],
            [float(v) for v in page.cropbox],
            int(page.get("/Rotate", 0)),
        )
        source_geometry = (
            [float(v) for v in source_page.mediabox],
            [float(v) for v in source_page.cropbox],
            int(source_page.get("/Rotate", 0)),
        )
        if text != source_page.extract_text() or geometry != source_geometry:
            raise AssertionError(f"{name} content or geometry differs from source page {index + 1}")
    if sha256(source) != source_hash or sha256(fixture) != source_hash:
        raise AssertionError("Independent output inspection found changed source bytes")
    return {
        "pypdfVersion": pypdf_version,
        "sourceSha256": source_hash,
        "extracted": {
            "pageCount": len(extracted_reader.pages),
            "textInRequestedOrder": actual_text,
            "pageBoxesAndRotationInRequestedOrder": actual_boxes,
            "reopenedStrictly": True,
        },
        "split": {
            "filenames": actual_names,
            "pageCountPerFile": 1,
            "textBySourceOrder": split_text,
            "reopenedStrictly": True,
        },
        "sourceUnchanged": True,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--page-organization", type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    worker = root / "App.Worker" / "bin" / "Debug" / "net10.0" / "App.Worker.exe"
    if not worker.is_file():
        raise FileNotFoundError(worker)
    results = {
        "navigation": reorder_and_insert(worker, root, output),
        "customLabels": reject_custom_labels(worker, output),
        "numericDestination": reject_unpreservable_numeric_destination(worker, output),
    }
    if args.page_organization:
        results["pageExports"] = verify_page_exports(args.page_organization.resolve(), root)
    (output / "independent-page-proof.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(json.dumps(results, indent=2))


if __name__ == "__main__":
    main()
