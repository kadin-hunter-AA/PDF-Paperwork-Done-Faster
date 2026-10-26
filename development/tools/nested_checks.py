"""Nested-editor fixtures and independent structural/rendering acceptance checks."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).parent / "python-packages"))
from pypdf import PdfReader, PdfWriter
from pypdf.generic import (ArrayObject, BooleanObject, ContentStream, DecodedStreamObject,
                          DictionaryObject, FloatObject, NameObject, NumberObject, RectangleObject)


def obj(**values):
    return DictionaryObject({NameObject('/' + k): v for k, v in values.items()})


def numbers(values):
    return ArrayObject([FloatObject(v) for v in values])


def stream(data, **values):
    result = DecodedStreamObject(); result.update(obj(**values)); result.set_data(data)
    return result


def fixture(path, *, depth=2, operator="Tj", inherited=False, rotate=0, selected=0,
            edited=False, spacing=False, actual=False, text_clip=False, shared_resources=False, branch=False):
    w = PdfWriter()
    font = w._add_object(obj(Type=NameObject('/Font'), Subtype=NameObject('/Type1'), BaseFont=NameObject('/Courier')))
    image = w._add_object(stream(b'\x00\x99\xaa', Type=NameObject('/XObject'), Subtype=NameObject('/Image'),
                                Width=NumberObject(1), Height=NumberObject(1), ColorSpace=NameObject('/DeviceRGB'), BitsPerComponent=NumberObject(8)))
    alpha = obj(ca=FloatObject(.6), CA=FloatObject(.6), BM=NameObject('/Multiply'))
    resources = obj(Font=obj(F1=font), XObject=obj(Im=image), ExtGState=obj(Alpha=alpha))
    # Courier has fixed 600-unit advances. Expected output uses an explicit text
    # matrix for the following run, independently of the editor's TJ calculation.
    char_space, word_space = ((.7, 1.3) if spacing else (0, 0))
    target_y = 35
    init = b'' if inherited else b'/F1 12 Tf '
    init += f'{char_space} Tc {word_space} Tw 80 Tz 16 TL '.encode()
    if text_clip:
        init += b'7 Tr '
    before = b'(Before) Tj'
    old_width = 6 * (7.2 + char_space) * .8
    # Each operator starts at (20,35), including quote operators' implicit T*.
    start_y = target_y + (16 if operator in ("'", '"') else 0)
    if operator == 'TJ':
        before = b'[100 (Be) 45 (fore) -20] TJ'
        old_width -= (100 + 45 - 20) * .012 * .8
    elif operator == "'":
        before = b"(Before) '"
    elif operator == '"':
        before = f'{word_space} {char_space} (Before) "'.encode()
    start_x = 20 - (100 * .012 * .8 if operator == 'TJ' else 0)
    tail_x = 20 + old_width + 5000 * .012 * .8
    if edited:
        before = f'1 0 0 1 {start_x} {target_y} Tm (After edit) Tj 1 0 0 1 {tail_x} {target_y} Tm (Tail) Tj'.encode()
    else:
        before += b' [-5000] TJ (Tail) Tj'
    marks = b'/Span << /ActualText (Before) >> BDC ' if actual else b''
    end_marks = b' EMC' if actual else b''
    def leaf(changed):
        value = before
        if changed != edited:
            raise AssertionError('fixture leaf variant mismatch')
        return stream(b'q 0 0 450 105 re W n .93 .96 .98 rg 0 0 450 105 re f Q '
                      b'q /Alpha gs .8 .3 .1 rg 360 25 110 60 re f Q '
                      b'q 25 0 0 25 405 65 cm /Im Do Q '
                      b'q BT /F1 12 Tf 1 0 0 1 20 80 Tm (Leaf sibling) Tj ET Q '
                      b'q 0 0 445 105 re W n ' + marks + b'BT ' + init +
                      f'1 0 0 1 20 {start_y} Tm '.encode() + value + b' ET' + end_marks + b' Q',
                      Type=NameObject('/XObject'), Subtype=NameObject('/Form'), BBox=RectangleObject([0,0,445,105]),
                      Group=obj(S=NameObject('/Transparency'), I=BooleanObject(True), K=BooleanObject(False)),
                      **({} if inherited else {'Resources': resources}))
    # Build original hierarchy independently, so only the selected occurrence can
    # differ in the expected document. The other occurrences retain shared refs.
    if edited:
        original_path = path.with_name(path.stem + '-source.tmp.pdf')
        fixture(original_path, depth=depth, operator=operator, inherited=inherited,
                rotate=rotate, selected=selected, spacing=spacing, actual=actual,
                text_clip=text_clip, shared_resources=shared_resources, branch=branch)
        original_reader = PdfReader(original_path)
        original_top = original_reader.pages[0]['/Resources']['/XObject']['/Root'].clone(w)
    current = w._add_object(leaf(edited))
    for level in range(depth - 1):
        res = obj(Font=obj(F1=font), XObject=obj(Child=current, Im=image), ExtGState=obj(Alpha=alpha))
        commands = b'q 1 0 0 1 5 5 cm /Child Do Q'
        if branch:
            assert depth == 2
            if edited:
                res['/XObject'][NameObject('/Child')] = original_top['/Resources']['/XObject'].raw_get('/Child')
                res['/XObject'][NameObject('/Changed')] = current
            commands = b'q .48 0 0 .48 0 0 cm /Child Do Q q .48 0 0 .48 225 0 cm /'
            commands += b'Changed Do Q' if edited else b'Child Do Q'
        current = w._add_object(stream(commands,
            Type=NameObject('/XObject'), Subtype=NameObject('/Form'), BBox=RectangleObject([-10,-10,500,150]),
            Matrix=numbers([1,.01,-.02,1,0,0]), Resources=res))
    page_res = obj(Font=obj(F1=font), XObject=obj(Root=original_top if edited else current, Changed=current))
    if shared_resources:
        page_res = w._add_object(page_res)
    for page_index in range(2):
        page = w.add_blank_page(612,792)
        page[NameObject('/Resources')] = page_res
        page[NameObject('/Rotate')] = NumberObject(rotate if page_index == 0 else 0)
        page[NameObject('/CropBox')] = RectangleObject([10,10,602,782])
        data = b'BT /F1 18 Tf 45 735 Td (Nested editing preservation proof) Tj ET '
        data += b'BT /F1 12 Tf 45 710 Td (Text, image, clipping, transparency and sharing) Tj ET '
        for occurrence, y in enumerate([510,270]):
            label = 'SELECTED INSTANCE' if page_index == 0 and occurrence == selected else 'UNCHANGED SHARED INSTANCE'
            data += f'BT /F1 12 Tf 45 {y+155} Td ({label}) Tj ET '.encode()
            name = 'Changed' if edited and page_index == 0 and occurrence == selected else 'Root'
            data += f'q .1 .2 .4 rg BT /F1 12 Tf ET 1 0 0 1 45 {y} cm /{name} Do Q '.encode()
        page[NameObject('/Contents')] = w._add_object(stream(data))
    with path.open('xb') as f:
        w.write(f)
    if edited:
        original_path.unlink()


def create(root):
    cases = []
    variants = [
        ('shared-forms', {}),
        ('second-occurrence', {'selected':1, 'shared_resources':True}),
        ('deep-eight-levels', {'depth':8}),
        ('inherited-font-and-resources', {'inherited':True}),
        ('rotated-page', {'rotate':90}),
        ('positioned-TJ', {'operator':'TJ'}),
        ('quote', {'operator':"'"}),
        ('double-quote-spacing', {'operator':'"','spacing':True}),
        ('character-spacing', {'spacing':True}),
        ('shared-child-in-form', {'branch':True}),
    ]
    for name, options in variants:
        fixture(root / f'{name}.pdf', **options)
        fixture(root / f'{name}-expected.pdf', edited=True, **options)
        cases.append(dict(name=name, source=f'{name}.pdf', destination=f'{name}-edited.pdf',
                          pageIndex=0, formPath=[options.get('selected',0)]+([1] if options.get('branch') else [0]*(options.get('depth',2)-1)),
                          textIndex=1, expectedText='Before', replacement='After edit', reject=False))
    base = cases[0]
    for name, change in [
        ('bad-page', {'pageIndex':3}), ('bad-form', {'formPath':[20]}),
        ('image-is-not-form', {'formPath':[0,0,0]}),
        ('bad-text', {'textIndex':20}), ('stale-text', {'expectedText':'stale'}),
        ('stale-source', {'sourceSha256':'0'*64}), ('missing-glyph', {'replacement':'After \u03a9'}),
        ('empty-path', {'formPath':[]}), ('negative-index', {'formPath':[-1]}),
    ]:
        cases.append({**base, 'name':name, 'destination':f'{name}.pdf', 'reject':True, **change})
    for name, options in [('actual-text',{'actual':True}), ('text-clip',{'text_clip':True})]:
        fixture(root / f'{name}.pdf', **options)
        cases.append({**base, 'name':name, 'source':f'{name}.pdf', 'destination':f'{name}-edited.pdf','reject':True})
    # Embedded and genuine subset CID fonts from the frozen corpus; no fixture changes.
    for name in ['font-embedded-original', 'font-subset-original']:
        source = Path(__file__).parent.parent / 'fixtures/corpus-v1/engine' / f'{name}.pdf'
        r = PdfReader(source); w = PdfWriter()
        src = r.pages[0]
        form = stream(src.get_contents().get_data(), Type=NameObject('/XObject'), Subtype=NameObject('/Form'),
                      BBox=RectangleObject([0,0,612,792]), Resources=src['/Resources'].clone(w))
        page=w.add_blank_page(612,792)
        page[NameObject('/Resources')]=obj(XObject=obj(F=w._add_object(form)))
        page[NameObject('/Contents')]=w._add_object(stream(b'/F Do'))
        with (root/f'{name}.pdf').open('xb') as target: w.write(target)
        cases.append(dict(name=name,source=f'{name}.pdf',destination=f'{name}-edited.pdf',pageIndex=0,
                          formPath=[0],textIndex=0,expectedText='ABC',replacement='CABCABCAB',reject=False))
        cases.append({**cases[-1], 'name':name+'-greek-cyrillic', 'destination':name+'-greek-cyrillic.pdf',
                      'replacement':'ABC\u03a9\u0416', 'reject':name=='font-subset-original'})
    # Nested edits must never silently strip encryption or rewrite a signed PDF.
    protected=PdfWriter(clone_from=str(root/base['source']))
    protected.encrypt('test-user','test-owner',algorithm='AES-256')
    with (root/'encrypted.pdf').open('xb') as target: protected.write(target)
    cases.append({**base,'name':'encrypted','source':'encrypted.pdf','destination':'encrypted-edited.pdf','reject':True})
    signed=PdfWriter(clone_from=str(root/base['source']))
    field=obj(FT=NameObject('/Sig'), V=signed._add_object(obj(Type=NameObject('/Sig'))))
    signed.root_object[NameObject('/AcroForm')]=obj(Fields=ArrayObject([signed._add_object(field)]))
    with (root/'signed.pdf').open('xb') as target: signed.write(target)
    cases.append({**base,'name':'signed','source':'signed.pdf','destination':'signed-edited.pdf','reject':True})
    tagged=PdfWriter(clone_from=str(root/base['source']))
    tagged.root_object[NameObject('/StructTreeRoot')]=obj(Type=NameObject('/StructTreeRoot'))
    with (root/'tagged.pdf').open('xb') as target: tagged.write(target)
    cases.append({**base,'name':'tagged','source':'tagged.pdf','destination':'tagged-edited.pdf','reject':True})
    for case in cases:
        case.setdefault('sourceSha256', hashlib.sha256((root/case['source']).read_bytes()).hexdigest())
        if case['reject']:
            case['expectedError']={
                'bad-page':'Invalid page index','bad-form':'Target occurrence not found',
                'image-is-not-form':'Selected Do is not a form','bad-text':'Target occurrence not found',
                'stale-text':'Selected text changed','stale-source':'Source changed; refresh selection',
                'missing-glyph':'Replacement has absent or ambiguous glyphs',
                'empty-path':'Form path must have','negative-index':'Invalid occurrence index',
                'actual-text':'ActualText replacement','text-clip':'Text that changes clipping',
                'font-subset-original-greek-cyrillic':'Replacement has absent or ambiguous glyphs',
                'encrypted':'Encrypted nested edits','signed':'Signed nested edits','tagged':'Tagged documents',
            }[case['name']]
    (root/'nested-cases.json').write_text(json.dumps(cases,indent=2))
    print(f'Created {len(cases)} nested-edit cases')


def hierarchy(page):
    def visit(container, inherited=None, ancestors=()):
        resources=container.get('/Resources', inherited).get_object()
        raw=container if container.get('/Subtype')=='/Form' else container.get('/Contents')
        result=[]
        for args, op in ContentStream(raw,None).operations:
            if op == b'Do':
                child=resources['/XObject'][args[0]].get_object()
                assert id(child) not in ancestors
                if child.get('/Subtype')=='/Form':
                    result.append(dict(box=list(child['/BBox']),matrix=list(child.get('/Matrix',[])),
                                       group=str(child.get('/Group')),children=visit(child,resources,ancestors+(id(child),))))
                else:
                    result.append(dict(image=hashlib.sha256(child.get_data()).hexdigest()))
        return result
    return visit(page)


def inspect(root, poppler):
    from PIL import Image, ImageChops
    cases=json.loads((root/'nested-cases.json').read_text())
    verified=[]
    for case in cases:
        source=root/case['source']; result=root/case['destination']
        if case['name']!='stale-source':
            assert hashlib.sha256(source.read_bytes()).hexdigest()==case['sourceSha256']
        if case['reject']:
            assert not result.exists(), case['name']
            continue
        original, saved=PdfReader(source),PdfReader(result)
        assert len(original.pages)==len(saved.pages)
        for a,b in zip(original.pages,saved.pages):
            assert hierarchy(a)==hierarchy(b), case['name']
            assert a.get('/Rotate')==b.get('/Rotate') and a.get('/CropBox')==b.get('/CropBox')
        expected=root/(case['name']+'-expected.pdf')
        if expected.exists():
            resaved=root/(case['name']+'-resaved.pdf')
            for path in [result,expected,source,resaved]:
                subprocess.run([poppler,'-r','96','-png',str(path),str(path.with_suffix(''))],check=True,capture_output=True)
            for page in range(1,len(saved.pages)+1):
                actual=Image.open(str(result.with_suffix(''))+f'-{page}.png').convert('RGB')
                wanted=Image.open(str(expected.with_suffix(''))+f'-{page}.png').convert('RGB')
                difference=ImageChops.difference(actual,wanted)
                assert difference.getbbox() is None, (case['name'],page,difference.getbbox())
                native=Image.open(str(resaved.with_suffix(''))+f'-{page}.png').convert('RGB')
                assert ImageChops.difference(actual,native).getbbox() is None, (case['name'],'native resave',page)
                if page==2:
                    before=Image.open(str(source.with_suffix(''))+f'-{page}.png').convert('RGB')
                    assert ImageChops.difference(actual,before).getbbox() is None
        verified.append(case['name'])
    report={'status':'PASS','positiveCases':verified,'rejectedCases':sum(c['reject'] for c in cases),
            'independentRenderer':'Poppler','renderComparison':'Exact pixels against independently positioned expected PDFs',
            'sourceFilesUnchanged':True,'formHierarchyPreserved':True}
    (root/'independent-nested.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report,indent=2))


if __name__=='__main__':
    root=Path(sys.argv[1]).resolve()
    if sys.argv[2]=='create':
        root.mkdir(parents=True,exist_ok=True); create(root)
    else:
        inspect(root,sys.argv[3])
