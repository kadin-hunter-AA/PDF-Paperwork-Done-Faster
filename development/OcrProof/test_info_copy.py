import sys,tempfile
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/python-packages').resolve()))
from pypdf import PdfReader,PdfWriter
src=PdfReader('development/fixtures/corpus-v1/edge/scan-real-text.pdf',strict=True)
writer=PdfWriter(clone_from='development/artifacts/ocr-validation-f9c8c761-7108-4e55-96c7-4b030b3f4086/candidate.pdf')
info=src.trailer.raw_get('/Info')
print(type(info),info)
clone=info.clone(writer)
print(type(clone),clone)
writer._info=clone
f='development/OcrProof/test_info_copy.pdf'
with open(f,'wb') as h: writer.write(h)
r=PdfReader(f,strict=True)
print('source',src.metadata);print('new',r.metadata)
