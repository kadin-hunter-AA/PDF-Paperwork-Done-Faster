from pathlib import Path
import struct
import hashlib
import json
import io

import numpy as np
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
ASSETS = ROOT / "development" / "App.Desktop" / "Assets"
WINDOWS = Path(__file__).resolve().parent


def png_metrics(path: Path):
    image = Image.open(path).convert("RGBA")
    alpha = np.asarray(image.getchannel("A"))
    bands = {
        "alpha_0": int(np.count_nonzero(alpha == 0)),
        "alpha_1_7": int(np.count_nonzero((alpha > 0) & (alpha < 8))),
        "alpha_8_31": int(np.count_nonzero((alpha >= 8) & (alpha < 32))),
        "alpha_32_127": int(np.count_nonzero((alpha >= 32) & (alpha < 128))),
        "alpha_128_254": int(np.count_nonzero((alpha >= 128) & (alpha < 255))),
        "alpha_255": int(np.count_nonzero(alpha == 255)),
    }
    bbox = image.getchannel("A").getbbox()
    return image, bands, bbox


def low_alpha_components(image: Image.Image):
    alpha = np.asarray(image.getchannel("A"))
    ys, xs = np.nonzero((alpha > 0) & (alpha < 8))
    remaining = set((ys.astype(np.int64) * image.width + xs).tolist())
    components = []
    above_threshold = alpha >= 8
    while remaining:
        start = remaining.pop()
        stack = [start]
        size = 0
        component_pixels = []
        touches_opaque_edge = False
        while stack:
            pixel = stack.pop()
            size += 1
            component_pixels.append(pixel)
            y, x = divmod(pixel, image.width)
            for ny in range(max(0, y - 1), min(image.height, y + 2)):
                base = ny * image.width
                for nx in range(max(0, x - 1), min(image.width, x + 2)):
                    neighbor = base + nx
                    if above_threshold[ny, nx]:
                        touches_opaque_edge = True
                    if neighbor in remaining:
                        remaining.remove(neighbor)
                        stack.append(neighbor)
        components.append((size, touches_opaque_edge, tuple(component_pixels)))
    components.sort(reverse=True)
    attached = sum(touches for _, touches, _ in components)
    isolated_sizes = [size for size, touches, _ in components if not touches]
    removable = np.zeros((image.height, image.width), dtype=bool)
    for size, touches, pixels in components:
        if not touches and size <= 64:
            for pixel in pixels:
                removable[divmod(pixel, image.width)] = True
    return len(components), [size for size, _, _ in components[:10]], attached, len(components) - attached, isolated_sizes, removable


def inspect_ico(path: Path):
    data = path.read_bytes()
    reserved, kind, count = struct.unpack_from("<HHH", data, 0)
    assert (reserved, kind) == (0, 1), "invalid ICO header"
    frames = []
    ranges = []
    directory_end = 6 + 16 * count
    assert directory_end <= len(data), "truncated ICO directory"
    for i in range(count):
        offset = 6 + i * 16
        width, height, colors, reserved_byte, planes, bpp, length, payload_offset = struct.unpack_from(
            "<BBBBHHII", data, offset
        )
        width = width or 256
        height = height or 256
        payload = data[payload_offset : payload_offset + length]
        assert colors == 0 and reserved_byte == 0, f"invalid reserved ICO fields in frame {i}"
        assert planes == 1 and bpp == 32, f"unexpected ICO planes/bpp in frame {i}"
        assert payload_offset >= directory_end and payload_offset + length <= len(data), f"invalid payload bounds for frame {i}"
        assert len(payload) == length, f"truncated ICO frame {i}"
        assert payload[:8] == b"\x89PNG\r\n\x1a\n", f"frame {i} is not PNG-backed"
        image = Image.open(io.BytesIO(payload))
        assert image.size == (width, height), f"frame {i} directory/payload size mismatch"
        frames.append((width, height, length, payload_offset))
        ranges.append((payload_offset, payload_offset + length))
        standalone = (ASSETS / f"PdfEditor-{width}.png").read_bytes()
        assert payload == standalone, f"ICO frame {width} does not byte-match its standalone PNG"
    assert sorted({w for w, h, _, _ in frames}) == [16, 20, 24, 32, 40, 48, 64, 128, 256]
    assert all(w == h for w, h, _, _ in frames)
    assert len(frames) == count == 9
    assert len(set(frames)) == count
    ordered = sorted(ranges)
    assert all(ordered[i][1] <= ordered[i + 1][0] for i in range(len(ordered) - 1)), "overlapping ICO payloads"
    print(f"ICO: {len(data)} bytes; {count} valid PNG frames: " + ", ".join(f"{w}x{height}" for w, height, _, _ in frames))
    return frames


def review_sheet():
    sizes = [16, 20, 24, 30, 32, 40, 48, 64]
    cell_w, cell_h, header = 100, 100, 20
    sheet = Image.new("RGB", (cell_w * len(sizes), (cell_h + header) * 2), "white")
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("segoeui.ttf", 11)
    except OSError:
        font = ImageFont.load_default()
    for row, bg in enumerate(("#F5F6FA", "#171928")):
        for col, size in enumerate(sizes):
            x, y = col * cell_w, row * (cell_h + header)
            draw.rectangle((x, y, x + cell_w - 1, y + cell_h + header - 1), fill=bg)
            color = "#171928" if row == 0 else "#F5F6FA"
            draw.text((x + 4, y + 3), f"{size}px", font=font, fill=color)
            icon = Image.open(ASSETS / f"PdfEditor-{size}.png").convert("RGBA")
            assert icon.size == (size, size)
            sheet.paste(icon, (x + (cell_w - size) // 2, y + header + (cell_h - size) // 2), icon)
    sheet.save(WINDOWS / "icon-size-review.png")
    print(f"Review sheet: {WINDOWS / 'icon-size-review.png'} ({sheet.width}x{sheet.height}, icons at native size)")


def main():
    source = ROOT / "design" / "logos" / "selected-pdf-quill.png"
    master = WINDOWS / "production-master.png"
    images = {}
    metrics = {"source": {}, "production_master": {}, "png_exports": {}, "ico": {}}
    for key, path in (("source", source), ("production_master", master)):
        image, bands, bbox = png_metrics(path)
        print(f"{path.relative_to(ROOT)}: {image.width}x{image.height}; alpha bbox={bbox}; {bands}")
        components, largest, attached, isolated, isolated_sizes, removable = low_alpha_components(image)
        print(f"  alpha 1-7 components (8-connected): {components}; edge-attached={attached}; isolated={isolated}; largest sizes={largest}; isolated pixels={sum(isolated_sizes)}; isolated sizes={sorted(isolated_sizes, reverse=True)[:10]}")
        images[key] = image
        metrics[key] = {"path": str(path.relative_to(ROOT)), "dimensions": list(image.size), "alpha_bbox": list(bbox) if bbox else None, "alpha_bands": bands, "low_alpha_components_8conn": components, "edge_attached_components": attached, "isolated_components": isolated, "isolated_pixels": sum(isolated_sizes), "largest_component_sizes": largest}
        if key == "source":
            expected_removal = removable
    source_rgba = np.asarray(images["source"])
    master_rgba = np.asarray(images["production_master"])
    assert source_rgba.shape == master_rgba.shape, "master dimensions differ from source"
    assert np.array_equal(source_rgba[:, :, :3], master_rgba[:, :, :3]), "master RGB differs from source"
    changed_alpha = source_rgba[:, :, 3] != master_rgba[:, :, 3]
    assert np.array_equal(changed_alpha, expected_removal), "master alpha changes are outside eligible detached specks"
    assert np.all(master_rgba[:, :, 3][expected_removal] == 0), "eligible alpha specks were not cleared"
    assert np.all(master_rgba[:, :, 3][~expected_removal] == source_rgba[:, :, 3][~expected_removal]), "retained alpha differs from source"
    metrics["cleanup_assertions"] = {"rgb_identical_every_pixel": True, "alpha_changed_only_in_eligible_detached_components": True, "pixels_cleared": int(expected_removal.sum()), "eligible_component_limit_px": 64, "alpha_cutoff_exclusive": 8}

    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 150, 256):
        path = ASSETS / f"PdfEditor-{size}.png"
        image, bands, bbox = png_metrics(path)
        assert image.size == (size, size), f"wrong PNG dimensions: {path}"
        print(f"{path.name}: {size}x{size}; alpha bbox={bbox}; {bands}")
        metrics["png_exports"][str(size)] = {"path": str(path.relative_to(ROOT)), "dimensions": list(image.size), "alpha_bbox": list(bbox) if bbox else None, "alpha_bands": bands, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
    ico_path = ASSETS / "PdfEditor.ico"
    frames = inspect_ico(ico_path)
    metrics["ico"] = {"path": str(ico_path.relative_to(ROOT)), "bytes": ico_path.stat().st_size, "frame_count": len(frames), "frames": [{"width": w, "height": h, "payload_bytes": n, "payload_offset": o} for w, h, n, o in frames], "header_reserved": 0, "header_type": 1, "planes": 1, "bits_per_pixel": 32}
    hashes = {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in [source, master, WINDOWS / "export-icons.ps1", Path(__file__)] + sorted(ASSETS.glob("PdfEditor-*.png")) + [ico_path]}
    metrics["sha256"] = hashes
    review_sheet()
    output = WINDOWS / "icon-audit-metrics.json"
    output.write_text(json.dumps(metrics, indent=2) + "\n", encoding="utf-8")
    print(f"Audit metrics: {output}")


if __name__ == "__main__":
    main()
