import sys
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/redaction-pymupdf').resolve()))
import pymupdf
d=pymupdf.open('development/fixtures/corpus-v1/edge/scan-real-text.pdf');p=d[0];x=p.get_images(full=True)[0][0];print(p.get_image_rects(x,transform=True));print(d.xref_get_key(d.pdf_catalog(), 'StructTreeRoot'));print('info',d.xref_get_key(d.pdf_catalog(),'Metadata'),d.xref_get_key(d.pdf_catalog(),'Lang')); pix=pymupdf.Pixmap(d,x);print(pix.colorspace.name,pix.n,pix.alpha,pix.width,pix.height,pix.xres,pix.yres)
