"""Page import preparation and bookmark-keeping combine for the companion engine.

PrepareImport writes a plain copy of selected pages with document-level and risky page-level
structures removed, so the strict native page insert can accept it. CombineKeepBookmarks merges
whole files (or page ranges) with pypdf and keeps each file's outline nested under a bookmark
named after the file.
"""
from __future__ import annotations

import os
import re

import pymupdf
from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, ContentStream, DecodedStreamObject, DictionaryObject, NameObject

MAX_PAGES = 50_000
MAX_SELECTION = 5_000
MAX_COMBINE_INPUTS = 200
MAX_TREE_NODES = 100_000
MARKED_OPERATORS = (b"BDC", b"BMC", b"EMC", b"DP", b"MP")


class Refused(ValueError):
    pass


def _bool(request, name, default):
    value = request.get(name, default)
    if not isinstance(value, bool):
        raise Refused("The page import request is malformed.")
    return value


def _check_paths(request, extra=()):
    output = request.get("Output")
    if not isinstance(output, str) or not output:
        raise Refused("The page import request is malformed.")
    if os.path.exists(output):
        raise Refused("The output already exists. Existing files are never replaced.")
    if not os.path.isdir(os.path.dirname(output)):
        raise Refused("The output folder does not exist or is unavailable.")
    for path in extra:
        if os.path.normcase(os.path.abspath(path)) == os.path.normcase(os.path.abspath(output)):
            raise Refused("The output must not be one of the input files.")
    return output


def _pages(request, count):
    pages = request.get("Pages")
    if pages is None:
        if count > MAX_PAGES:
            raise Refused("This PDF exceeds the 50,000-page limit.")
        return list(range(count))
    if (not isinstance(pages, list) or not 1 <= len(pages) <= MAX_SELECTION or
            any(not isinstance(p, int) or isinstance(p, bool) or not 0 <= p < count for p in pages) or
            len(set(pages)) != len(pages)):
        raise Refused("Select distinct pages that exist in the source PDF.")
    return pages


def _walk_tree(node, key, limit=MAX_TREE_NODES):
    """Counts leaf entries of a PDF name tree (/Names pairs) with bounded work."""
    seen, stack, total = set(), [node], 0
    while stack:
        item = stack.pop()
        item = item.get_object() if hasattr(item, "get_object") else item
        if not isinstance(item, DictionaryObject) or id(item) in seen:
            continue
        seen.add(id(item))
        if len(seen) > limit:
            break
        names = item.get(key)
        if names is not None:
            names = names.get_object()
            total += len(names) // 2 if isinstance(names, ArrayObject) else 0
        kids = item.get("/Kids")
        if kids is not None:
            kids = kids.get_object()
            if isinstance(kids, ArrayObject):
                stack.extend(kids)
    return total


def _catalog_counts(path, password):
    reader = PdfReader(path)
    if reader.is_encrypted and not reader.decrypt(password or "") :
        raise Refused("The source PDF's password is incorrect.")
    root = reader.trailer["/Root"]
    counts = {"NamedDestinations": 0, "JavaScript": 0, "StructureTree": 0}
    dests = root.get("/Dests")
    if dests is not None and isinstance(dests.get_object(), DictionaryObject):
        counts["NamedDestinations"] += len(dests.get_object())
    names = root.get("/Names")
    names = names.get_object() if names is not None else None
    if isinstance(names, DictionaryObject):
        if "/Dests" in names:
            counts["NamedDestinations"] += _walk_tree(names["/Dests"], "/Names")
        if "/JavaScript" in names:
            counts["JavaScript"] += max(1, _walk_tree(names["/JavaScript"], "/Names"))
    if "/OpenAction" in root:
        counts["JavaScript"] += 1
    if "/AA" in root:
        counts["JavaScript"] += 1
    if "/StructTreeRoot" in root or ("/MarkInfo" in root and root["/MarkInfo"].get_object().get("/Marked")):
        counts["StructureTree"] = 1
    return counts


def _open_source(request):
    password = request.get("Password")
    if password is not None and (not isinstance(password, str) or len(password) > 256):
        raise Refused("The page import request is malformed.")
    try:
        doc = pymupdf.open(request["Input"])
    except Exception:
        raise Refused("The source PDF could not be opened.")
    if doc.needs_pass:
        if not password:
            doc.close()
            raise Refused("The source PDF is password protected. Enter its password to import pages.")
        if not doc.authenticate(password):
            doc.close()
            raise Refused("The source PDF's password is incorrect.")
    if doc.page_count < 1:
        doc.close()
        raise Refused("The source PDF has no pages.")
    return doc, password


def _has_signature(doc):
    if not doc.is_form_pdf:
        return False
    for page in doc:
        for widget in page.widgets() or ():
            if widget.field_type == pymupdf.PDF_WIDGET_TYPE_SIGNATURE:
                return True
    return False


def _page_annotations(page):
    """(annotations, links, widgets, signature_widgets) remaining on a page."""
    annots = sum(1 for _ in (page.annots() or ()))
    links = len(page.get_links())
    widgets = list(page.widgets() or ())
    return annots, links, len(widgets), sum(1 for w in widgets if w.field_type == pymupdf.PDF_WIDGET_TYPE_SIGNATURE)


def _strip_marked(stream_obj, writer, counter):
    """Rewrites a content stream without marked-content operators; returns the new bytes or None."""
    content = ContentStream(stream_obj, writer)
    kept = []
    for operands, operator in content.operations:
        if operator in MARKED_OPERATORS:
            if operator in (b"BDC", b"BMC"):
                counter[0] += 1
            continue
        kept.append((operands, operator))
    if len(kept) == len(content.operations):
        return None
    content.operations = kept
    return content


FORM_KEYS = ("/StructParent", "/StructParents", "/Metadata", "/PieceInfo")


def _clean_resources(resources, writer, visited, counter):
    resources = resources.get_object() if resources is not None else None
    if not isinstance(resources, DictionaryObject):
        return
    xobjects = resources.get("/XObject")
    xobjects = xobjects.get_object() if xobjects is not None else None
    if not isinstance(xobjects, DictionaryObject):
        return
    for name in list(xobjects.keys()):
        reference = xobjects.raw_get(name)
        target = reference.get_object()
        key = getattr(reference, "idnum", id(target))
        if key in visited or not isinstance(target, DictionaryObject):
            continue
        visited.add(key)
        if len(visited) > MAX_TREE_NODES:
            raise Refused("The page has too many nested objects to import safely.")
        for entry in FORM_KEYS:
            if entry in target:
                del target[entry]
        if target.get("/Subtype") != "/Form":
            continue
        _clean_resources(target.get("/Resources"), writer, visited, counter)
        rewritten = _strip_marked(target, writer, counter)
        if rewritten is not None:
            replacement = DecodedStreamObject()
            replacement.set_data(rewritten.get_data())
            for entry, value in target.items():
                if entry not in ("/Filter", "/DecodeParms", "/Length", "/DL"):
                    replacement[NameObject(entry)] = value
            if hasattr(reference, "idnum"):
                writer._replace_object(reference.idnum, replacement)
            else:
                xobjects[name] = replacement


def _sanitize(plain, output):
    writer = PdfWriter(clone_from=plain)
    counter, visited = [0], set()
    for page in writer.pages:
        for entry in ("/StructParents", "/AA", "/Metadata", "/PieceInfo", "/B", "/Annots"):
            if entry in page:
                del page[entry]
        _clean_resources(page.get("/Resources"), writer, visited, counter)
        rewritten = _strip_marked(page.get_contents(), writer, counter) if page.get("/Contents") is not None else None
        if rewritten is not None:
            page.replace_contents(rewritten)
    for entry in ("/Outlines", "/Names", "/Dests", "/PageLabels", "/StructTreeRoot", "/MarkInfo", "/OpenAction", "/AA",
                  "/AcroForm", "/OCProperties", "/Threads", "/Metadata", "/PieceInfo", "/Perms"):
        if entry in writer._root_object:
            del writer._root_object[NameObject(entry)]
    with open(output, "xb") as stream:
        writer.write(stream)
    return counter[0]


def _prepare_import(request):
    output = _check_paths(request, (request["Input"],))
    keep_annotations = _bool(request, "KeepAnnotationsAsContent", True)
    keep_forms = _bool(request, "KeepFormValues", True)
    allow_signed = _bool(request, "AllowSigned", False)
    doc, password = _open_source(request)
    temp = output + "." + os.urandom(6).hex() + ".stage.pdf"
    try:
        count = doc.page_count
        pages = _pages(request, count)
        if any(not ocg.get("on", True) for ocg in (doc.get_ocgs() or {}).values()):
            raise Refused("This PDF has hidden layers that cannot be imported without changing what is visible.")
        signed = _has_signature(doc)
        if signed and not allow_signed:
            raise Refused("The source PDF contains signature fields. Import is refused unless you allow an unsigned copy of its pages.")
        counts = _catalog_counts(request["Input"], password)
        dropped = {
            "Bookmarks": len(doc.get_toc(simple=True) or []),
            "PageLabels": len(doc.get_page_labels() or []),
            "Attachments": doc.embfile_count(),
            "NamedDestinations": counts["NamedDestinations"],
            "JavaScriptAndActions": counts["JavaScript"],
            "StructureTree": counts["StructureTree"],
        }
        doc.select(pages)
        before = [_page_annotations(page) for page in doc]
        annots_before = sum(item[0] for item in before)
        links_before = sum(item[1] for item in before)
        widgets_before = sum(item[2] for item in before)
        signature_widgets = sum(item[3] for item in before)
        if keep_annotations or keep_forms:
            doc.bake(annots=keep_annotations, widgets=keep_forms)
        after = [_page_annotations(page) for page in doc]
        annots_left = sum(item[0] for item in after)
        widgets_left = sum(item[2] for item in after)
        page_actions = 0
        for page in doc:
            if doc.xref_get_key(page.xref, "AA")[0] != "null":
                page_actions += 1
        dropped["JavaScriptAndActions"] += page_actions
        dropped["AnnotationsFlattened"] = annots_before - annots_left
        dropped["Annotations"] = annots_left
        dropped["Links"] = links_before
        dropped["FormFieldsFlattened"] = widgets_before - widgets_left - signature_widgets if keep_forms else 0
        dropped["FormFields"] = widgets_left - signature_widgets if keep_forms else widgets_before - signature_widgets
        dropped["SignatureFields"] = signature_widgets
        plain = pymupdf.open()
        plain.insert_pdf(doc, links=0, annots=0, widgets=0)
        plain.save(temp, garbage=3, deflate=True, encryption=pymupdf.PDF_ENCRYPT_NONE)
        plain.close()
        marked = _sanitize(temp, output)
        dropped["MarkedContentSections"] = marked
    except BaseException:
        _discard(output)
        raise
    finally:
        doc.close()
        _discard(temp)
    # Independent check that nothing risky survived.
    check = pymupdf.open(output)
    try:
        if check.page_count != len(pages) or check.get_toc() or check.embfile_count():
            raise Refused("The sanitized copy failed verification.")
        for page in check:
            if sum(_page_annotations(page)[:3]):
                raise Refused("The sanitized copy failed verification.")
    except Refused:
        check.close()
        _discard(output)
        raise
    check.close()
    return {"Applied": True, "PageCount": len(pages), "Dropped": {k: int(v) for k, v in dropped.items()}}


def _discard(path):
    try:
        if path and os.path.exists(path):
            os.remove(path)
    except OSError:
        pass


def _parse_range(text, count):
    if text is None or (isinstance(text, str) and not text.strip()):
        return list(range(count))
    if not isinstance(text, str) or len(text) > 4096:
        raise Refused("A page range is malformed.")
    pages, seen = [], set()
    for part in text.split(","):
        match = re.fullmatch(r"\s*(\d{1,6})\s*(?:-\s*(\d{1,6}))?\s*", part)
        if not match:
            raise Refused("Use page numbers such as 1,3-5; page numbers start at 1.")
        first = int(match.group(1))
        last = int(match.group(2)) if match.group(2) else first
        if first < 1 or last < first:
            raise Refused("Use ascending page ranges such as 2-5.")
        if last > count:
            raise Refused("A page range is outside its PDF.")
        for page in range(first - 1, last):
            if page in seen:
                raise Refused("A page appears more than once in a range.")
            seen.add(page)
            pages.append(page)
            if len(pages) > MAX_PAGES:
                raise Refused("Combine supports up to 50,000 selected pages per output.")
    return pages


def _title(path):
    stem = os.path.splitext(os.path.basename(path))[0]
    return re.sub(r"[\x00-\x1f\x7f]", " ", stem).strip()[:200] or "Untitled"


def _combine_keep_bookmarks(request):
    inputs = request.get("Inputs")
    if not isinstance(inputs, list) or not 2 <= len(inputs) <= MAX_COMBINE_INPUTS:
        raise Refused("Select at least two PDF files to combine.")
    group = _bool(request, "GroupByFile", True)
    paths = []
    for item in inputs:
        if not isinstance(item, dict) or not isinstance(item.get("Path"), str) or not item["Path"]:
            raise Refused("Each input must include a PDF path and page range.")
        paths.append(item["Path"])
    output = _check_paths(request, paths)
    writer, readers, total = PdfWriter(), [], 0
    try:
        for item in inputs:
            try:
                reader = PdfReader(item["Path"])
            except Exception:
                raise Refused("An input PDF could not be opened.")
            readers.append(reader)
            if reader.is_encrypted:
                raise Refused("Password-protected PDFs are not supported in Combine yet. Remove encryption or open an unprotected copy.")
            try:
                fields = reader.get_fields() or {}
            except Exception:
                fields = {}
            if any(field.get("/FT") == "/Sig" for field in fields.values()):
                raise Refused("Signed PDFs are not supported in Combine. Keep the signed original and choose unsigned copies.")
            pages = _parse_range(item.get("Range"), len(reader.pages))
            total += len(pages)
            if not pages or total > MAX_PAGES:
                raise Refused("The selected ranges contain no pages." if not pages else "Combine supports up to 50,000 selected pages per output.")
            writer.append(reader, outline_item=_title(item["Path"]) if group else None, pages=pages, import_outline=True)
        if len(writer.pages) != total:
            raise Refused("The combined result had an unexpected page count. No output was published.")
        with open(output, "xb") as stream:
            writer.write(stream)
    except BaseException:
        if len(writer.pages) == total:
            _discard(output)
        raise
    return {"Applied": True, "PageCount": total}


def handle(operation: str, request: dict) -> dict:
    if operation == "PrepareImport":
        return _prepare_import(request)
    if operation == "CombineKeepBookmarks":
        return _combine_keep_bookmarks(request)
    raise Refused("Unsupported page tool operation.")
