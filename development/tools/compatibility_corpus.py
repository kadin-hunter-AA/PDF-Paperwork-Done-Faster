"""Synthetic compatibility fixtures; independent inspection after native save."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent / 'python-packages'))
import hashlib, json
from reportlab.pdfgen import canvas
from pypdf import PdfReader, PdfWriter
from pypdf.generic import (DictionaryObject, NameObject, TextStringObject, ArrayObject,
                           NumberObject, RectangleObject, DecodedStreamObject)

root = Path(sys.argv[1]).resolve()
mode = sys.argv[2]
root.mkdir(parents=True, exist_ok=True)

def write(writer, name):
    with (root / name).open('xb') as target:
        writer.write(target)

def create():
    base = root / 'interactive-base.pdf'
    c = canvas.Canvas(str(base), pagesize=(612,792))
    c.setFont('Helvetica',22); c.drawString(50,735,'Interactive document fixtures')
    c.setFont('Helvetica',12); c.drawString(50,690,'Editable field - initial value: Corpus value')
    c.acroForm.textfield(name='sample_name', value='Corpus value', x=50,y=640,
                        width=300,height=30, fontSize=14, borderWidth=1,
                        forceBorder=True)
    c.drawString(50,600,'Internal link: jumps to this page')
    c.bookmarkPage('start');c.addOutlineEntry('Corpus bookmark','start')
    c.linkRect('Internal fixture link','start',(45,590,320,620),relative=0,thickness=1)
    c.drawString(50,545,'Attachment: fixture-note.txt (synthetic text)')
    c.drawString(50,510,'Visible optional layer: mint rectangle below')
    c.drawString(50,390,'Unsigned signature field below - no certificate or signature')
    c.showPage();c.save()
    w=PdfWriter(clone_from=str(base))
    w.add_attachment('fixture-note.txt',b'Synthetic compatibility attachment.\n')
    ocg=w._add_object(DictionaryObject({NameObject('/Type'):NameObject('/OCG'),NameObject('/Name'):TextStringObject('Fixture layer')}))
    w.root_object[NameObject('/OCProperties')]=DictionaryObject({NameObject('/OCGs'):ArrayObject([ocg]),NameObject('/D'):DictionaryObject({NameObject('/Order'):ArrayObject([ocg]),NameObject('/ON'):ArrayObject([ocg]),NameObject('/OFF'):ArrayObject([])})})
    resources=w.pages[0]['/Resources'].get_object()
    resources[NameObject('/Properties')]=DictionaryObject({NameObject('/FixtureLayer'):ocg})
    layer=DecodedStreamObject();layer.set_data(b'/OC /FixtureLayer BDC q 0.2 0.8 0.65 rg 50 440 300 40 re f Q EMC\n')
    existing=w.pages[0].raw_get('/Contents')
    w.pages[0][NameObject('/Contents')]=ArrayObject([existing,w._add_object(layer)])
    sig=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/Widget'),NameObject('/FT'):NameObject('/Sig'),NameObject('/T'):TextStringObject('unsigned_signature'),NameObject('/Rect'):RectangleObject([50,330,350,375]),NameObject('/F'):NumberObject(4),NameObject('/P'):w.pages[0].indirect_reference})
    sig_ref=w._add_object(sig)
    w.root_object['/AcroForm']['/Fields'].append(sig_ref)
    w.pages[0]['/Annots'].append(sig_ref)
    write(w,'interactive-features.pdf')

    graphics=root/'graphics-base.pdf'
    c=canvas.Canvas(str(graphics),pagesize=(612,792))
    c.setFont('Helvetica',20);c.drawString(75,705,'Transparency, clipping and page boxes')
    c.setFillColorRGB(.45,.2,.7);c.rect(90,480,240,120,fill=1,stroke=0)
    c.saveState();c.setFillAlpha(.45);c.setFillColorRGB(.1,.9,.7);c.rect(210,440,240,120,fill=1,stroke=0);c.restoreState()
    c.setFillColorRGB(0,0,0);c.setFont('Helvetica',12);c.drawString(90,410,'Overlap blends mint and plum with 45% opacity.')
    c.saveState();p=c.beginPath();p.rect(90,250,300,100);c.clipPath(p,stroke=1,fill=0)
    c.setFillColorRGB(.9,.65,.2);c.rect(30,190,480,200,fill=1,stroke=0)
    c.setFillColorRGB(0,0,0);c.drawString(110,300,'Clipped gold rectangle stays within this frame.');c.restoreState()
    c.showPage();c.save()
    w=PdfWriter(clone_from=str(graphics));page=w.pages[0]
    page.cropbox=RectangleObject([36,36,576,756]);page.trimbox=RectangleObject([54,54,558,738]);page.bleedbox=RectangleObject([45,45,567,747])
    write(w,'graphics-boxes.pdf')

    password_base=root/'password-base.pdf'
    c=canvas.Canvas(str(password_base),pagesize=(612,792));c.setFont('Helvetica',20);c.drawString(60,700,'Password protected fixture');c.setFont('Helvetica',12);c.drawString(60,650,'Test password: corpus-user');c.showPage();c.save()
    w=PdfWriter(clone_from=str(password_base));w.encrypt(user_password='corpus-user',owner_password='corpus-owner',algorithm='AES-256')
    write(w,'password-aes256.pdf')

def inspect():
    checks=[]
    for suffix in ('','-saved'):
        r=PdfReader(root/f'interactive-features{suffix}.pdf')
        fields=r.get_fields();assert fields['sample_name']['/V']=='Corpus value'
        assert fields['unsigned_signature']['/FT']=='/Sig' and '/V' not in fields['unsigned_signature']
        widgets=[a.get_object() for a in r.pages[0]['/Annots'] if a.get_object()['/Subtype']=='/Widget']
        value_widgets=[a for a in widgets if a.get('/T')=='sample_name']
        assert len(value_widgets)==1 and value_widgets[0]['/V']=='Corpus value'
        assert len(value_widgets[0]['/AP']['/N'].get_object().get_data())>0
        assert any(a.get_object()['/Subtype']=='/Link' for a in r.pages[0]['/Annots'])
        assert r.outline and r.outline[0].title=='Corpus bookmark'
        assert r.attachments['fixture-note.txt'][0]==b'Synthetic compatibility attachment.\n'
        assert r.trailer['/Root']['/OCProperties']['/OCGs'][0].get_object()['/Name']=='Fixture layer'
        checks.append(f'interactive{suffix}: canonical fields/widgets/appearance, unsigned signature field, link, bookmark, attachment and layer retained')
        r=PdfReader(root/f'graphics-boxes{suffix}.pdf');page=r.pages[0]
        assert list(page.cropbox)==[36,36,576,756] and list(page.trimbox)==[54,54,558,738] and list(page.bleedbox)==[45,45,567,747]
        assert '/ExtGState' in page['/Resources'] and b'W' in page.get_contents().get_data()
        checks.append(f'graphics{suffix}: crop/trim/bleed, transparency state and clipping retained')
        r=PdfReader(root/f'password-aes256{suffix}.pdf');assert r.is_encrypted
        assert not r.decrypt('wrong-password');assert r.decrypt('corpus-user')
        assert 'Password protected fixture' in r.pages[0].extract_text()
        checks.append(f'password{suffix}: encryption and correct password retained')
    manifest={p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in root.glob('*.pdf')}
    (root/'independent-compatibility.json').write_text(json.dumps({'checks':checks,'fixtureSha256':manifest,'status':'PASSED','pending':['signed certificate fixtures','ligatures/missing Unicode mappings','licensed public fixtures','full P0 feature fixture mapping','general nested edit persistence']},indent=2),encoding='utf8')
    print('PASS: independent parser confirms interactive structures, graphics and password preservation.')

if mode=='create':create()
elif mode=='inspect':inspect()
else:raise ValueError('mode must be create or inspect')
