import argparse
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageChops


def render(poppler, pdf, prefix):
    subprocess.run(
        [str(poppler), "-png", "-f", "1", "-l", "1", "-singlefile", "-r", "144", str(pdf), str(prefix)],
        check=True,
        capture_output=True,
        text=True,
    )
    return Path(str(prefix) + ".png")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--marked", type=Path, required=True)
    parser.add_argument("--poppler", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--python-packages", type=Path, required=True)
    parser.add_argument("--expect-comment", action="store_true")
    args = parser.parse_args()
    # Use the bundled pure-Python pypdf package while keeping the runtime's
    # matching Pillow extension modules ahead of the project's cp314 wheel.
    sys.path.insert(0, str(args.python_packages))
    from pypdf import PdfReader, __version__ as pypdf_version

    args.output.mkdir(parents=True, exist_ok=False)

    reader = PdfReader(str(args.marked), strict=True)
    annotations = [obj.get_object() for obj in reader.pages[0].get("/Annots", [])]
    by_subtype = {str(a.get("/Subtype")): a for a in annotations}
    expected = {
        "/Highlight": ([72, 700, 180, 720], [72, 720, 180, 720, 72, 700, 180, 700]),
        "/Underline": ([72, 690, 180, 692], [72, 692, 180, 692, 72, 690, 180, 690]),
        "/StrikeOut": ([72, 680, 180, 682], [72, 682, 180, 682, 72, 680, 180, 680]),
    }
    checks = []
    for subtype, (rect, quads) in expected.items():
        annotation = by_subtype.get(subtype)
        if annotation is None:
            raise RuntimeError(f"Missing saved {subtype} annotation")
        actual_rect = [float(x) for x in annotation["/Rect"]]
        actual_quads = [float(x) for x in annotation["/QuadPoints"]]
        if actual_rect != rect or actual_quads != quads:
            raise RuntimeError(f"Unexpected {subtype} geometry: Rect={actual_rect}, QuadPoints={actual_quads}")
        checks.append({"subtype": subtype, "rect": actual_rect, "quadPoints": actual_quads,
                       "appearanceStream": annotation.get("/AP") is not None})

    if args.expect_comment:
        notes = [a for a in annotations if str(a.get("/Subtype")) == "/Text"]
        expected_comment = "Review this clause — confirm the date.\nSecond line."
        if len(notes) != 1 or str(notes[0].get("/Contents")) != expected_comment:
            raise RuntimeError("Saved sticky-note Unicode/multiline contents differ from the requested comment")
        note_rect = [float(x) for x in notes[0]["/Rect"]]
        if note_rect != [220, 650, 244, 674]:
            raise RuntimeError(f"Saved sticky-note rectangle differs: {note_rect}")
        if reader.pages[0].extract_text() != PdfReader(str(args.source), strict=True).pages[0].extract_text():
            raise RuntimeError("Annotation creation changed the original page text")
        checks.append({"subtype": "/Text", "rect": note_rect,
                       "unicodeMultilineContentsMatch": True, "originalPageTextPreserved": True})

    source_png = render(args.poppler, args.source, args.output / "source")
    marked_png = render(args.poppler, args.marked, args.output / "marked")
    source_image = Image.open(source_png).convert("RGB")
    marked_image = Image.open(marked_png).convert("RGB")
    if source_image.size != marked_image.size:
        raise RuntimeError(f"Rendered dimensions differ: {source_image.size} vs {marked_image.size}")
    difference = ImageChops.difference(source_image, marked_image)
    changed = sum(1 for px in difference.getdata() if px != (0, 0, 0))
    bbox = difference.getbbox()
    if changed == 0:
        raise RuntimeError("Poppler renders of saved markup and source are pixel-identical")
    report = {
        "scope": "Independent pypdf structure inspection and Poppler rendering; PDF saved by the application worker",
        "pypdfVersion": pypdf_version,
        "source": str(args.source),
        "marked": str(args.marked),
        "page": 1,
        "annotations": checks,
        "popplerRenderedDimensions": list(source_image.size),
        "changedPixelCount": changed,
        "differenceBoundingBox": list(bbox) if bbox else None,
        "sourceRender": source_png.name,
        "markedRender": marked_png.name,
        "status": "PASS",
    }
    (args.output / "independent-markup.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
