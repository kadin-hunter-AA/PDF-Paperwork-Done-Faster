import sys
from pathlib import Path
root=Path.cwd()
sys.path.insert(0,str(root/'development/tools/python-packages'))
sys.path.insert(0,str(root/'development/tools/redaction-pymupdf'))
from PIL import Image, ImageDraw, ImageFont
import pymupdf
print('pymupdf',pymupdf.__version__)
img=Image.new('RGB',(1275,1650),'white')
d=ImageDraw.Draw(img)
font=ImageFont.truetype(r'C:\Windows\Fonts\arial.ttf',44)
d.text((90,120),'ENGLISH OCR PROOF',font=font,fill='black')
d.text((90,220),'Invoice number: 48271',font=font,fill='black')
p=root/'development/OcrProof/pymupdf-ocr-input.png'
p.parent.mkdir(parents=True,exist_ok=True)
img.save(p)
pix=pymupdf.Pixmap(str(p))
try:
 data=pix.pdfocr_tobytes(language='eng',tessdata=str(root/'development/tools/ocr-runtime'))
 out=root/'development/OcrProof/pymupdf-ocr-result.pdf'
 out.write_bytes(data)
 doc=pymupdf.open(out)
 print('OCR PDF bytes',len(data),'text',repr(doc[0].get_text()))
except Exception as e:
 print(type(e).__name__,str(e))
 raise
