"""Isolated structural redaction proof. Generated synthetic PDFs only; not product code."""
from __future__ import annotations

import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path
from typing import Any, Callable, Iterable

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "redaction-pymupdf"))
sys.path.insert(0, str(ROOT / "tools" / "python-packages"))

import pymupdf
from PIL import Image
from pypdf import PdfReader
from pypdf.generic import ArrayObject, ByteStringObject, DictionaryObject, IndirectObject, NullObject, StreamObject, TextStringObject

SECRET_TEXT = "REDACTION_TARGET_ALPHA"
SECRET_HIDDEN = "HIDDEN_OCR_TARGET_GAMMA"
ROTATED_SECRET = "ROTATED_TARGET_BETA"
PUBLIC_BEFORE = "PUBLIC_BEFORE_MARKER"
PUBLIC_AFTER = "PUBLIC_AFTER_MARKER"
PUBLIC_VECTOR = "PUBLIC_VECTOR_MARKER"
IMAGE_REDACTION = pymupdf.Rect(100, 320, 130, 370)
MAX_REDACTION_REGIONS = 128
MAX_TOTAL_REGION_AREA = 8_000_000
OUTPUT_ROOT = ROOT / "artifacts" / f"redaction-proof-{uuid.uuid4().hex}"


class RefusedInput(ValueError):
    pass


def check(condition: bool, message: str, results: list[str]) -> None:
    if not condition:
        raise AssertionError(message)
    results.append(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def make_primary_source(path: Path) -> None:
    doc = pymupdf.open()
    page = doc.new_page(width=612, height=792)
    page.insert_text((72, 80), PUBLIC_BEFORE, fontsize=12)
    page.insert_text((72, 120), SECRET_TEXT, fontsize=12)
    page.insert_text((72, 160), PUBLIC_AFTER, fontsize=12)
    page.insert_text((72, 220), SECRET_HIDDEN, fontsize=12, render_mode=3)
    page.insert_text((72, 450), PUBLIC_VECTOR, fontsize=12)
    page.draw_rect(pymupdf.Rect(300, 300, 360, 360), color=(0, 0.3, 0.8), fill=(0.3, 0.7, 0.9))
    page.draw_rect(pymupdf.Rect(210, 300, 260, 340), color=(0.8, 0, 0), fill=(0.9, 0.4, 0.4))
    # This path only partially intersects the redaction region; safe mode removes the whole path.
    page.draw_line((180, 325), (280, 325), color=(0, 0, 0), width=2)
    page.set_cropbox(pymupdf.Rect(0, 0, 612, 792))

    image_path = path.with_name("synthetic-image.png")
    image = Image.new("RGB", (100, 100))
    pixels = image.load()
    for y in range(100):
        for x in range(100):
            pixels[x, y] = (40 + x * 2, 70 + y, 150)
    image.save(image_path)
    page.insert_image(pymupdf.Rect(72, 300, 172, 400), filename=str(image_path))

    # Non-sensitive metadata verifies removal from both the default Info dictionary and XMP.
    doc.set_metadata({"title": "Redaction proof synthetic title", "author": "Synthetic generator"})
    doc.save(str(path), garbage=4, deflate=True)
    doc.close()
    image_path.unlink()


def make_rotated_crop_source(path: Path) -> None:
    doc = pymupdf.open()
    page = doc.new_page(width=612, height=792)
    page.insert_text((80, 100), ROTATED_SECRET, fontsize=12)
    page.insert_text((80, 150), "ROTATED_PUBLIC_MARKER", fontsize=12)
    page.set_cropbox(pymupdf.Rect(20, 30, 592, 762))
    page.set_rotation(90)
    doc.save(str(path), garbage=4, deflate=True)
    doc.close()


def make_form_source(path: Path) -> None:
    inner = pymupdf.open()
    page = inner.new_page(width=240, height=100)
    page.insert_text((20, 40), "FORM_XOBJECT_SECRET", fontsize=12)
    outer = pymupdf.open()
    page = outer.new_page(width=612, height=792)
    page.show_pdf_page(pymupdf.Rect(72, 100, 312, 200), inner, 0)
    page.show_pdf_page(pymupdf.Rect(330, 100, 570, 200), inner, 0)
    outer.save(str(path), garbage=4, deflate=True)
    outer.close()
    inner.close()


def make_unsupported_sources(folder: Path) -> dict[str, Path]:
    paths: dict[str, Path] = {}

    encrypted = folder / "encrypted.pdf"
    doc = pymupdf.open(); doc.new_page()
    doc.save(str(encrypted), encryption=pymupdf.PDF_ENCRYPT_AES_256, owner_pw="owner-proof", user_pw="user-proof")
    doc.close(); paths["encrypted"] = encrypted

    signed_form = folder / "signature-widget.pdf"
    doc = pymupdf.open(); page = doc.new_page()
    widget = pymupdf.Widget(); widget.field_type = pymupdf.PDF_WIDGET_TYPE_SIGNATURE
    widget.field_name = "Signature1"; widget.rect = pymupdf.Rect(40, 40, 180, 80)
    page.add_widget(widget); doc.save(str(signed_form), garbage=4); doc.close()
    paths["signature-form"] = signed_form

    attached = folder / "embedded-attachment.pdf"
    doc = pymupdf.open(); doc.new_page(); doc.embfile_add("proof.txt", b"synthetic attachment")
    doc.save(str(attached), garbage=4); doc.close(); paths["attachment"] = attached

    layered = folder / "optional-content.pdf"
    doc = pymupdf.open(); page = doc.new_page(); ocg = doc.add_ocg("Synthetic layer")
    page.insert_text((50, 60), "LAYER_CONTENT", oc=ocg); doc.save(str(layered), garbage=4); doc.close()
    paths["optional-content"] = layered

    tagged = folder / "tagged-structure.pdf"
    doc = pymupdf.open(); doc.new_page()
    catalog = doc.pdf_catalog()
    doc.xref_set_key(catalog, "StructTreeRoot", "<< /Type /StructTreeRoot /K [] >>")
    doc.save(str(tagged), garbage=4); doc.close(); paths["tagged"] = tagged

    form = folder / "nested-shared-form.pdf"
    make_form_source(form); paths["nested-shared-form"] = form
    return paths


def preflight(path: Path) -> None:
    try:
        doc = pymupdf.open(str(path))
    except Exception as exc:
        raise RefusedInput("The file could not be opened as a supported PDF.") from exc
    try:
        if not doc.is_pdf:
            raise RefusedInput("Only PDF input is in this prototype scope.")
        if doc.needs_pass:
            raise RefusedInput("Encrypted PDFs are outside this prototype scope.")
        catalog = doc.pdf_catalog()
        for key, reason in (("StructTreeRoot", "Tagged PDFs"), ("OCProperties", "Optional-content layers"),
                            ("Perms", "Signed or permission-controlled PDFs")):
            kind, value = doc.xref_get_key(catalog, key)
            if kind != "null":
                raise RefusedInput(f"{reason} are outside this prototype scope.")
        if doc.embfile_count() > 0:
            raise RefusedInput("PDFs with embedded files are outside this prototype scope.")
        sigflags = doc.get_sigflags()
        if sigflags not in (None, -1, 0):
            raise RefusedInput("PDFs with signature flags are outside this prototype scope.")
        for page in doc:
            if page.get_xobjects():
                raise RefusedInput("Pages using Form XObjects are outside this prototype scope.")
            if list(page.widgets() or []):
                raise RefusedInput("PDF forms and signature widgets are outside this prototype scope.")
            if page.get_links():
                raise RefusedInput("Pages with links are outside this prototype scope.")
            if list(page.annots() or []):
                raise RefusedInput("Existing annotations are outside this prototype scope.")
    finally:
        doc.close()


def publish_no_replace(candidate: Path, destination: Path) -> None:
    """Create the destination name atomically; never replace an existing file."""
    # Candidate is deliberately in the destination directory, so the hard-link operation
    # is on one volume and fails atomically if another process won the destination name.
    os.link(candidate, destination)
    candidate.unlink()


def apply_fresh(source: Path, destination: Path, regions: dict[int, list[pymupdf.Rect]], *,
                cancellation_check: Callable[[], bool] | None = None,
                before_publish: Callable[[Path, Path], None] | None = None) -> None:
    """Apply bounded native redactions, validate a fresh rewrite, then publish create-only."""
    if source.resolve() == destination.resolve():
        raise RefusedInput("In-place overwrite is refused.")
    if destination.exists():
        raise RefusedInput("Existing output is never overwritten.")
    if (not regions or len(regions) > MAX_REDACTION_REGIONS or
            sum(len(rects) for rects in regions.values()) > MAX_REDACTION_REGIONS):
        raise RefusedInput("Provide a bounded, non-empty set of redaction regions.")
    source_hash = sha256(source)
    preflight(source)
    if cancellation_check is not None and cancellation_check():
        raise InterruptedError("Redaction was canceled before editing.")
    destination.parent.mkdir(parents=True, exist_ok=True)
    candidate = destination.with_name(destination.name + ".candidate-" + uuid.uuid4().hex)
    try:
        doc = pymupdf.open(str(source))
        try:
            total_area = 0.0
            for page_number, rects in regions.items():
                if not isinstance(page_number, int) or isinstance(page_number, bool) or page_number < 0 or page_number >= doc.page_count:
                    raise RefusedInput("Redaction page index is outside the document.")
                if not rects:
                    raise RefusedInput("Each selected page must have at least one redaction region.")
                page = doc[page_number]
                for rect in rects:
                    if not all(math.isfinite(value) for value in rect) or rect.is_empty or not page.rect.contains(rect):
                        raise RefusedInput("Redaction region must be finite, non-empty and inside the visible page.")
                    total_area += rect.get_area()
                    if total_area > MAX_TOTAL_REGION_AREA:
                        raise RefusedInput("Combined redaction area exceeds the bounded prototype limit.")
                    page.add_redact_annot(rect, fill=(1, 1, 1), cross_out=False)
                page.apply_redactions(images=pymupdf.PDF_REDACT_IMAGE_PIXELS,
                                      graphics=pymupdf.PDF_REDACT_LINE_ART_REMOVE_IF_TOUCHED,
                                      text=pymupdf.PDF_REDACT_TEXT_REMOVE)
            doc.set_metadata({})
            doc.set_xml_metadata("")
            # Clearing XMP contents alone can leave an empty /Metadata stream linked from the catalog.
            doc.xref_set_key(doc.pdf_catalog(), "Metadata", "null")
            doc.save(str(candidate), garbage=4, deflate=True, clean=True, incremental=False)
        finally:
            doc.close()
        with pymupdf.open(str(candidate)) as reopened:
            if reopened.needs_pass or not reopened.is_pdf:
                raise RuntimeError("The candidate failed independent reopen validation.")
        if cancellation_check is not None and cancellation_check():
            raise InterruptedError("Redaction was canceled before publication.")
        if before_publish is not None:
            before_publish(source, destination)
        if sha256(source) != source_hash:
            raise RefusedInput("The source changed while the redaction candidate was being prepared.")
        publish_no_replace(candidate, destination)
    finally:
        if candidate.exists():
            candidate.unlink()


def object_strings(value: Any, seen: set[int] | None = None) -> Iterable[bytes]:
    if seen is None:
        seen = set()
    if isinstance(value, IndirectObject):
        key = (value.idnum << 16) + value.generation
        if key in seen:
            return
        seen.add(key)
        try:
            value = value.get_object()
        except Exception:
            return
    if isinstance(value, (TextStringObject, ByteStringObject)):
        yield str(value).encode("utf-8", "ignore")
    if isinstance(value, StreamObject):
        try:
            yield value.get_data()
        except Exception:
            pass
    if isinstance(value, DictionaryObject):
        for key, child in value.items():
            yield str(key).encode("utf-8", "ignore")
            yield from object_strings(child, seen)
    elif isinstance(value, ArrayObject):
        for child in value:
            yield from object_strings(child, seen)


def all_object_streams(path: Path) -> tuple[PdfReader, list[bytes]]:
    reader = PdfReader(str(path), strict=True)
    chunks: list[bytes] = []
    seen: set[int] = set()
    # Include every xref object, not just what text extraction reaches, to catch stale
    # or unreachable compressed streams left behind by an incremental save.
    for generation, objects in reader.xref.items():
        for object_number in objects:
            if object_number == 0:
                continue
            chunks.extend(object_strings(IndirectObject(object_number, generation, reader), seen))
    chunks.extend(object_strings(reader.trailer))
    return reader, chunks


def run_poppler_render(path: Path, output_stem: Path) -> Path:
    executable = shutil.which("pdftoppm") or os.path.join(os.environ.get("POPPLER_BIN", ""), "pdftoppm.exe")
    if not Path(executable).is_file():
        raise RuntimeError("Independent Poppler pdftoppm renderer is unavailable.")
    result = subprocess.run([executable, "-f", "1", "-l", "1", "-r", "72", "-singlefile", "-png", str(path), str(output_stem)],
                            capture_output=True, text=True, timeout=60, check=False)
    if result.returncode != 0:
        raise RuntimeError("Poppler could not render the generated proof PDF.")
    png = output_stem.with_suffix(".png")
    if not png.is_file():
        raise RuntimeError("Poppler render output is missing.")
    return png


def exercise_accepted_subset(folder: Path, results: list[str]) -> dict[str, Any]:
    source = folder / "accepted-source.pdf"
    output = folder / "accepted-redacted.pdf"
    make_primary_source(source)
    source_hash = sha256(source)
    with pymupdf.open(str(source)) as doc:
        page = doc[0]
        normal_hits = page.search_for(SECRET_TEXT)
        hidden_hits = page.search_for(SECRET_HIDDEN)
        check(len(normal_hits) == 1, "Synthetic visible target is located as PDF text", results)
        check(len(hidden_hits) == 1, "Synthetic invisible OCR-style target is still searchable as text", results)
        regions = [normal_hits[0] + (-1, -1, 1, 1), hidden_hits[0] + (-1, -1, 1, 1), IMAGE_REDACTION,
                   pymupdf.Rect(209, 299, 261, 341)]
        page_count = doc.page_count
        cropbox = tuple(page.cropbox)
        rotation = page.rotation
    apply_fresh(source, output, {0: regions})

    with pymupdf.open(str(output)) as doc:
        page = doc[0]
        text = page.get_text()
        check(SECRET_TEXT not in text and SECRET_HIDDEN not in text,
              "Applied redaction removes visible and invisible target text after reopen", results)
        check(PUBLIC_BEFORE in text and PUBLIC_AFTER in text and PUBLIC_VECTOR in text,
              "Unrelated text before, after and beside redactions survives reopen", results)
        check(len(page.get_drawings()) >= 1, "Unrelated vector drawing survives; the page was not flattened", results)
        outside_partial_line = pymupdf.Rect(179, 324, 205, 326)
        check(not any(item["rect"].intersects(outside_partial_line) for item in page.get_drawings()),
              "A vector path that partially intersects the redaction region is removed as a whole", results)
        images = page.get_images(full=True)
        check(len(images) >= 1, "Partially redacted image remains an image resource", results)
        check(doc.page_count == page_count and tuple(page.cropbox) == cropbox and page.rotation == rotation,
              "Page count, crop box and rotation survive the redaction rewrite", results)
        check(not any(doc.metadata.get(key) for key in ("title", "author", "subject", "keywords", "creator", "producer")),
              "Standard metadata is removed by default", results)
        page_streams = b"".join(doc.xref_stream(xref) or b"" for xref in page.get_contents())
        check(b"BT" in page_streams and b"Do" in page_streams,
              "Output page retains text operators and an image invocation in page content streams", results)
        check(sha256(source) == source_hash, "Original source bytes remain unchanged", results)

    reader, objects = all_object_streams(output)
    all_bytes = b"\n".join(objects)
    root = reader.trailer["/Root"].get_object()
    metadata_ref = root.get("/Metadata")
    check(b"Redaction proof synthetic title" not in all_bytes and b"Synthetic generator" not in all_bytes and
          (metadata_ref is None or isinstance(metadata_ref, NullObject)),
          "Independent pypdf scan finds no retained standard metadata or XMP stream", results)
    for marker in (SECRET_TEXT.encode(), SECRET_HIDDEN.encode()):
        check(marker not in all_bytes, "Target text is absent from every pypdf-readable reachable or unreachable object stream", results)
    page0 = reader.pages[0]
    independent_text = page0.extract_text() or ""
    check(SECRET_TEXT not in independent_text and SECRET_HIDDEN not in independent_text and
          PUBLIC_AFTER in independent_text,
          "Independent pypdf extraction agrees that targets are gone and unrelated text remains", results)
    resource_xobjects = page0["/Resources"].get("/XObject", {})
    image_resources = [obj.get_object() for obj in resource_xobjects.values()
                       if obj.get_object().get("/Subtype") == "/Image"]
    check(len(image_resources) >= 1, "Independent pypdf resource traversal reaches a preserved image XObject", results)

    source_png = run_poppler_render(source, folder / "poppler-source")
    output_png = run_poppler_render(output, folder / "poppler-output")
    with Image.open(source_png).convert("RGB") as before, Image.open(output_png).convert("RGB") as after:
        check(before.size == after.size == (612, 792), "Independent Poppler renders retain the expected full page", results)
        check(after.getpixel((110, 345)) == (255, 255, 255), "Poppler render shows a white, applied partial-image redaction region", results)
        check(after.getpixel((160, 345)) == before.getpixel((160, 345)), "Poppler render retains untouched pixels in the same image", results)
        check(after.getpixel((400, 330)) == before.getpixel((400, 330)), "Poppler render preserves unrelated vector-area pixels", results)
        check(before.getpixel((190, 325)) != (255, 255, 255) and after.getpixel((190, 325)) == (255, 255, 255),
              "Independent Poppler render confirms the entire partially touched vector path is gone", results)
    return {"source": str(source), "output": str(output), "source_sha256": source_hash,
            "output_sha256": sha256(output), "poppler_source_png": str(source_png),
            "poppler_output_png": str(output_png)}


def exercise_rotated_crop(folder: Path, results: list[str]) -> str:
    source = folder / "rotated-crop-source.pdf"
    output = folder / "rotated-crop-redacted.pdf"
    make_rotated_crop_source(source)
    with pymupdf.open(str(source)) as doc:
        page = doc[0]
        check(page.rotation == 90 and page.cropbox != page.mediabox,
              "Rotated and cropped page adversary is generated", results)
        hits = page.search_for(ROTATED_SECRET)
        check(len(hits) == 1, "Rotated/cropped target can be located in MuPDF page coordinates", results)
    apply_fresh(source, output, {0: [hits[0] + (-1, -1, 1, 1)]})
    with pymupdf.open(str(output)) as doc:
        page = doc[0]
        check(ROTATED_SECRET not in page.get_text() and "ROTATED_PUBLIC_MARKER" in page.get_text(),
              "Rotated/cropped redaction removes only target text and preserves unrelated text", results)
        check(page.rotation == 90, "Rotated page orientation survives a full rewrite", results)
    reader, chunks = all_object_streams(output)
    check(ROTATED_SECRET.encode() not in b"\n".join(chunks),
          "Independent pypdf object-stream scan finds no rotated target bytes", results)
    return str(output)


def exercise_refusals(folder: Path, results: list[str]) -> dict[str, str]:
    sources = make_unsupported_sources(folder)
    outcomes: dict[str, str] = {}
    for name, source in sources.items():
        try:
            preflight(source)
        except RefusedInput as exc:
            outcomes[name] = str(exc)
            results.append(f"Unsupported {name} source is refused before editing")
        else:
            raise AssertionError(f"Unsupported {name} PDF was not rejected")
    for name in ("encrypted", "signature-form", "attachment", "optional-content", "tagged", "nested-shared-form"):
        check(name in outcomes, f"Fail-closed preflight includes {name}", results)
    return outcomes


def exercise_metadata_default(folder: Path, results: list[str]) -> str:
    source = folder / "accepted-source.pdf"
    output = folder / "metadata-default-removal.pdf"
    with pymupdf.open(str(source)) as doc:
        hit = doc[0].search_for(SECRET_TEXT)[0]
    apply_fresh(source, output, {0: [hit + (-1, -1, 1, 1)]})
    with pymupdf.open(str(output)) as doc:
        metadata = doc.metadata
        check(not any(metadata.get(key) for key in ("title", "author", "subject", "keywords", "creator", "producer")),
              "Default save path clears standard document metadata", results)
        check(not doc.get_xml_metadata(), "Default save path clears XMP metadata", results)
    return str(output)


def exercise_publication_safety(folder: Path, results: list[str]) -> dict[str, str]:
    source = folder / "accepted-source.pdf"
    with pymupdf.open(str(source)) as doc:
        hit = doc[0].search_for(SECRET_TEXT)[0]
    region = hit + (-1, -1, 1, 1)

    for name, regions in (
        ("empty", {}),
        ("bad-page", {doc_page_count(source): [region]}),
        ("bad-bool-page", {True: [region]}),
        ("too-many-regions", {0: [pymupdf.Rect(1, 1, 2, 2)] * (MAX_REDACTION_REGIONS + 1)}),
        ("too-much-area", {0: [pymupdf.Rect(0, 0, 612, 792)] * 17}),
    ):
        destination = folder / f"invalid-region-{name}.pdf"
        try:
            apply_fresh(source, destination, regions)
        except RefusedInput:
            check(not destination.exists(), f"Invalid {name} geometry is refused without publication", results)
        else:
            raise AssertionError(f"Invalid {name} region set was accepted")

    canceled_output = folder / "canceled-output.pdf"
    try:
        apply_fresh(source, canceled_output, {0: [region]}, cancellation_check=lambda: True)
    except InterruptedError:
        check(not canceled_output.exists(), "Cancellation prevents publication", results)
    else:
        raise AssertionError("Cancellation was ignored")

    collision_output = folder / "collision-output.pdf"
    sentinel = b"destination created by competing writer"
    def create_collision(_source: Path, destination: Path) -> None:
        destination.write_bytes(sentinel)
    try:
        apply_fresh(source, collision_output, {0: [region]}, before_publish=create_collision)
    except FileExistsError:
        check(collision_output.read_bytes() == sentinel,
              "Atomic create-only publication preserves a destination created during processing", results)
    else:
        raise AssertionError("Destination race overwrote or ignored a competing file")

    changed_source = folder / "source-changed-during-work.pdf"
    shutil.copy2(source, changed_source)
    changed_output = folder / "source-change-output.pdf"
    def mutate_source(source_path: Path, _destination: Path) -> None:
        with source_path.open("ab") as stream:
            stream.write(b"\n")
    try:
        apply_fresh(changed_source, changed_output, {0: [region]}, before_publish=mutate_source)
    except RefusedInput as exc:
        check("source changed" in str(exc).lower() and not changed_output.exists(),
              "Source hash recheck refuses publication after concurrent source change", results)
    else:
        raise AssertionError("A changed source was published")
    return {"collision_output": str(collision_output), "canceled_output": str(canceled_output),
            "source_change_output": str(changed_output)}


def doc_page_count(path: Path) -> int:
    with pymupdf.open(str(path)) as doc:
        return doc.page_count


def main() -> int:
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=False)
    results: list[str] = []
    try:
        accepted = exercise_accepted_subset(OUTPUT_ROOT, results)
        rotated = exercise_rotated_crop(OUTPUT_ROOT, results)
        refused = exercise_refusals(OUTPUT_ROOT, results)
        metadata_removed = exercise_metadata_default(OUTPUT_ROOT, results)
        safety = exercise_publication_safety(OUTPUT_ROOT, results)
        report = {"engine": "PyMuPDF", "engine_version": pymupdf.VersionBind,
                  "scope": "synthetic unencrypted, unsigned, untagged PDFs without forms, links, annotations, optional-content groups, attachments or Form XObjects",
                  "passed": results, "accepted_case": accepted, "rotated_crop_case": rotated,
                  "refused_cases": refused, "metadata_removal_case": metadata_removed,
                  "publication_safety_cases": safety,
                  "limitations": ["development-only proof; not integrated into UI", "not general verified redaction",
                                  "Form XObjects are refused because nested/shared content is outside the accepted subset",
                                  "signature/forms/tags/layers/attachments/links/existing annotations are refused",
                                  "source scanning is not a proof that arbitrary PDF semantics or every viewer are handled",
                                  "PyMuPDF AGPL/commercial licensing remains a legal review gate"]}
        (OUTPUT_ROOT / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"PASS {len(results)} structural redaction proof checks; artifacts: {OUTPUT_ROOT}")
        return 0
    except Exception as exc:
        (OUTPUT_ROOT / "failure.json").write_text(json.dumps({"type": type(exc).__name__, "message": str(exc), "passed": results}, indent=2), encoding="utf-8")
        print(f"FAIL after {len(results)} checks; details: {OUTPUT_ROOT / 'failure.json'}")
        raise


if __name__ == "__main__":
    raise SystemExit(main())
