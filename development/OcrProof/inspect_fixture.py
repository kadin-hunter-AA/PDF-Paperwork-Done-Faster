import sys, pathlib
r=pathlib.Path('development/tools/redaction-pymupdf').resolve(); sys.path.insert(0,str(r)); import pymupdf
p='development/fixtures/corpus-v1/edge/scan-real-text.pdf'; d=pymupdf.open(p); print(d.metadata); print('toc',d.get_toc(),'layers',d.get_ocgs(),'embeds',d.embfile_count(),'page',d[0].rect,d[0].cropbox,d[0].mediabox,d[0].rotation,'images',d[0].get_images(full=True),'drawings',d[0].get_drawings(),'links',d[0].get_links(),'annots',list(d[0].annots() or []),'text',repr(d[0].get_text()));
