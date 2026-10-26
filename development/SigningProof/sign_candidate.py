"""Development shim: all transaction logic lives in the companion engine."""
import importlib.util
import os
from pathlib import Path
import sys

PROJECT = Path(__file__).resolve().parents[1]
if override := os.environ.get("HUNTER_PDF_SIGNING_PACKAGES"):
    sys.path.insert(0, override)
elif importlib.util.find_spec("pyhanko") is None:
    sys.path.insert(0, str(PROJECT / "tools" / "signing-packages"))
sys.path.insert(0, str(PROJECT / "App.Advanced"))
from signing import fingerprint, sign_candidate, validate_candidate
