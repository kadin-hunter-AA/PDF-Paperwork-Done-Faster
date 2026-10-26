from pypdf import PdfReader
p=PdfReader('development/fixtures/corpus-v1/edge/scan-real-text.pdf',strict=True).pages[0]
print(p.keys())
for k in ('/Annots','/AA','/StructParents','/Metadata','/PieceInfo','/Group','/Thumb','/AF','/Dur','/Trans','/PresSteps','/LastModified'):
 print(k,repr(p.get(k)),type(p.get(k)))
