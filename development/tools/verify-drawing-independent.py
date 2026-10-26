"""Independent pypdf structure and MuPDF pixel check for saved drawing annotations."""
import argparse
import json
import sys
from pathlib import Path


def close_sequence(actual, expected, tolerance=1e-5):
    return len(actual) == len(expected) and all(abs(float(a) - b) <= tolerance for a, b in zip(actual, expected))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--saved", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--python-packages", type=Path, required=True)
    parser.add_argument("--pymupdf-packages", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(args.python_packages))
    sys.path.insert(0, str(args.pymupdf_packages))
    from pypdf import PdfReader, __version__ as pypdf_version
    import pymupdf

    args.output.mkdir(parents=True, exist_ok=False)
    source = PdfReader(str(args.source), strict=True)
    saved = PdfReader(str(args.saved), strict=True)
    if len(source.pages) != len(saved.pages) or len(saved.pages) < 1:
        raise RuntimeError("Saved PDF page count differs from source")
    if source.pages[0].extract_text() != saved.pages[0].extract_text():
        raise RuntimeError("Drawing annotations changed original page text")

    annotations = [ref.get_object() for ref in saved.pages[0].get("/Annots", [])]
    drawings = [item for item in annotations if str(item.get("/Subtype")) in {"/Ink", "/Square", "/Circle"}]
    inks = [item for item in drawings if str(item.get("/Subtype")) == "/Ink"]
    by_subtype = {str(item.get("/Subtype")): item for item in drawings if str(item.get("/Subtype")) != "/Ink"}
    if len(drawings) != 4 or len(inks) != 2 or set(by_subtype) != {"/Square", "/Circle"}:
        raise RuntimeError(f"Expected line, arrow, square and circle annotations, got {[str(a.get('/Subtype')) for a in drawings]}")

    expected = {
        "/Square": {"rect": [250, 600, 370, 690], "color": [51 / 255, 23 / 255, 139 / 255], "border": [0, 0, 2]},
        "/Circle": {"rect": [400, 600, 520, 690], "color": [51 / 255, 23 / 255, 139 / 255], "border": [0, 0, 2]},
    }
    verified = []
    specs = [
        ("/Ink line", inks[0], {"rect": [90, 600, 220, 690], "color": [51 / 255, 23 / 255, 139 / 255], "ink": [[90, 600, 220, 690]]}),
        ("/Ink arrow", inks[1], {"rect": [90, 550, 220, 590], "color": [51 / 255, 23 / 255, 139 / 255]}),
        *[(subtype, by_subtype[subtype], spec) for subtype, spec in expected.items()],
    ]
    for subtype, annotation, spec in specs:
        rect = [float(value) for value in annotation["/Rect"]]
        color = [float(value) for value in annotation["/C"]]
        flags = int(annotation.get("/F", 0))
        if (flags & 4) == 0:
            raise RuntimeError(f"Saved {subtype} annotation is missing the PDF Print flag: /F={flags}")
        if not close_sequence(rect, spec["rect"]):
            raise RuntimeError(f"Unexpected {subtype} rectangle: {rect}")
        if not close_sequence(color, spec["color"]):
            raise RuntimeError(f"Unexpected {subtype} stroke color: {color}")
        result = {"subtype": subtype, "rect": rect, "color": color, "flags": flags, "printFlagSet": True}
        if subtype.startswith("/Ink"):
            ink = [[float(value) for value in path] for path in annotation["/InkList"]]
            expected_paths = 1 if subtype == "/Ink line" else 3
            if len(ink) != expected_paths or (subtype == "/Ink line" and not close_sequence(ink[0], spec["ink"][0])):
                raise RuntimeError(f"Unexpected saved InkList endpoints: {ink}")
            for path in ink:
                for x, y in zip(path[::2], path[1::2]):
                    if not rect[0] <= x <= rect[2] or not rect[1] <= y <= rect[3]:
                        raise RuntimeError(f"Saved {subtype} stroke escapes annotation bounds: {(x, y)} outside {rect}")
            if subtype == "/Ink arrow" and (not close_sequence(ink[0], [90, 550, 220, 590]) or
                    any(not close_sequence(path[:2], [220, 590]) for path in ink[1:])):
                raise RuntimeError(f"Saved arrowhead strokes do not join the arrow endpoint: {ink}")
            result["inkList"] = ink
        else:
            border = [float(value) for value in annotation["/Border"]]
            if not close_sequence(border, spec["border"]):
                raise RuntimeError(f"Unexpected {subtype} border: {border}")
            result["border"] = border
        verified.append(result)

    # This isolated MuPDF rendering path is independent of the PDFium worker.
    source_doc = pymupdf.open(str(args.source))
    saved_doc = pymupdf.open(str(args.saved))
    try:
        source_pix = source_doc[0].get_pixmap(matrix=pymupdf.Matrix(2, 2), colorspace=pymupdf.csRGB, alpha=False, annots=True)
        saved_pix = saved_doc[0].get_pixmap(matrix=pymupdf.Matrix(2, 2), colorspace=pymupdf.csRGB, alpha=False, annots=True)
        if source_pix.width != saved_pix.width or source_pix.height != saved_pix.height:
            raise RuntimeError("Independent render dimensions differ")
        source_png = args.output / "source.png"
        saved_png = args.output / "drawing-saved.png"
        source_pix.save(str(source_png))
        saved_pix.save(str(saved_png))
        a, b = source_pix.samples, saved_pix.samples
        if len(a) != len(b):
            raise RuntimeError("Independent render byte counts differ")
        width, height, stride = saved_pix.width, saved_pix.height, saved_pix.n
        changed = 0
        xs, ys = [], []
        for pixel in range(width * height):
            offset = pixel * stride
            if a[offset:offset + 3] != b[offset:offset + 3]:
                changed += 1
                xs.append(pixel % width)
                ys.append(pixel // width)
        if changed == 0:
            raise RuntimeError("Independent render shows no drawing annotation pixels")
        report = {
            "status": "PASS",
            "scope": "Independent pypdf annotation inspection and isolated MuPDF rendering",
            "pypdfVersion": pypdf_version,
            "renderer": f"MuPDF {pymupdf.VersionBind}",
            "source": str(args.source.resolve()),
            "saved": str(args.saved.resolve()),
            "pageCount": len(saved.pages),
            "originalPageTextPreserved": True,
            "annotations": verified,
            "renderDimensions": [width, height],
            "changedPixelCount": changed,
            "differenceBoundingBox": [min(xs), min(ys), max(xs) + 1, max(ys) + 1],
            "sourceRender": source_png.name,
            "savedRender": saved_png.name,
        }
    finally:
        source_doc.close()
        saved_doc.close()

    (args.output / "independent-drawing.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
