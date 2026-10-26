"""Run the development-only signing transaction with an in-memory test key."""
import datetime
import hashlib
import json
from pathlib import Path
import sys
import uuid
import subprocess

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sign_candidate import PROJECT, fingerprint, sign_candidate
from asn1crypto import cms, keys, x509
from cryptography import x509 as certificates
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from cryptography.x509.oid import NameOID
from pyhanko.sign import signers
from pyhanko_certvalidator.registry import SimpleCertificateStore

import importlib.util
if importlib.util.find_spec("pypdf") is None:
    sys.path.insert(0, str(PROJECT / "tools" / "python-packages"))
from pypdf import PdfReader


def independent_integrity(path, certificate):
    """Use pypdf/ASN.1 and cryptography directly, without pyHanko validation."""
    reader = PdfReader(path, strict=True)
    signature = reader.get_fields()["PaperworkDoneFasterSignature"]["/V"].get_object()
    start, first_length, second_start, second_length = map(int, signature["/ByteRange"])
    content = path.read_bytes()
    if start != 0 or second_start + second_length != len(content):
        raise ValueError("Independent check: incomplete byte-range coverage")
    signed_bytes = content[:first_length] + content[second_start:second_start + second_length]
    envelope = cms.ContentInfo.load(bytes(signature["/Contents"]))
    signer_info = envelope["content"]["signer_infos"][0]
    attributes = signer_info["signed_attrs"]
    digest = next(a["values"][0].native for a in attributes if a["type"].native == "message_digest")
    if digest != hashlib.sha256(signed_bytes).digest():
        raise ValueError("Independent check: signed byte digest mismatch")
    certificates.load_der_x509_certificate(certificate.dump()).public_key().verify(
        signer_info["signature"].native, attributes.untag().dump(), padding.PKCS1v15(), hashes.SHA256())
    return {"digestMatches": True, "rsaSignatureValid": True, "completeByteRange": True,
            "validator": "pypdf + asn1crypto + cryptography (separate from pyHanko validation)"}


def main():
    output = PROJECT / "artifacts" / ("signing-proof-" + uuid.uuid4().hex)
    output.mkdir()
    source = PROJECT / "fixtures" / "corpus-v1" / "engine" / "original.pdf"
    original_hash = fingerprint(source)
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.timezone.utc)
    identity = certificates.Name([certificates.NameAttribute(NameOID.COMMON_NAME, "Disposable transaction proof")])
    certificate = (certificates.CertificateBuilder().subject_name(identity).issuer_name(identity)
        .public_key(key.public_key()).serial_number(certificates.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=30))
        .add_extension(certificates.BasicConstraints(ca=True, path_length=None), critical=True)
        .add_extension(certificates.KeyUsage(digital_signature=True, content_commitment=True,
            key_encipherment=False, data_encipherment=False, key_agreement=False, key_cert_sign=True,
            crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
        .sign(key, hashes.SHA256()))
    certificate = x509.Certificate.load(certificate.public_bytes(serialization.Encoding.DER))
    registry = SimpleCertificateStore()
    registry.register(certificate)
    signer = signers.SimpleSigner(signing_cert=certificate, cert_registry=registry,
        signing_key=keys.PrivateKeyInfo.load(key.private_bytes(serialization.Encoding.DER,
            serialization.PrivateFormat.PKCS8, serialization.NoEncryption())))
    checks = []
    signed = output / "signed.pdf"
    result = sign_candidate(source, signed, signer)
    independent = independent_integrity(signed, certificate)
    checks.append("Incremental signature independently verifies and covers the full output")
    if PdfReader(source).pages[0].extract_text() != PdfReader(signed).pages[0].extract_text():
        raise ValueError("Signing changed independently extracted page content")
    checks.append("Source bytes and original page text are preserved")
    requests = []
    for path in (source, signed):
        document = str(uuid.uuid4())
        for operation, arguments in (("Open", {"Path": str(path)}),
                ("Render", {"Page": 0, "Width": 612, "Height": 792}), ("Close", {})):
            requests.append({"RequestId": str(uuid.uuid4()), "DocumentId": document,
                             "Revision": 0, "Operation": operation, "Arguments": arguments})
    rendered = subprocess.run([str(PROJECT / "App.Worker/bin/Debug/net10.0/App.Worker.exe")],
        input="\n".join(json.dumps(request) for request in requests) + "\n", capture_output=True,
        text=True, encoding="utf-8", timeout=60, check=True)
    responses = [json.loads(line) for line in rendered.stdout.splitlines()]
    if len(responses) != 6 or not all(response["Success"] for response in responses):
        raise ValueError("PDFium did not accept the signed/source render proof")
    if responses[1]["Data"]["Bgra"] != responses[4]["Data"]["Bgra"]:
        raise ValueError("Signing changed the rendered page appearance")
    checks.append("Independent PDFium renderer returns identical source and signed page pixels")

    def reject(action, exception, name):
        try:
            action()
        except exception:
            checks.append(name)
        else:
            raise AssertionError(name)

    reject(lambda: sign_candidate(source, source, signer), FileExistsError, "Source cannot be the destination")
    signed_hash = fingerprint(signed)
    reject(lambda: sign_candidate(source, signed, signer), FileExistsError, "Existing destination is not overwritten")
    if fingerprint(signed) != signed_hash:
        raise AssertionError("Existing signed output was changed")
    reject(lambda: sign_candidate(signed, output / "cosigned.pdf", signer), ValueError,
           "Already signed documents are refused in this proof subset")
    expired_certificate = (certificates.CertificateBuilder().subject_name(identity).issuer_name(identity)
        .public_key(key.public_key()).serial_number(certificates.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=3)).not_valid_after(now - datetime.timedelta(days=1))
        .sign(key, hashes.SHA256()))
    expired_signer = signers.SimpleSigner(signing_cert=x509.Certificate.load(
        expired_certificate.public_bytes(serialization.Encoding.DER)), cert_registry=registry,
        signing_key=signer.signing_key)
    reject(lambda: sign_candidate(source, output / "expired.pdf", expired_signer), ValueError,
           "Expired signing certificates are refused before candidate creation")
    reject(lambda: sign_candidate(source, output / "cancelled.pdf", signer, cancelled=lambda: True),
           InterruptedError, "Early cancellation publishes no output")
    cancelled_calls = 0
    def cancel_after_signing():
        nonlocal cancelled_calls
        cancelled_calls += 1
        return cancelled_calls >= 2
    reject(lambda: sign_candidate(source, output / "cancelled-late.pdf", signer, cancelled=cancel_after_signing),
           InterruptedError, "Cancellation after signing publishes no output")
    collision = output / "collision.pdf"
    reject(lambda: sign_candidate(source, collision, signer,
           before_publish=lambda: collision.write_bytes(b"user-owned destination")), FileExistsError,
           "Publication-time destination collision preserves the competing file")
    if collision.read_bytes() != b"user-owned destination":
        raise AssertionError("Competing destination changed")
    tampered = output / "tampered.pdf"
    modified = bytearray(signed.read_bytes())
    modified[7] = ord("6") if modified[7] != ord("6") else ord("7")
    tampered.write_bytes(modified)
    reject(lambda: independent_integrity(tampered, certificate), ValueError,
           "Independent validator rejects covered-byte tampering")
    if any(output.glob(".signing-*.pdf")) or (output / "cancelled.pdf").exists() or (output / "cancelled-late.pdf").exists():
        raise AssertionError("A failed transaction left a candidate or published output")
    if fingerprint(source) != original_hash:
        raise AssertionError("The original fixture changed")
    report = {"status": "PASS", "passed": checks, "transaction": result, "independent": independent,
        "scope": "Development proof only; not desktop signing or certificate trust acceptance",
        "privateKey": "Disposable key generated in memory; never saved", "sourceSha256": original_hash}
    (output / "results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(f"PASS {len(checks)} signing transaction checks. Evidence: {output}")


if __name__ == "__main__":
    main()
