"""Single-request isolated companion worker for advanced PDF operations."""
from __future__ import annotations

import json
import logging
from pathlib import Path
import re
import sys

# In isolated mode Python omits untrusted script-path/environment additions.
# Only this worker's own packaged directory is admitted as trusted code.
sys.path.insert(0, str(Path(__file__).resolve().parent))

from forms import FillRefused, fill_candidate

# Library diagnostic messages must not place document or credential material on
# the transport. The parent accepts only the bounded response contract below.
logging.disable(logging.CRITICAL)


def _safe_refusal(message: str) -> str:
    text = re.sub(r"[\x00-\x1f\x7f]", " ", message)
    text = " ".join(text.split())
    return text[:512] or "The requested PDF operation was refused."


# Feature modules loaded only for their own operations, so a job imports just the
# libraries it needs. Each module exposes Refused (a ValueError subclass whose
# message is safe to show) and handle(operation, request) -> JSON-safe dict.
EXTENSION_OPERATIONS = {
    "quick_tools": ("Compress", "Protect", "Unprotect", "Watermark", "HeaderFooter", "Bates", "Crop",
                    "Flatten", "GetProperties", "SetProperties", "GetBookmarks", "SetBookmarks",
                    "ExtractImages", "ListLinks", "AddLink", "RemoveLink"),
    "form_panel": ("ListFields", "FillFields"),
    "rich_text": ("AddFreeText", "ListFreeText", "UpdateFreeText", "DeleteFreeText", "AddUnicodeText"),
    "page_tools": ("PrepareImport", "CombineKeepBookmarks"),
    "ocr_tools": ("OcrInspect", "OcrPages", "OcrLanguages"),
}
EXTENSION_MODULE = {operation: module for module, operations in EXTENSION_OPERATIONS.items() for operation in operations}


def _run_extension(request: dict) -> int:
    import importlib
    module = importlib.import_module(EXTENSION_MODULE[request["Operation"]])
    try:
        result = module.handle(request["Operation"], request)
    except module.Refused as error:
        print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
        return 0
    print(json.dumps({"Success": True, "Data": result}, ensure_ascii=True))
    return 0


def main() -> int:
    line = sys.stdin.readline(262_145)
    if not line or len(line) > 262_144:
        print(json.dumps({"Success": False, "Error": "The operation request is invalid."}, ensure_ascii=True))
        return 2
    try:
        request = json.loads(line)
        if isinstance(request, dict) and request.get("Operation") in EXTENSION_MODULE:
            if not isinstance(request.get("Input"), str):
                raise FillRefused("The PDF operation request is malformed.")
            return _run_extension(request)
        if not isinstance(request, dict) or request.get("Operation") not in ("FillForm", "SignPdf", "OcrScan", "Redact", "InspectText", "FindText", "EditText"):
            raise FillRefused("Unsupported advanced PDF operation.")
        source = request.get("Input")
        destination = request.get("Output")
        if not all(isinstance(item, str) for item in (source, destination)):
            raise FillRefused("The PDF operation request is malformed.")
        if request["Operation"] in ("InspectText", "FindText", "EditText"):
            from structured_text import UnsupportedEdit, inspect_text, find_text, edit_candidate
            try:
                if request["Operation"] == "InspectText":
                    result = inspect_text(source, request.get("Page"), request.get("Cursor", 0), request.get("IncludeInvisible", False))
                elif request["Operation"] == "FindText":
                    result = find_text(source, request.get("Page"), request.get("Text"), request.get("Occurrence"), request.get("IncludeInvisible", False))
                else:
                    selection = request.get("Selection")
                    if not isinstance(selection, dict):
                        raise UnsupportedEdit("The text selection is malformed.")
                    result = edit_candidate({"source": source, "destination": destination,
                        "sourceSha256": selection.get("SourceSHA256"), "pageIndex": request.get("Page"),
                        "formPath": selection.get("FormPath"), "textIndex": selection.get("TextIndex"),
                        "expectedText": selection.get("Text"), "replacement": request.get("Replacement"),
                        "allowWider": request.get("AllowWider", False), "includeInvisible": request.get("IncludeInvisible", False)})
            except UnsupportedEdit as error:
                print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
                return 0
            print(json.dumps({"Success": True, "Data": result}, ensure_ascii=True))
            return 0
        if request["Operation"] == "Redact":
            from redaction import RedactionRefused, redact_candidate
            try:
                result = redact_candidate(source, destination, request.get("Regions"))
            except RedactionRefused as error:
                print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
                return 0
            print(json.dumps({"Success": True, "Data": result}, ensure_ascii=True))
            return 0
        if request["Operation"] == "OcrScan":
            from ocr import OcrRefused, ocr_scan
            traineddata = request.get("TrainedDataPath")
            library = request.get("PdfiumPath")
            if not isinstance(traineddata, str) or not isinstance(library, str):
                raise FillRefused("The local OCR engine request is malformed.")
            try:
                result = ocr_scan(source, destination, traineddata_path=traineddata, pdfium_library=library)
            except OcrRefused as error:
                print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
                return 0
            print(json.dumps({"Success": True, "Data": {
                "Applied": True, "OutputSHA256": result["candidateSha256"]}}, ensure_ascii=True))
            return 0
        if request["Operation"] == "SignPdf":
            from signing import SignatureRefused, load_pfx_signer, sign_candidate
            certificate = request.get("CertificatePath")
            password = request.get("CertificatePassword")
            if not isinstance(certificate, str) or not isinstance(password, str):
                raise FillRefused("The certificate request is malformed.")
            try:
                signer = load_pfx_signer(certificate, password)
                result = sign_candidate(Path(source), Path(destination), signer)
            except SignatureRefused as error:
                print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
                return 0
            print(json.dumps({"Success": True, "Data": {
                "Applied": True, "Intact": result["intact"],
                "CryptographicallyValid": result["cryptographicallyValid"],
                "OutputSHA256": result["outputSha256"], "CertificateTrustEstablished": False,
                "RevocationEstablished": False, "TrustedTimestamp": False,
                "TemporaryCandidateRetained": result["temporaryCandidateRetained"]}}, ensure_ascii=True))
            return 0
        field_name = request.get("FieldName")
        value = request.get("Value")
        if not all(isinstance(item, str) for item in (field_name, value)):
            raise FillRefused("The form-fill request is malformed.")
        result = fill_candidate(source, destination, field_name, value)
        print(json.dumps({"Success": True, "Data": {"Applied": True, "Page": result["Page"]}}, ensure_ascii=True))
        return 0
    except FillRefused as error:
        print(json.dumps({"Success": False, "Error": _safe_refusal(str(error))}, ensure_ascii=True))
        return 0
    except Exception:
        # Never return exception details, document data, paths, or credentials.
        print(json.dumps({"Success": False, "Error": "The PDF operation could not be completed."}, ensure_ascii=True))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
