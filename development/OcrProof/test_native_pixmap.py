import sys
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/redaction-pymupdf').resolve()))
import pymupdf
s=Path('development/fixtures/corpus-v1/edge/scan-real-text.pdf'); d=pymupdf.open(s); page=d[0]; xref=page.get_images(full=True)[0][0]; pix=pymupdf.Pixmap(d,xref); print('src',pix.width,pix.height,pix.n,pix.alpha,pix.colorspace.name, pix.xres,pix.yres);pix.set_dpi(144,144);data=pix.pdfocr_tobytes(language='eng',tessdata='development/tools/ocr-runtime');o=pymupdf.open(stream=data,filetype='pdf');print('outpage',o[0].rect,'images',o[0].get_images(full=True));outpix=pymupdf.Pixmap(o[0].parent,o[0].get_images(full=True)[0][0]);print('outpix',outpix.width,outpix.height,outpix.n,outpix.alpha,outpix.colorspace.name,'dpi',outpix.xres,outpix.yres,'eqsamples',pix.samples==outpix.samples);print(repr(o[0].get_text()))
