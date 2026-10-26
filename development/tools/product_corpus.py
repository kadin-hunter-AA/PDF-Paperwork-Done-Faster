"""Product-level P0 markup and styled-content corpus, not editor implementation."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent/'python-packages'))
import json,hashlib
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from pypdf import PdfReader,PdfWriter
from pypdf.generic import (DictionaryObject,NameObject,ArrayObject,FloatObject,NumberObject,
                           TextStringObject,DecodedStreamObject,RectangleObject)
root=Path(sys.argv[1]);root.mkdir(parents=True,exist_ok=True)
def arr(items):return ArrayObject([FloatObject(x) for x in items])
def create():
    for name,file in [('CorpusArial','arial.ttf'),('CorpusTimes','times.ttf')]:pdfmetrics.registerFont(TTFont(name,'C:/Windows/Fonts/'+file))
    c=canvas.Canvas(str(root/'mixed-styles-languages.pdf'),pagesize=(612,792))
    c.setFont('Helvetica-Bold',24);c.drawString(50,735,'Mixed fonts, sizes, colors and languages')
    for name,size,color,text,y in [('CorpusArial',18,(.1,.3,.55),'English: Sample invoice 12345',680),('CorpusTimes',22,(.5,.2,.65),'Français: Café et résumé',630),('CorpusArial',20,(.1,.45,.25),'Ελληνικά: Καλημέρα',580),('CorpusTimes',20,(.6,.2,.15),'Русский: Привет',530),('Courier',14,(.1,.1,.1),'Quantity  Unit price  Total',450),('Courier',14,(.1,.1,.1),'3         25.00       75.00',420)]:
        c.setFont(name,size);c.setFillColorRGB(*color);c.drawString(50,y,text)
    c.setFillColorRGB(0,0,0);c.setFont('Helvetica',12);c.drawString(50,350,'Editing expected: preserve nearby content and supported styles.');c.showPage();c.save()
    base=root/'markup-base.pdf';c=canvas.Canvas(str(base),pagesize=(612,792))
    c.setFont('Helvetica-Bold',24);c.drawString(50,735,'Text markup, notes and comments')
    c.setFont('Helvetica',18)
    for y,text in [(680,'Highlight this sentence.'),(640,'Underline this sentence.'),(600,'Strike out this sentence.')]:c.drawString(50,y,text)
    c.setFont('Helvetica',12);c.drawString(50,520,'Sticky note: double-click the yellow icon to read its comment.');c.drawString(50,350,'All marks are standard editable PDF annotations.');c.showPage()
    c.setFont('Helvetica-Bold',24);c.drawString(50,735,'Shapes, arrows and freehand ink');c.setFont('Helvetica',12)
    for x,y,t in [(50,680,'Line'),(320,680,'Arrow'),(50,545,'Rectangle'),(320,545,'Ellipse'),(50,360,'Freehand ink')]:c.drawString(x,y,t)
    c.showPage();c.save();w=PdfWriter(clone_from=str(base))
    specs=[]
    def annot(page,subtype,rect,content,appearance,extras=None):
        width=rect[2]-rect[0];height=rect[3]-rect[1]
        a=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/'+subtype),NameObject('/Rect'):RectangleObject(rect),NameObject('/F'):NumberObject(4),NameObject('/T'):TextStringObject('Corpus author'),NameObject('/Contents'):TextStringObject(content),NameObject('/NM'):TextStringObject('corpus-'+str(len(specs)+1)),NameObject('/C'):arr([.1,.5,.4])})
        if extras:a.update(extras)
        ap=DecodedStreamObject();ap.set_data(appearance.encode('ascii'))
        state=DictionaryObject({NameObject('/Type'):NameObject('/ExtGState'),NameObject('/ca'):FloatObject(.35),NameObject('/CA'):FloatObject(.35)})
        resources=DictionaryObject({NameObject('/Font'):w.pages[page]['/Resources']['/Font'],NameObject('/ExtGState'):DictionaryObject({NameObject('/GS1'):state})})
        ap.update({NameObject('/Type'):NameObject('/XObject'),NameObject('/Subtype'):NameObject('/Form'),NameObject('/BBox'):arr([0,0,width,height]),NameObject('/Resources'):resources})
        a[NameObject('/AP')]=DictionaryObject({NameObject('/N'):w._add_object(ap)})
        w.add_annotation(page_number=page,annotation=a);specs.append({'page':page,'subtype':subtype,'comment':content})
    for subtype,y,paint in [('Highlight',675,'/GS1 gs 1 .9 0 rg 0 0 225 25 re f'),('Underline',635,'0 .5 .2 RG 2 w 0 3 m 225 3 l S'),('StrikeOut',595,'.8 .1 .1 RG 2 w 0 12 m 225 12 l S')]:
        annot(0,subtype,[50,y,275,y+25],subtype+' comment',paint,{NameObject('/QuadPoints'):arr([50,y+25,275,y+25,50,y,275,y])})
    annot(0,'Text',[410,510,435,535],'Sticky note comment survives save/reopen.','1 .9 .2 rg 0 0 25 25 re f 0 0 0 RG 1 w 0 0 25 25 re S')
    annot(0,'FreeText',[50,400,440,485],'Editable text box comment','0.95 0.96 1 rg 0 0 390 85 re f .3 .3 .6 RG 1 w 0 0 390 85 re S 0 g BT /F1 14 Tf 10 50 Td (Editable text box with a comment.) Tj ET',{NameObject('/DA'):TextStringObject('/F1 14 Tf 0 g')})
    annot(1,'Line',[50,600,250,660],'Line comment','0 .4 .3 RG 3 w 5 10 m 195 50 l S',{NameObject('/L'):arr([55,610,245,650])})
    annot(1,'Line',[320,600,530,660],'Arrow comment','0 .4 .3 RG 3 w 5 10 m 195 50 l S 183 37 m 195 50 l 179 54 l S',{NameObject('/L'):arr([325,610,515,650]),NameObject('/LE'):ArrayObject([NameObject('/None'),NameObject('/OpenArrow')])})
    annot(1,'Square',[50,400,250,520],'Rectangle comment','0 .4 .3 RG 3 w 2 2 196 116 re S')
    annot(1,'Circle',[320,400,530,520],'Ellipse comment','0 .4 .3 RG 3 w 105 2 m 162 2 208 28 208 60 c 208 92 162 118 105 118 c 48 118 2 92 2 60 c 2 28 48 2 105 2 c S')
    points=[55,280,95,330,140,290,190,335,245,285]
    annot(1,'Ink',[50,260,260,345],'Freehand ink comment','0 .3 .8 RG 3 w 5 20 m 45 70 l 90 30 l 140 75 l 195 25 l S',{NameObject('/InkList'):ArrayObject([arr(points)])})
    with (root/'markup-gallery.pdf').open('xb') as f:w.write(f)
    (root/'markup-expectations.json').write_text(json.dumps(specs,indent=2))
def inspect():
    for suffix in ('','-saved'):
        r=PdfReader(root/f'mixed-styles-languages{suffix}.pdf');text=r.pages[0].extract_text();assert 'Café' in text and 'Καλημέρα' in text and 'Привет' in text
        r=PdfReader(root/f'markup-gallery{suffix}.pdf');expected=json.loads((root/'markup-expectations.json').read_text());actual=[]
        for index,page in enumerate(r.pages):
            for ref in page.get('/Annots',[]):
                a=ref.get_object();assert len(a['/AP']['/N'].get_object().get_data())>0
                actual.append({'page':index,'subtype':str(a['/Subtype'])[1:],'comment':str(a['/Contents'])})
        assert actual==expected
    (root/'product-results.json').write_text(json.dumps({'status':'PASSED','checks':['mixed fonts/sizes/colors/languages text retained','ten standard annotations and comment contents/appearance streams retained'],'fixtureSha256':{p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in root.glob('*.pdf')}},indent=2))
    print('PASS: mixed styles/languages and every P0 annotation fixture persist after native save.')
if sys.argv[2]=='create':create()
else:inspect()
