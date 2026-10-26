# Signing transaction proof

This is development work, not an available desktop signing feature. It uses the
existing pinned `tools/signing-packages` and a disposable RSA key generated only
in memory. It does not install a certificate, change Windows trust, upload a
document, obtain a trusted timestamp or establish revocation status.

Run `run_proof.py` with the bundled compatible Python runtime. Outputs go to a
fresh `artifacts/signing-proof-<guid>/` directory. Existing PDFs and destinations
are never overwritten. `sign_candidate.py` accepts an already constructed
in-memory pyHanko signer; certificate selection, PFX/password handling, Windows
key-store access and the application transaction boundary are still unimplemented.

The proof requires an unsigned, unencrypted PDF below 200 MiB. It creates a fresh
incrementally signed candidate, validates cryptographic integrity and complete
byte-range coverage, checks the original byte prefix and source hash, and uses
create-only hard-link publication. Unsupported destination filesystems fail
without publishing. Cancellation is checked before/after signing and immediately
before publication; interruption during signing requires future worker isolation.

The harness independently reads the signature with pypdf and verifies the CMS
digest and RSA signed attributes with asn1crypto/cryptography, separately from
pyHanko's PDF validator. It also tests tampering, source/destination identity,
overwrite conflicts, publication-time collision and cancellation. A passing proof
does not establish broad PDF compatibility, certificate trust, timestamp/revocation
validation, co-signing, certified-document support or an installed product.
