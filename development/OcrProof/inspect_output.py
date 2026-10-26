import sys
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/redaction-pymupdf').resolve()))
import pymupdf
p=next(Path('development/artifacts').glob('ocr-proof-051b11dd*/searchable.pdf'))
d=pymupdf.open(p); print(d[0].get_texttrace()[:1]); print(d[0].get_images(full=True)); print(d[0].get_image_rects(d[0].get_images(full=True)[0][0]));
