from __future__ import annotations
import hashlib, json, sys, uuid
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'development/tools/redaction-pymupdf'))
import pymupdf

SOURCE = ROOT / 'development/fixtures/corpus-v1/edge/scan-real-text.pdf'
TRAINING_DATA = ROOT / 'development/tools/ocr-runtime'
RUN = ROOT / 'development/artifacts' / f'ocr-proof-{uuid.uuid4()}'
RUN.mkdir(parents=True)
SEARCHABLE_PDF = RUN / 'searchable.pdf'
TEXT_PATH = RUN / 'recognized.txt'
BOXES_PATH = RUN / 'word-boxes.json'
RESULT_PATH = RUN / 'result.json'

source_hash = hashlib.sha256(SOURCE.read_bytes()).hexdigest()
source_doc = pymupdf.open(SOURCE)
if source_doc.page_count != 1:
    raise SystemExit('Proof accepts only the frozen one-page scan fixture')
source_page = source_doc[0]
if source_page.get_text().strip() or len(source_page.get_images(full=True)) != 1:
    raise SystemExit('Proof requires a single image-only page; refusing mixed/digital PDF')
source_rect = source_page.rect
source_pix = source_page.get_pixmap(dpi=300, alpha=False)
# Native MuPDF OCR consumes the frozen scan and creates a new searchable PDF.
# The original fixture remains untouched; this helper does not overwrite inputs.
SEARCHABLE_PDF.write_bytes(source_pix.pdfocr_tobytes(
    language='eng', tessdata=str(TRAINING_DATA)))
source_doc.close()

result_doc = pymupdf.open(SEARCHABLE_PDF)
if result_doc.page_count != 1:
    raise SystemExit('OCR output page-count mismatch')
result_page = result_doc[0]
recognized = result_page.get_text(sort=True)
TEXT_PATH.write_text(recognized, encoding='utf-8')
words = result_page.get_text('words', sort=True)
boxes = [
    {'x0': x0, 'y0': y0, 'x1': x1, 'y1': y1, 'text': text,
     'block': block, 'line': line, 'word': word}
    for x0, y0, x1, y1, text, block, line, word in words
]
BOXES_PATH.write_text(json.dumps(boxes, ensure_ascii=False, indent=2), encoding='utf-8')
result_rect = result_page.rect
result_pix = result_page.get_pixmap(dpi=300, alpha=False)
pixels_identical = (source_pix.width == result_pix.width and
                    source_pix.height == result_pix.height and
                    source_pix.samples == result_pix.samples)
geometry_identical = (abs(source_rect.width - result_rect.width) < .01 and
                      abs(source_rect.height - result_rect.height) < .01)
report = {
    'engine': 'PyMuPDF/MuPDF native OCR', 'engine_version': pymupdf.__version__,
    'language': 'eng', 'tesseract_executable_available': False,
    'source_fixture': str(SOURCE.relative_to(ROOT)),
    'source_sha256': source_hash, 'source_page_count': 1,
    'source_has_existing_text': False, 'source_image_count': 1,
    'source_page_size_points': [source_rect.width, source_rect.height],
    'traineddata_source_commit': '87416418657359cb625c412a48b6e1d6d41c29bd',
    'traineddata_sha256': hashlib.sha256((TRAINING_DATA / 'eng-pinned.traineddata').read_bytes()).hexdigest(),
    'traineddata_bytes': (TRAINING_DATA / 'eng-pinned.traineddata').stat().st_size,
    'output_page_count': result_doc.page_count,
    'output_page_size_points': [result_rect.width, result_rect.height],
    'geometry_identical': geometry_identical,
    'rendered_pixels_identical_at_300dpi': pixels_identical,
    'recognized_text_nonempty': bool(recognized.strip()),
    'recognized_text': recognized,
    'word_box_count': len(boxes),
    'searchable_pdf_sha256': hashlib.sha256(SEARCHABLE_PDF.read_bytes()).hexdigest(),
    'files': {'searchable_pdf': str(SEARCHABLE_PDF.relative_to(ROOT)),
              'recognized_text': str(TEXT_PATH.relative_to(ROOT)),
              'word_boxes': str(BOXES_PATH.relative_to(ROOT))},
}
RESULT_PATH.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
if not recognized.strip() or not pixels_identical or not geometry_identical:
    raise SystemExit('OCR proof acceptance failed')
