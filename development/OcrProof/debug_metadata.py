import sys,os,tempfile
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/redaction-pymupdf').resolve()))
import pymupdf
src=Path('development/fixtures/corpus-v1/edge/scan-real-text.pdf');d=pymupdf.open(src); metadata=dict(d.metadata);pix=d[0].get_pixmap(dpi=300,alpha=False);data=pix.pdfocr_tobytes(language='eng',tessdata='development/tools/ocr-runtime');c=pymupdf.open(stream=data,filetype='pdf');print('source',metadata);print('before',dict(c.metadata)); c.set_metadata(metadata);print('set',dict(c.metadata)); f=tempfile.mktemp(suffix='.pdf');c.save(f,garbage=4,deflate=True);c.close();r=pymupdf.open(f);print('saved',dict(r.metadata)); print('eq',dict(r.metadata)==metadata)
