import sys
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/python-packages').resolve()))
from pypdf import PdfReader
p=PdfReader('development/fixtures/corpus-v1/edge/scan-real-text.pdf',strict=True).pages[0]
print(p['/Resources']);print(p['/Contents'].get_object().get_data()[:1000]);
from pypdf.generic import ContentStream
c=ContentStream(p['/Contents'],p.pdf);print([(x,o) for x,o in c.operations]);
x=p['/Resources']['/XObject']; print(x)
for k,v in x.items():
 ob=v.get_object(); print(k,ob.get('/Subtype'),ob.get('/Resources'),ob.get_data()[:1000] if ob.get('/Subtype')=='/Form' else '')
