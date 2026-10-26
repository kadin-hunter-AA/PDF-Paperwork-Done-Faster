"""Milestone 2 corpus completeness/integrity gate; not P0 app acceptance."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent/'python-packages'))
import json,hashlib,platform,logging
logging.getLogger('pypdf').setLevel(logging.CRITICAL)
from pypdf import PdfReader
root=Path(sys.argv[1]).resolve();mode=sys.argv[2]
required=[
 'view-open','view-tabs','view-thumbnails','view-bookmarks','view-search','view-text-selection-copy','view-zoom','view-rotation','view-page-navigation','view-printing',
 'edit-existing-text','edit-existing-images','edit-add-text','edit-add-images','edit-move-objects','edit-resize-objects','edit-replace-objects','edit-delete-objects','edit-insert-pages','edit-delete-pages','edit-reorder-pages','edit-rotate-pages','edit-extract-pages','edit-merge-pages',
 'markup-highlight','markup-underline','markup-strikeout','markup-freehand-ink','markup-lines','markup-arrows','markup-shapes','markup-text-boxes','markup-sticky-notes','markup-comments','markup-modify-style','markup-independent-viewer',
 'reliability-undo-redo','reliability-safe-save','reliability-dirty-state','reliability-recovery','reliability-clear-errors','reliability-installer-clean-windows','reliability-installer-portable-repair-uninstall']
categories={
 'simple text/images':['engine/original.pdf','engine/objects-original.pdf'],
 'mixed fonts/sizes/colors/languages':['product/mixed-styles-languages.pdf'],
 'columns/tables':['corpus/layout-columns-table.pdf'],
 'subsets/ligatures/missing mappings':['engine/font-subset-original.pdf','edge/ligature-mapped.pdf','edge/missing-unicode-map.pdf'],
 'nonwhite/overlap':['corpus/layout-columns-table.pdf'],
 'shared images':['engine/complex-original.pdf'],
 'nested/transparency/clipping':['engine/complex-original.pdf','compatibility/graphics-boxes.pdf'],
 'scans/mixed pages':['edge/scan-real-text.pdf','corpus/mixed-digital-scan.pdf'],
 'rotation/page boxes':['engine/complex-original.pdf','compatibility/graphics-boxes.pdf'],
 'passwords':['compatibility/password-aes256.pdf'],
 'forms/signatures/navigation/attachments/layers':['compatibility/interactive-features.pdf','edge/signed.pdf'],
 'malformed/large':['corpus/malformed.pdf','corpus/large-500-pages.pdf'],
 'licensed public':['edge/public-hello-world.pdf'],
 'every markup type':['product/markup-gallery.pdf']}
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def expectation(path):
    name=path.name
    if name=='malformed.pdf':return 'Reject open safely; preserve original bytes; present an understandable error.'
    if name=='signed-tampered.pdf':return 'Signature document digest is not intact; never report valid signed document.'
    if name=='signed-saved.pdf':return 'Native rewrite is a separate copy whose original signature no longer validates.'
    if name=='signed.pdf':return 'CMS signature is intact in the isolated explicit test-trust context; source bytes remain unchanged.'
    if name=='font-missing-direct.pdf':return 'Negative case: direct subset edit loses new characters; do not commit it as a successful edit.'
    if name=='complex-direct-edit.pdf':return 'Negative case: nested text stays original despite attempted mutation; unrelated content survives.'
    if name.startswith('complex-'):return 'Rotated page and shared image occurrences remain intact; simple-form replacement output is documented separately.'
    if name.startswith('password-'):return 'Protected copies reject missing/wrong password and accept corpus-user; preserve protection after save. Base is unencrypted setup input.'
    if name.startswith('interactive-'):return 'Canonical field/widget value Corpus value, bookmark, internal link, exact attachment and optional layer survive; unsigned signature field has no value.'
    if name.startswith('graphics-'):return 'Translucent overlap and clipped gold rectangle display; crop/trim/bleed values persist in final fixtures.'
    if name.startswith('markup-'):return 'Final gallery has ten standard editable annotations with comments/appearance streams across two pages; base is unannotated setup input.'
    if name.startswith('mixed-styles-'):return 'Four languages, multiple font families/sizes/colors and numeric columns retain text and layout.'
    if name.startswith('ligature-'):return 'Explicit fi ligature and accented/Greek/Cyrillic mappings retain appearance and readable extraction.'
    if name.startswith('missing-unicode-'):return 'ToUnicode intentionally absent; appearance retained, text editing rejected or conservatively unsupported.'
    if name.startswith('scan-'):return 'Image-only pages have visible synthetic raster content and no hidden searchable text.'
    if name.startswith('public-'):return 'Hello/world and Goodbye/world text retained; intentional missing stream length is tolerated.'
    if name=='large-500-pages.pdf':return '500 pages; randomly access pages 1,250,500; render only requested pages.'
    if name=='mixed-digital-scan.pdf':return 'Two pages: digital layout searchable, second page image-only.'
    if name=='layout-columns-table.pdf':return 'Two eight-line columns, 4x3 labeled table, lilac background and intentional overlap retained.'
    if name.startswith('font-'):return 'ABC source, CABCABCAB mapped edit or ABCΩЖ reembedded replacement retained, with unrelated text.'
    if name.startswith('objects-'):return 'Original red images/highlight/ink or documented modified green image/highlight/ink survive save/reopen; unrelated text unchanged.'
    if name.startswith('page-'):return 'Source page labels are retained; merged output order is page two, page one, merged source page.'
    if name=='edge-preview.pdf':return 'Three-page mapped-font, raster-scan and upstream public preview; excluded from signature validity claims.'
    return 'Original or longer text replacement fixture retains its unrelated second line; verify against paired engine output.'
def check_path(relative):
    p=(root/relative).resolve()
    assert p.is_relative_to(root) and p.is_file(),f'Missing or invalid fixture reference: {relative}'
    return p
cases=json.loads((root/'scenarios.json').read_text(encoding='utf-8'))
assert len({c['id'] for c in cases})==len(cases),'Duplicate scenario ID'
assert set(required).issubset({c['id'] for c in cases}),'Missing P0 acceptance scenario'
for case in cases:
    assert case['steps'] and case['expected'] and case['feature'] and case['fixturePaths']
    assert case['verificationStage']=='future P0 implementation'
    for path in case['fixturePaths']:check_path(path)
for refs in categories.values():
    for path in refs:check_path(path)
public=json.loads((root/'edge/public-provenance.json').read_text())
assert digest(root/'edge/public-hello-world.pdf').upper()==public['sha256']
assert (root/'edge/LICENSE.pdfium').is_file()
signature=json.loads((root/'edge/signature-results.json').read_text())
assert signature['original']['bottomLine'] and not signature['tampered']['bottomLine'] and not signature['nativeRewrite']['bottomLine']
assert digest(root/'edge/signed.pdf')==signature['sourceSha256']==signature['sourceSha256AfterNativeTest']
for path in ['compatibility/independent-compatibility.json','edge/edge-results.json','product/product-results.json']:
    assert json.loads(check_path(path).read_text())['status']=='PASSED'
assert json.loads(check_path('engine/proof.json').read_text())['complex']['knownSimpleFormReplacement']
fixtures=[]
for path in sorted(root.rglob('*.pdf')):
    relative=path.relative_to(root).as_posix()
    if path.name=='malformed.pdf':
        rejected=False
        try:PdfReader(path)
        except Exception:rejected=True
        assert rejected,'Malformed fixture accepted'
        pages=None;role='malformed input: rejection expected'
    else:
        reader=PdfReader(path)
        if reader.is_encrypted:assert reader.decrypt('corpus-user')
        pages=len(reader.pages)
        role='negative capability evidence' if path.name in ['signed-tampered.pdf','signed-saved.pdf','font-missing-direct.pdf','complex-direct-edit.pdf'] else 'valid structural fixture'
    license_note='Project synthetic content'
    if 'font-' in path.name or path.name.startswith(('ligature','missing-unicode','mixed-styles','scan-real-text','signed')):license_note='Contains local test font or synthetic signing data; review font terms before redistribution'
    if path.name.startswith('public-hello-world'):license_note='Upstream PDFium: provenance and full license in edge/'
    fixtures.append({'path':relative,'sha256':digest(path),'bytes':path.stat().st_size,'pages':pages,'role':role,'expected':expectation(path),'license':license_note})
assert len(PdfReader(root/'corpus/large-500-pages.pdf').pages)==500
assert len(PdfReader(root/'corpus/mixed-digital-scan.pdf').pages)==2
manifest_path=root/'manifest.json'
reports=[p for p in sorted(root.rglob('*')) if p.is_file() and p.suffix!='.pdf' and p.name not in ['manifest.json','verification.json']]
hashes={p.relative_to(root).as_posix():digest(p) for p in reports}
if mode=='build':
    manifest={'schema':1,'milestone':2,'gate':'COMPLETE_CORPUS_ONLY','fixtureCount':len(fixtures),'scenarioCount':len(cases),'categories':categories,'fixtures':fixtures,'evidenceSha256':hashes,'engine':'PDFium chromium/8086','python':platform.python_version(),'limitations':['Milestone 1 general nested editing remains unresolved','43 application behaviors are defined future checks, not passing app tests','No application/installer/recovery release is claimed','Signature test trust is isolated and offline'],'credentials':{'compatibility/password-aes256.pdf':{'user':'corpus-user','owner':'corpus-owner'}}}
    manifest_path.write_text(json.dumps(manifest,indent=2),encoding='utf-8')
else:
    manifest=json.loads(manifest_path.read_text())
    assert manifest['fixtures']==fixtures,'Fixture snapshot changed'
    assert manifest['evidenceSha256']==hashes,'Evidence/scenario snapshot changed'
    assert manifest['scenarioCount']==len(cases) and manifest['gate']=='COMPLETE_CORPUS_ONLY'
print(f"PASS: Milestone 2 corpus gate; {len(fixtures)} hashed PDFs, {len(cases)} P0 scenarios, {len(categories)} fixture categories. Application acceptance is not claimed.")
