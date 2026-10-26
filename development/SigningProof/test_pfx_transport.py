"""Exercise PFX/password transport through the isolated advanced worker.

All private credentials and signed candidates stay in a disposable temp folder;
only aggregate pass/fail text is written to stdout.
"""
from __future__ import annotations

import datetime
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import uuid

from asn1crypto import cms
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa
from cryptography.hazmat.primitives.serialization import pkcs12
from cryptography.x509.oid import NameOID
from pypdf import PdfReader


ROOT = Path(__file__).resolve().parents[1]
SOURCE_FIXTURE = ROOT / "fixtures" / "corpus-v1" / "engine" / "original.pdf"
WORKER = ROOT / "App.Advanced" / "worker.py"
PYTHON = Path(sys.executable).resolve()


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def make_pfx(path: Path, password: str) -> x509.Certificate:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    now = datetime.datetime.now(datetime.timezone.utc)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Disposable signing transport test")])
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=7))
        .add_extension(x509.BasicConstraints(ca=True, path_length=None), critical=True)
        .add_extension(x509.KeyUsage(digital_signature=True, content_commitment=True,
            key_encipherment=False, data_encipherment=False, key_agreement=False,
            key_cert_sign=True, crl_sign=True, encipher_only=False, decipher_only=False), critical=True)
        .sign(key, hashes.SHA256()))
    blob = pkcs12.serialize_key_and_certificates(
        b"temporary-test-only", key, cert, None,
        serialization.BestAvailableEncryption(password.encode("utf-8")))
    path.write_bytes(blob)
    return cert


def invoke(source: Path, destination: Path, pfx: Path, password: str) -> tuple[int, dict[str, object], str, str]:
    request = {"Operation": "SignPdf", "Input": str(source), "Output": str(destination),
               "CertificatePath": str(pfx), "CertificatePassword": password}
    process = subprocess.run(
        [str(PYTHON), "-I", str(WORKER)], input=json.dumps(request) + "\n",
        capture_output=True, text=True, encoding="utf-8", timeout=90,
        cwd=str(WORKER.parent), check=False,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    # Do not place worker output or request material in assertion diagnostics.
    try:
        response = json.loads(process.stdout)
    except Exception:
        raise AssertionError("Worker returned an invalid response") from None
    if not isinstance(response, dict):
        raise AssertionError("Worker returned an invalid response")
    return process.returncode, response, process.stdout, process.stderr


def independently_verify(path: Path, certificate: x509.Certificate) -> None:
    """Verify CMS digest and RSA signature without using the signing engine."""
    reader = PdfReader(path, strict=True)
    fields = reader.get_fields() or {}
    signature_field = fields.get("PaperworkDoneFasterSignature")
    if signature_field is None:
        raise AssertionError("Signed output has no expected signature field")
    signature = signature_field["/V"].get_object()
    start, first_length, second_start, second_length = map(int, signature["/ByteRange"])
    content = path.read_bytes()
    if start != 0 or second_start + second_length != len(content):
        raise AssertionError("Signature does not cover the complete output")
    signed_bytes = content[:first_length] + content[second_start:second_start + second_length]
    envelope = cms.ContentInfo.load(bytes(signature["/Contents"]))
    signer_info = envelope["content"]["signer_infos"][0]
    attributes = signer_info["signed_attrs"]
    message_digest = next(a["values"][0].native for a in attributes
                          if a["type"].native == "message_digest")
    if message_digest != hashlib.sha256(signed_bytes).digest():
        raise AssertionError("CMS digest does not match the signed PDF byte ranges")
    certificate.public_key().verify(signer_info["signature"].native,
        attributes.untag().dump(), padding.PKCS1v15(), hashes.SHA256())


def main() -> None:
    if not SOURCE_FIXTURE.is_file() or not WORKER.is_file():
        raise AssertionError("Signing proof inputs are missing")
    fixture_hash = sha256(SOURCE_FIXTURE)
    with tempfile.TemporaryDirectory(prefix="pdf-signing-pfx-test-") as temporary:
        private_dir = Path(temporary)
        source = private_dir / "input.pdf"
        source.write_bytes(SOURCE_FIXTURE.read_bytes())
        source_hash = sha256(source)
        password = secrets.token_urlsafe(32)
        pfx = private_dir / "disposable.pfx"
        certificate = make_pfx(pfx, password)

        signed = private_dir / "signed.pdf"
        code, response, stdout, stderr = invoke(source, signed, pfx, password)
        if code != 0 or response.get("Success") is not True:
            raise AssertionError("Worker refused valid disposable PFX signing")
        data = response.get("Data")
        if not isinstance(data, dict) or data.get("Intact") is not True or data.get("CryptographicallyValid") is not True:
            raise AssertionError("Worker did not report successful integrity validation")
        if data.get("CertificateTrustEstablished") is not False or data.get("RevocationEstablished") is not False or data.get("TrustedTimestamp") is not False:
            raise AssertionError("Worker overstated certificate trust or timestamp status")
        independently_verify(signed, certificate)
        if source.read_bytes() != SOURCE_FIXTURE.read_bytes():
            raise AssertionError("Worker changed the disposable source copy")

        wrong_destination = private_dir / "wrong-password.pdf"
        wrong_password = secrets.token_urlsafe(32)
        wrong_code, wrong_response, wrong_out, wrong_err = invoke(source, wrong_destination, pfx, wrong_password)
        if wrong_code != 0 or wrong_response.get("Success") is not False or wrong_destination.exists():
            raise AssertionError("Wrong password did not fail safely")
        if password in wrong_out or password in wrong_err or wrong_password in wrong_out or wrong_password in wrong_err:
            raise AssertionError("Worker exposed credential text")

        collision = private_dir / "existing.pdf"
        owned_bytes = b"preserve existing destination"
        collision.write_bytes(owned_bytes)
        collision_code, collision_response, _, _ = invoke(source, collision, pfx, password)
        if collision_response.get("Success") is not False:
            raise AssertionError("Existing destination was not refused")
        if collision.read_bytes() != owned_bytes:
            raise AssertionError("Existing destination was modified")

        if source.read_bytes() != SOURCE_FIXTURE.read_bytes() or sha256(source) != source_hash:
            raise AssertionError("Source copy changed during refusal checks")
        if sha256(SOURCE_FIXTURE) != fixture_hash:
            raise AssertionError("Frozen source fixture changed")
        if password in stdout or password in stderr:
            raise AssertionError("Worker exposed the signing password")

    print("PASS PFX worker transport, independent CMS/RSA verification, wrong-password refusal, collision preservation, and source integrity")


if __name__ == "__main__":
    main()
