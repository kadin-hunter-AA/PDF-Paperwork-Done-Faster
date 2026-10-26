import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent/'python-packages'))
import json, hashlib, shutil
from PIL import Image,ImageDraw,ImageFont
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pypdf import PdfReader,PdfWriter

root=Path(sys.argv[1]);root.mkdir(parents=True,exist_ok=True)
def create():
    font=Path('C:/Windows/Fonts/arial.ttf')
    pdfmetrics.registerFont(TTFont('CorpusArial',str(font)))
    c=canvas.Canvas(str(root/'ligature-mapped.pdf'),pagesize=(612,792))
    c.setFont('CorpusArial',20);c.drawString(60,700,'Ligature: ﬁ and fi');c.drawString(60,650,'Unicode: Café Ω Ж');c.showPage();c.save()
    w=PdfWriter(clone_from=str(root/'ligature-mapped.pdf'))
    removed=0
    for f in w.pages[0]['/Resources']['/Font'].values():
        obj=f.get_object()
        if '/ToUnicode' in obj:del obj['/ToUnicode'];removed+=1
    assert removed==1
    with (root/'missing-unicode-map.pdf').open('xb') as f:w.write(f)
    image=Image.new('RGB',(1224,1584),'white');draw=ImageDraw.Draw(image)
    for i,line in enumerate(['Synthetic scanned text','Invoice number 12345','Total amount 678.90','No hidden searchable text']):
        draw.text((100,160+i*100),line,font=ImageFont.truetype(str(font),44),fill='black')
    scan=root/'scan-text.png';image.save(scan)
    c=canvas.Canvas(str(root/'scan-real-text.pdf'),pagesize=(612,792));c.drawImage(str(scan),0,0,width=612,height=792);c.showPage();c.save()
    public=Path(__file__).parent.parent/'fixtures/public'
    shutil.copyfile(public/'hello_world.pdf',root/'public-hello-world.pdf')
    shutil.copyfile(public/'provenance.json',root/'public-provenance.json')
    shutil.copyfile(public/'LICENSE.pdfium',root/'LICENSE.pdfium')

def inspect():
    results=[]
    for suffix in ('','-saved'):
        r=PdfReader(root/f'ligature-mapped{suffix}.pdf');text=r.pages[0].extract_text()
        assert 'ﬁ' in text and 'Café Ω Ж' in text
        results.append(f'ligature{suffix}: ligature and Unicode mappings intact')
        r=PdfReader(root/f'missing-unicode-map{suffix}.pdf')
        fonts=r.pages[0]['/Resources']['/Font'];assert all('/ToUnicode' not in f.get_object() for f in fonts.values())
        results.append(f'missing-map{suffix}: intentionally absent ToUnicode preserved; editing unsafe without mapping checks')
        r=PdfReader(root/f'scan-real-text{suffix}.pdf');assert not r.pages[0].extract_text().strip()
        assert any(f.get_object().get('/Subtype')=='/Image' for f in r.pages[0]['/Resources']['/XObject'].values())
        results.append(f'scan{suffix}: readable pixels stored as image; no hidden text')
        r=PdfReader(root/f'public-hello-world{suffix}.pdf');assert 'Hello, world!' in r.pages[0].extract_text()
        results.append(f'public{suffix}: upstream PDFium fixture text retained')
    (root/'edge-results.json').write_text(json.dumps({'checks':results,'fixtureSha256':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in root.glob('*.pdf')},'status':'PASSED','pending':['OCR accuracy not tested','general nested editing unresolved','full P0 acceptance coverage']},indent=2))
    preview=PdfWriter()
    for name in ('ligature-mapped-saved.pdf','scan-real-text-saved.pdf','public-hello-world-saved.pdf'):preview.append(str(root/name))
    with (root/'edge-preview.pdf').open('xb') as target:preview.write(target)
    assert len(PdfReader(root/'edge-preview.pdf').pages)==3
    print('PASS: independent ligature, missing-map, scanned-text and public fixture checks.')

if sys.argv[2]=='create':create()
else:inspect()
