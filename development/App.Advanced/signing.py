"""Guarded signing transaction; no desktop command is enabled yet.

The caller supplies an in-memory pyHanko signer. This module never loads or stores
private keys, changes OS trust, or contacts timestamp/revocation services.
"""
from __future__ import annotations

import hashlib
import datetime
import os
from pathlib import Path
import uuid

from pyhanko.pdf_utils.incremental_writer import IncrementalPdfFileWriter
from pyhanko.pdf_utils.reader import PdfFileReader
from pyhanko.sign import signers
from pyhanko.sign.validation import validate_pdf_signature
from pyhanko_certvalidator import ValidationContext


class SignatureRefused(ValueError):
    pass


def load_pfx_signer(path: str, password: str):
    certificate_file = Path(path).resolve(strict=True)
    if certificate_file.stat().st_size > 4 * 1024 * 1024 or len(password) > 4096:
        raise SignatureRefused("The signing certificate or password exceeds the supported size.")
    try:
        signer = signers.SimpleSigner.load_pkcs12(str(certificate_file), passphrase=password.encode("utf-8"))
    except Exception:
        raise SignatureRefused("The certificate could not be opened. Check the certificate and password.") from None
    if signer is None:
        raise SignatureRefused("The certificate could not be opened. Check the certificate and password.")
    return signer


def fingerprint(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_candidate(path: Path, certificate) -> dict:
    """Cryptographic integrity only; supplied cert trust is isolated to this check."""
    with path.open("rb") as stream:
        reader = PdfFileReader(stream, strict=True)
        if reader.encrypted or len(reader.embedded_signatures) != 1:
            raise SignatureRefused("Expected one signature in an unencrypted candidate")
        signature = reader.embedded_signatures[0]
        status = validate_pdf_signature(
            signature,
            signer_validation_context=ValidationContext(
                trust_roots=[certificate], allow_fetching=False,
                revocation_mode="soft-fail",
            ),
        )
        if not status.intact or not status.valid:
            raise SignatureRefused("Candidate cryptographic integrity failed")
        # A newly signed candidate must cover the entire newly created file.
        byte_range = list(signature.sig_object["/ByteRange"])
        if len(byte_range) != 4 or byte_range[0] != 0 or byte_range[2] + byte_range[3] != path.stat().st_size:
            raise SignatureRefused("Candidate signature does not cover the complete file")
        return {"intact": True, "cryptographicallyValid": True,
                "byteRange": byte_range, "certificateTrustEstablished": False,
                "revocationEstablished": False, "trustedTimestamp": False}


def sign_candidate(source: Path, destination: Path, signer, *, cancelled=lambda: False,
                   before_publish=lambda: None) -> dict:
    """Sign a source copy, validate, then publish without replacing an existing file.

    Proof subset: unsigned, unencrypted PDFs, RSA test signers, no co-signing,
    certification, timestamps, trust-store changes or network fetching. A hard-link
    publication requires a supporting destination filesystem and fails closed.
    """
    source = source.resolve(strict=True)
    destination = destination.absolute()
    if source == destination.resolve() or destination.exists():
        raise FileExistsError("Choose a new output file; the source is never replaced")
    if not destination.parent.is_dir():
        raise FileNotFoundError("The destination folder does not exist")
    if source.stat().st_size > 200 * 1024 * 1024:
        raise SignatureRefused("Signing input exceeds 200 MiB")
    certificate = signer.signing_cert
    validity = certificate["tbs_certificate"]["validity"]
    now = datetime.datetime.now(datetime.timezone.utc)
    if not validity["not_before"].native <= now <= validity["not_after"].native:
        raise SignatureRefused("The signing certificate is outside its validity period")
    if certificate.public_key.algorithm != "rsa" or certificate.public_key.bit_size < 2048:
        raise SignatureRefused("This version supports RSA signing certificates of at least 2048 bits")
    usage = certificate.key_usage_value
    if usage is not None and not {"digital_signature", "non_repudiation"}.intersection(usage.native):
        raise SignatureRefused("The certificate does not permit document signing")
    original_hash = fingerprint(source)
    candidate = destination.parent / (".signing-" + uuid.uuid4().hex + ".pdf")
    published = False
    report = None
    try:
        if cancelled():
            raise InterruptedError("Signing cancelled before candidate creation")
        with source.open("rb") as input_stream, candidate.open("xb") as output_stream:
            reader = PdfFileReader(input_stream, strict=True)
            if reader.encrypted or reader.embedded_signatures:
                raise SignatureRefused("Encrypted or already signed PDFs are unsupported for signing")
            input_stream.seek(0)
            signers.sign_pdf(
                IncrementalPdfFileWriter(input_stream),
                signers.PdfSignatureMetadata(field_name="PaperworkDoneFasterSignature"),
                signer=signer, output=output_stream,
            )
            output_stream.flush()
            os.fsync(output_stream.fileno())
        if cancelled():
            raise InterruptedError("Signing cancelled; no output published")
        result = validate_candidate(candidate, signer.signing_cert)
        # Incremental signing must retain every byte of the stable source.
        with source.open("rb") as original, candidate.open("rb") as signed:
            while chunk := original.read(1024 * 1024):
                if signed.read(len(chunk)) != chunk:
                    raise SignatureRefused("Signing changed the source byte prefix")
        if fingerprint(source) != original_hash:
            raise SignatureRefused("The source changed while signing")
        before_publish()
        if cancelled():
            raise InterruptedError("Signing cancelled before publication")
        if fingerprint(source) != original_hash:
            raise SignatureRefused("The source changed before publication")
        report = {**result, "sourceSha256": original_hash,
                  "outputSha256": fingerprint(candidate), "sourcePrefixPreserved": True,
                  "temporaryCandidateRetained": False}
        # Atomic create-only directory entry: collisions cannot overwrite data.
        os.link(candidate, destination)
        published = True
        return report
    finally:
        try:
            candidate.unlink(missing_ok=True)
        except OSError:
            if published:
                # Do not report a successfully published signature as a failed save.
                report["temporaryCandidateRetained"] = True
            else:
                raise RuntimeError("Signing failed; temporary candidate cleanup could not complete")
