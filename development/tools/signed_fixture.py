"""Disposable signed test document; no key installation or system trust changes."""
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent/'signing-packages'))
import json, datetime, hashlib
from cryptography import x509 as cx
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID
from asn1crypto import x509, keys
from pyhanko.sign import signers
from pyhanko.sign.validation import validate_pdf_signature
from pyhanko.pdf_utils.reader import PdfFileReader
from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
from pyhanko_certvalidator import ValidationContext
from pyhanko_certvalidator.registry import SimpleCertificateStore

source=Path(sys.argv[1]);root=Path(sys.argv[2]);root.mkdir(parents=True,exist_ok=True)
def validate(path,cert):
    with path.open('rb') as f:
        reader=PdfFileReader(f)
        if len(reader.embedded_signatures)!=1:raise ValueError('Expected one certificate signature')
        context=ValidationContext(trust_roots=[cert],allow_fetching=False,revocation_mode='soft-fail')
        result=validate_pdf_signature(reader.embedded_signatures[0],signer_validation_context=context)
        return {'intact':result.intact,'cryptographicallyValid':result.valid,'trustedInTestContext':result.trusted,'bottomLine':result.bottom_line}

if len(sys.argv)>3 and sys.argv[3]=='inspect':
    cert=x509.Certificate.load((root/'test-certificate.der').read_bytes())
    original=validate(root/'signed.pdf',cert);assert original['intact'] and original['cryptographicallyValid']
    saved=root/'signed-saved.pdf'
    try:
        saved_status=validate(saved,cert)
        assert not saved_status['bottomLine'],'Rewritten document must not present original signature as valid'
    except AssertionError:raise
    except Exception as exc:saved_status={'bottomLine':False,'structurallyUnverifiable':True,'errorType':type(exc).__name__}
    report=json.loads((root/'signature-results.json').read_text())
    report['nativeRewrite']=saved_status
    report['sourceSha256AfterNativeTest']=hashlib.sha256((root/'signed.pdf').read_bytes()).hexdigest()
    assert report['sourceSha256']==report['sourceSha256AfterNativeTest']
    (root/'signature-results.json').write_text(json.dumps(report,indent=2))
    print('PASS: signed source preserved; rewritten copy does not validate as original signature.')
else:
    key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
    now=datetime.datetime.now(datetime.timezone.utc)
    name=cx.Name([cx.NameAttribute(NameOID.COMMON_NAME,'Disposable PDF corpus test signer')])
    cert=cx.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key()).serial_number(cx.random_serial_number()).not_valid_before(now-datetime.timedelta(days=1)).not_valid_after(now+datetime.timedelta(days=30)).add_extension(cx.BasicConstraints(ca=True,path_length=None),critical=True).add_extension(cx.KeyUsage(digital_signature=True,content_commitment=True,key_encipherment=False,data_encipherment=False,key_agreement=False,key_cert_sign=True,crl_sign=True,encipher_only=False,decipher_only=False),critical=True).sign(key,hashes.SHA256())
    der=cert.public_bytes(serialization.Encoding.DER);(root/'test-certificate.der').write_bytes(der)
    cert=x509.Certificate.load(der);registry=SimpleCertificateStore();registry.register(cert)
    signer=signers.SimpleSigner(signing_cert=cert,signing_key=keys.PrivateKeyInfo.load(key.private_bytes(serialization.Encoding.DER,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())),cert_registry=registry)
    with source.open('rb') as f,(root/'signed.pdf').open('xb') as out:
        signers.sign_pdf(IncrementalPdfFileWriter(f),signers.PdfSignatureMetadata(field_name='CorpusCertificateSignature'),signer=signer,output=out)
    passed=validate(root/'signed.pdf',cert);assert passed['intact'] and passed['cryptographicallyValid'] and passed['bottomLine']
    content=bytearray((root/'signed.pdf').read_bytes());assert content[:5]==b'%PDF-';content[7]=ord('7') if content[7]!=ord('7') else ord('6')
    tampered=root/'signed-tampered.pdf';tampered.write_bytes(content)
    failed=validate(tampered,cert);assert not failed['intact'] and not failed['bottomLine']
    (root/'signature-results.json').write_text(json.dumps({'original':passed,'tampered':failed,'sourceSha256':hashlib.sha256((root/'signed.pdf').read_bytes()).hexdigest(),'trust':'Self-signed certificate explicitly trusted only inside this test; not system/public trust','revocation':'Offline and not established','timestamp':'No trusted timestamp','privateKey':'Generated in memory only; not saved'},indent=2))
    print('PASS: real certificate signature validates in test context; covered-byte tampering is rejected.')
