import sys
from pathlib import Path
sys.path.insert(0,str(Path('development/tools/python-packages').resolve()))
from pypdf import PdfReader
source=PdfReader('development/fixtures/corpus-v1/edge/scan-real-text.pdf',strict=True)
output=PdfReader('development/artifacts/ocr-validation-f9c8c761-7108-4e55-96c7-4b030b3f4086/candidate.pdf',strict=True)
print('source',source.metadata);print('output',output.metadata);print('source root',source.trailer['/Root'].keys());print('out root',output.trailer['/Root'].keys())
