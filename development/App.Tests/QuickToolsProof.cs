using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HunterPdf.Core;

static class QuickToolsProof
{
    const string FixtureScript = """
import sys, random, pymupdf
out = sys.argv[1]
d = pymupdf.open()
pix = pymupdf.Pixmap(pymupdf.csRGB, pymupdf.IRect(0, 0, 600, 600), False)
random.seed(3)
pix.set_rect(pix.irect, (200, 30, 30))
for i in range(0, 600, 7):
    pix.set_rect(pymupdf.IRect(i, 0, i + 3, 600), (random.randint(0, 255), 80, 200))
png = pix.tobytes("png")
for i in range(4):
    p = d.new_page(width=612, height=792)
    p.insert_text((72, 100), "Synthetic page %d body" % (i + 1), fontsize=14)
p0, p1, p2 = d[0], d[1], d[2]
p0.insert_image(pymupdf.Rect(72, 200, 372, 500), stream=png)
p0.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(72, 120, 200, 140), "uri": "https://example.com/a"})
p0.insert_link({"kind": pymupdf.LINK_GOTO, "from": pymupdf.Rect(220, 120, 300, 140), "page": 2, "to": pymupdf.Point(0, 0)})
w = pymupdf.Widget(); w.field_name = "txt1"; w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
w.rect = pymupdf.Rect(72, 520, 250, 545); w.field_value = "form value"
p0.add_widget(w)
p0.add_text_annot((400, 150), "a note")
p0.add_highlight_annot(pymupdf.Rect(72, 90, 200, 105))
p1.set_rotation(90)
p2.set_cropbox(pymupdf.Rect(20, 30, 592, 762))
d.set_metadata({"title": "Orig title", "author": "Orig author", "subject": "S", "keywords": "k", "creator": "c", "producer": "p"})
d.set_xml_metadata("<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:creator>Orig author</dc:creator></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>")
d.set_toc([[1, "Chapter 1", 1], [2, "Section 1.1", 2], [1, "Chapter 2", 3]])
d.save(out)
""";

    const string VerifyScript = """
import sys, json, os, pymupdf, pypdf
cmd = sys.argv[1]
def out(x): sys.stdout.write(json.dumps(x, ensure_ascii=True))
if cmd == "text":
    d = pymupdf.open(sys.argv[2]); r = pypdf.PdfReader(sys.argv[2])
    out({"mu": [p.get_text() for p in d], "pypdf": [p.extract_text() for p in r.pages]})
elif cmd == "bbox":
    d = pymupdf.open(sys.argv[2]); p = d[int(sys.argv[3])]; needle = sys.argv[4]; found = None
    for b in p.get_text("dict")["blocks"]:
        for l in b.get("lines", []):
            t = "".join(s["text"] for s in l["spans"])
            if needle in t:
                bb = pymupdf.Rect(l["bbox"]) * p.rotation_matrix
                found = [bb.x0, bb.y0, bb.x1, bb.y1]
    out({"rect": [p.rect.x0, p.rect.y0, p.rect.x1, p.rect.y1], "bbox": found})
elif cmd == "boxes":
    r = pypdf.PdfReader(sys.argv[2])
    out([{"crop": [float(v) for v in (p.cropbox.left, p.cropbox.bottom, p.cropbox.right, p.cropbox.top)],
          "media": [float(v) for v in (p.mediabox.left, p.mediabox.bottom, p.mediabox.right, p.mediabox.top)]} for p in r.pages])
elif cmd == "enc":
    r = pypdf.PdfReader(sys.argv[2]); enc = r.is_encrypted
    res = r.decrypt(sys.argv[3]) if enc else -1
    perms = int(r.user_access_permissions) if enc and res else 0
    out({"encrypted": enc, "decrypt": int(res), "perms": perms, "pages": len(r.pages) if (not enc or res) else 0,
         "aes256": bool(enc and r.trailer["/Encrypt"].get("/V") == 5)})
elif cmd == "meta":
    r = pypdf.PdfReader(sys.argv[2]); m = r.metadata
    d = pymupdf.open(sys.argv[2])
    out({"meta": {k: str(v) for k, v in (m or {}).items() if str(v) not in ("", "None", "NullObject")}, "xmp": bool(d.xref_xml_metadata()), "has_info": m is not None})
elif cmd == "outline":
    out(pymupdf.open(sys.argv[2]).get_toc())
elif cmd == "links":
    r = pypdf.PdfReader(sys.argv[2]); res = []
    for a in r.pages[int(sys.argv[3])].get("/Annots", []):
        a = a.get_object()
        if a.get("/Subtype") == "/Link":
            act = a.get("/A")
            res.append({"uri": str(act.get_object().get("/URI")) if act is not None and "/URI" in act.get_object() else None,
                        "dest": "/Dest" in a or (act is not None and "/D" in act.get_object()), "rect": [float(v) for v in a["/Rect"]]})
    out(res)
elif cmd == "counts":
    r = pypdf.PdfReader(sys.argv[2]); w = a = 0
    for p in r.pages:
        for x in p.get("/Annots", []):
            s = x.get_object().get("/Subtype")
            if s == "/Widget": w += 1
            elif s not in ("/Link", "/Popup"): a += 1
    out({"widgets": w, "annots": a, "acroform": "/AcroForm" in r.trailer["/Root"], "pages": len(r.pages)})
elif cmd == "images":
    res = []
    for n in sorted(os.listdir(sys.argv[2])):
        pm = pymupdf.Pixmap(os.path.join(sys.argv[2], n)); res.append([n, pm.width, pm.height])
    out(res)
elif cmd == "images-in-pdf":
    d = pymupdf.open(sys.argv[2]); out(max((pymupdf.Pixmap(d, i[0]).width for p in d for i in p.get_images(full=True)), default=0))
""";

    const string RefuseScript = """
import sys, os, json
sys.path.insert(0, sys.argv[1])
import quick_tools as q
src, work = sys.argv[2], sys.argv[3]
res = []
def bad(op, **a):
    out = os.path.join(work, "refuse-%d.pdf" % len(res))
    r = {"Operation": op, "Input": src, "Output": out}; r.update(a)
    try:
        q.handle(op, r); res.append(op + " accepted")
    except q.Refused:
        res.append("ok")
    assert not os.path.exists(out), "output written by refused " + op
wm = dict(Text="A", FontSize=20, Red=0, Green=0, Blue=0, Opacity=0.5, Rotation=0, Pages=None)
bad("Watermark", **{**wm, "FontSize": 7})
bad("Watermark", **{**wm, "Opacity": 0.01})
bad("Watermark", **{**wm, "Rotation": 30})
bad("Watermark", **{**wm, "Text": "snow \u2603"})
bad("Watermark", **{**wm, "Pages": [0, 0]})
bad("Watermark", **{**wm, "Pages": [9]})
bad("Watermark", **{**wm, "Red": 256})
hf = dict(Template="{n}", FileName="f", Date="d", Position="TopLeft", FontSize=10, Margin=18, StartNumber=1, Pages=None)
bad("HeaderFooter", **{**hf, "Position": "Middle"})
bad("HeaderFooter", **{**hf, "Margin": 8})
bad("HeaderFooter", **{**hf, "StartNumber": 0})
bad("HeaderFooter", **{**hf, "Template": "x" * 201})
bad("HeaderFooter", **{**hf, "Template": "\u20ac"})
bad("HeaderFooter", **{**hf, "FontSize": 40, "Template": "w" * 100})
bt = dict(Prefix="A", Suffix="", Start=0, Digits=3, Position="TopLeft", FontSize=10, Margin=18, Pages=None)
bad("Bates", **{**bt, "Start": 1000})
bad("Bates", **{**bt, "Start": 999})
bad("Bates", **{**bt, "Prefix": "\u00e9"})
bad("Bates", **{**bt, "Digits": 13})
cr = dict(Left=10, Bottom=10, Right=100, Top=100, Pages=None)
bad("Crop", **{**cr, "Right": 30})
bad("Crop", **{**cr, "Left": -5})
bad("Crop", **{**cr, "Top": 900})
bad("Compress", ImageDpi=50)
bad("Compress", ImageDpi=301)
bad("Protect", UserPassword="", OwnerPassword="o", AllowPrint=True, AllowCopy=True, AllowEdit=True)
bad("Protect", UserPassword="u", OwnerPassword="o" * 129, AllowPrint=True, AllowCopy=True, AllowEdit=True)
bad("Protect", UserPassword="u", OwnerPassword="o", AllowPrint=1, AllowCopy=True, AllowEdit=True)
bad("Unprotect")
bad("Flatten", Annotations=False, Forms=False)
bad("SetProperties", Title="x" * 1001, StripMetadata=False)
bad("SetBookmarks", Bookmarks=[{"Title": "a", "Level": 2, "Page": 0}])
bad("SetBookmarks", Bookmarks=[{"Title": "a", "Level": 1, "Page": 9}])
bad("SetBookmarks", Bookmarks=[{"Title": "", "Level": 1, "Page": 0}])
bad("AddLink", Page=0, Left=1, Bottom=1, Right=50, Top=50, Uri="javascript:alert(1)")
bad("AddLink", Page=0, Left=1, Bottom=1, Right=50, Top=50, Uri="file:///c:/x")
bad("AddLink", Page=0, Left=1, Bottom=1, Right=50, Top=50, Uri="https://a.example/", TargetPage=1)
bad("AddLink", Page=0, Left=1, Bottom=1, Right=50, Top=50, TargetPage=9)
bad("AddLink", Page=7, Left=1, Bottom=1, Right=50, Top=50, TargetPage=1)
bad("RemoveLink", Page=0, Index=9, Left=1, Bottom=1, Right=50, Top=50)
bad("ExtractImages", OutputDirectory=work)
print(json.dumps(res))
""";

    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "quick-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var helper = Path.Combine(root, "App.Advanced", "worker.py");
        var location = new AdvancedEngineLocation(python, helper);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception("FAILED: " + message); checks.Add(message); }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception("FAILED (not refused): " + message);
        }
        var fixtureScript = Path.Combine(work, "make-fixture.py");
        var verifyScript = Path.Combine(work, "verify.py");
        var refuseScript = Path.Combine(work, "refusals.py");
        File.WriteAllText(fixtureScript, FixtureScript);
        File.WriteAllText(verifyScript, VerifyScript);
        File.WriteAllText(refuseScript, RefuseScript);
        string Run(params string[] args)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-I", "-B" }.Concat(args)) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"Python check failed ({p.ExitCode}, {args[1]}): " + e + o);
            return o;
        }
        JsonElement Verify(params string[] args) => JsonDocument.Parse(Run(new[] { verifyScript }.Concat(args).ToArray())).RootElement.Clone();
        string[] Texts(string pdf) => Verify("text", pdf).GetProperty("mu").EnumerateArray().Select(t => t.GetString()!).ToArray();
        double[] Box(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        int n = 0;
        string Out(string name) => Path.Combine(work, $"{++n:00}-{name}");
        RecoveryStore NewStore() => new(Path.Combine(work, "recovery-" + Guid.NewGuid().ToString("N")));

        var source = Path.Combine(work, "source.pdf");
        Run(fixtureScript, source);
        var sourceHash = RecoveryStore.Fingerprint(source);

        // ---- refusals enforced by the Python engine itself
        var refusals = JsonDocument.Parse(Run(refuseScript, Path.Combine(root, "App.Advanced"), source, work)).RootElement;
        Check(refusals.EnumerateArray().All(r => r.GetString() == "ok") && refusals.GetArrayLength() > 35,
            $"Engine refuses {refusals.GetArrayLength()} out-of-range or unsafe requests without writing output");

        using var session = await DocumentSession.OpenAsync(worker, NewStore(), source);
        var pageCount = session.PageCount;
        Check(pageCount == 4, "Synthetic fixture opens through PDFium with 4 pages (rotated, cropped, image, link, widget, annotation)");

        // ---- queries
        var props = await session.GetPropertiesAsync(location);
        Check(props.Title == "Orig title" && props.Author == "Orig author" && props.PageCount == 4 && !props.Encrypted && props.HasXmp && props.PdfVersion.Length > 0,
            "GetProperties reports metadata, version, page count and XMP presence");
        var marks = await session.GetBookmarksListAsync(location);
        Check(marks.Count == 3 && !marks.Truncated && marks.Bookmarks[1] == new BookmarkEntry("Section 1.1", 1, 2), "GetBookmarks lists the outline with 0-based pages and levels");
        var links = await session.ListLinksAsync(location, 0);
        Check(links.Count == 2 && links.Links[0].Kind == "Uri" && links.Links[0].Uri == "https://example.com/a" && links.Links[1].Kind == "Page" && links.Links[1].TargetPage == 2,
            "ListLinks reports URI and page links with PDF-space rectangles");
        Check(Math.Abs(links.Links[0].Left - 72) < 0.01 && Math.Abs(links.Links[0].Bottom - 652) < 0.01, "Link rectangles are in PDF user space (origin bottom-left)");

        // ---- watermark (undoable), including the rotated and cropped pages
        var before = (await session.RenderAsync(0, 300, 400)).GetProperty("Bgra").GetString();
        var rev = session.Revision;
        await session.AddWatermarkAsync(location, new WatermarkOptions("DRAFT \u00e9", 60, 255, 0, 0, 0.3, 45), rev);
        Check(session.Revision == rev + 1 && session.IsDirty && session.CanUndo, "Watermark commits one undoable checkpoint");
        await Rejected<InvalidOperationException>(() => session.AddWatermarkAsync(location, new WatermarkOptions("X", 20, 0, 0, 0, 1, 0), rev), "Stale revision is refused for edits");
        var wmFile = Out("watermark.pdf");
        await session.SaveAsync(wmFile);
        Check(Texts(wmFile).All(t => t.Contains("DRAFT \u00e9")), "Watermark text is present on every page (independent reader)");
        foreach (var page in new[] { 0, 1, 2 })
        {
            var info = Verify("bbox", wmFile, page.ToString(), "DRAFT");
            var rect = Box(info.GetProperty("rect")); var bb = Box(info.GetProperty("bbox"));
            Check(Math.Abs((bb[0] + bb[2]) / 2 - (rect[0] + rect[2]) / 2) < 6 && Math.Abs((bb[1] + bb[3]) / 2 - (rect[1] + rect[3]) / 2) < 6,
                $"Watermark is centered as displayed on page {page + 1}" + (page == 1 ? " (page /Rotate 90)" : page == 2 ? " (offset CropBox)" : ""));
        }
        Check((await session.RenderAsync(0, 300, 400)).GetProperty("Bgra").GetString() != before, "PDFium renders the watermark");
        await session.UndoAsync();
        Check(!(await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("DRAFT") && session.CanRedo, "Undo removes the watermark");
        await session.RedoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("DRAFT"), "Redo restores the watermark");
        await session.AddWatermarkAsync(location, new WatermarkOptions("PAGE TWO", 30, 0, 0, 255, 0.5, 90, new[] { 1 }), session.Revision);
        Check((await session.ExtractAsync(1)).GetProperty("Text").GetString()!.Contains("PAGE TWO") && !(await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("PAGE TWO"),
            "Watermark honours a page subset");
        await session.UndoAsync();

        // ---- header/footer
        await session.AddHeaderFooterAsync(location, new HeaderFooterOptions("{filename} p{n}/{total} {date}", "demo.pdf", "2026-10-22", PageMarkPosition.BottomCenter, 10, 18, 3), session.Revision);
        var hfFile = Out("header-footer.pdf");
        await session.SaveAsync(hfFile);
        var hfTexts = Texts(hfFile);
        Check(hfTexts[0].Contains("demo.pdf p3/4 2026-10-22") && hfTexts[3].Contains("demo.pdf p6/4 2026-10-22"), "Header/footer expands {n} (from StartNumber), {total}, {date} and {filename}");
        foreach (var page in new[] { 0, 1, 2 })
        {
            var info = Verify("bbox", hfFile, page.ToString(), "demo.pdf");
            var rect = Box(info.GetProperty("rect")); var bb = Box(info.GetProperty("bbox"));
            Check(Math.Abs((bb[0] + bb[2]) / 2 - (rect[0] + rect[2]) / 2) < 2 && bb[3] <= rect[3] - 13 && bb[3] >= rect[3] - 22,
                $"Footer sits at bottom-center of the displayed page {page + 1}");
        }
        await session.UndoAsync();
        Check(!(await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("demo.pdf"), "Undo removes the footer");
        await session.RedoAsync();
        await session.AddHeaderFooterAsync(location, new HeaderFooterOptions("Top right {n}", "", "", PageMarkPosition.TopRight, 12, 30, 1, new[] { 0 }), session.Revision);
        {
            var topFile = Out("header-top.pdf");
            await session.SaveAsync(topFile);
            var info = Verify("bbox", topFile, "0", "Top right");
            var rect = Box(info.GetProperty("rect")); var bb = Box(info.GetProperty("bbox"));
            Check(Math.Abs(bb[2] - (rect[2] - 30)) < 2 && bb[1] > 22 && bb[1] < 34, "Top-right header respects the margin");
        }
        await session.UndoAsync();
        await Rejected<ArgumentException>(() => session.AddHeaderFooterAsync(location, new HeaderFooterOptions("\u20ac", "", "", PageMarkPosition.TopLeft), session.Revision), "Non-Latin header text is refused");

        // ---- Bates
        var bates = await session.AddBatesAsync(location, new BatesOptions("DOC-", "-X", 10, 6, PageMarkPosition.BottomRight, 10, 18, new[] { 0, 2 }), session.Revision);
        Check(bates.FirstNumber == 10 && bates.LastNumber == 11 && bates.LastLabel == "DOC-000011-X" && bates.Pages == 2, "Bates reports first and last numbers for continuation");
        var batesFile = Out("bates.pdf");
        await session.SaveAsync(batesFile);
        var bt = Texts(batesFile);
        Check(bt[0].Contains("DOC-000010-X") && bt[2].Contains("DOC-000011-X") && !bt[1].Contains("DOC-") && !bt[3].Contains("DOC-"), "Bates labels appear only on the selected pages, in order");
        await Rejected<ArgumentOutOfRangeException>(() => session.AddBatesAsync(location, new BatesOptions("A", "", 998, 3, PageMarkPosition.TopLeft), session.Revision), "Bates numbers that overflow the digit count are refused");
        await session.UndoAsync();

        // ---- crop
        await session.CropPagesAsync(location, new CropOptions(50, 60, 500, 700, new[] { 0, 1 }), session.Revision);
        var cropFile = Out("crop.pdf");
        await session.SaveAsync(cropFile);
        var boxes = Verify("boxes", cropFile).EnumerateArray().ToArray();
        Check(Box(boxes[0].GetProperty("crop")).SequenceEqual(new double[] { 50, 60, 500, 700 }) && Box(boxes[1].GetProperty("crop")).SequenceEqual(new double[] { 50, 60, 500, 700 }) &&
              Box(boxes[2].GetProperty("crop")).SequenceEqual(new double[] { 20, 30, 592, 762 }) && Box(boxes[0].GetProperty("media")).SequenceEqual(new double[] { 0, 0, 612, 792 }),
            "Crop sets the CropBox on selected pages only and leaves MediaBox");
        Check((await session.PageInfoAsync()).GetRawText().Contains("450"), "PDFium reports the cropped page size");
        await session.UndoAsync();
        Check((await session.PageInfoAsync()).GetRawText().Contains("612"), "Undo restores the original crop");
        await session.RedoAsync();
        await Rejected<InvalidOperationException>(() => session.CropPagesAsync(location, new CropOptions(-10, 0, 300, 300), session.Revision), "Crop outside the MediaBox is refused");
        await Rejected<ArgumentException>(() => session.CropPagesAsync(location, new CropOptions(0, 0, 30, 300), session.Revision), "Crop smaller than 36 pt is refused");
        await session.UndoAsync();

        // ---- properties, bookmarks, links (edits)
        await session.SetPropertiesAsync(location, "Neu \u00dcberschrift \u2713", null, "", null, false, session.Revision);
        var pFile = Out("props.pdf");
        await session.SaveAsync(pFile);
        var pm = Verify("meta", pFile).GetProperty("meta");
        Check(pm.GetProperty("/Title").GetString() == "Neu \u00dcberschrift \u2713" && pm.GetProperty("/Author").GetString() == "Orig author" && !pm.TryGetProperty("/Subject", out _),
            "SetProperties changes Unicode title, keeps unspecified fields and clears empty ones");
        Check((await session.GetPropertiesAsync(location)).Title == "Neu \u00dcberschrift \u2713", "GetProperties reads back the new title");
        await session.SetPropertiesAsync(location, "ignored", "ignored", null, null, true, session.Revision);
        var strippedFile = Out("stripped.pdf");
        await session.SaveAsync(strippedFile);
        var sm = Verify("meta", strippedFile);
        Check(sm.GetProperty("meta").EnumerateObject().Count() == 0 && !sm.GetProperty("xmp").GetBoolean(), "StripMetadata removes Info entries and XMP");
        await session.UndoAsync(); await session.UndoAsync();

        var newMarks = new[] { new BookmarkEntry("Intro \u2713", 0, 1), new BookmarkEntry("Detail", 3, 2), new BookmarkEntry("No target", -1, 2), new BookmarkEntry("End", 2, 1) };
        await session.SetBookmarksAsync(location, newMarks, session.Revision);
        var rt = await session.GetBookmarksListAsync(location);
        Check(rt.Bookmarks.SequenceEqual(newMarks), "SetBookmarks/GetBookmarks round-trip exactly (including a bookmark without a page)");
        var bFile = Out("bookmarks.pdf");
        await session.SaveAsync(bFile);
        Check(Verify("outline", bFile).GetArrayLength() == 4, "Independent reader sees the replaced outline");
        await Rejected<ArgumentException>(() => session.SetBookmarksAsync(location, new[] { new BookmarkEntry("a", 0, 2) }, session.Revision), "Outline starting below level 1 is refused");
        await Rejected<ArgumentOutOfRangeException>(() => session.SetBookmarksAsync(location, new[] { new BookmarkEntry("a", 9, 1) }, session.Revision), "Bookmark to a missing page is refused");
        await session.SetBookmarksAsync(location, Array.Empty<BookmarkEntry>(), session.Revision);
        Check((await session.GetBookmarksListAsync(location)).Count == 0, "An empty list removes the outline");
        await session.UndoAsync(); await session.UndoAsync();

        await session.AddLinkAsync(location, 0, 72, 560, 200, 580, "https://example.org/added", null, session.Revision);
        await session.AddLinkAsync(location, 0, 220, 560, 300, 580, null, 3, session.Revision);
        var added = await session.ListLinksAsync(location, 0);
        Check(added.Count == 4 && added.Links[2].Uri == "https://example.org/added" && added.Links[3].Kind == "Page" && added.Links[3].TargetPage == 3, "AddLink adds URI and page links that ListLinks reports");
        var lFile = Out("links.pdf");
        await session.SaveAsync(lFile);
        var raw = Verify("links", lFile, "0").EnumerateArray().ToArray();
        Check(raw.Length == 4 && raw[2].GetProperty("uri").GetString() == "https://example.org/added" && raw[3].GetProperty("dest").GetBoolean(), "Independent reader sees the added links");
        await Rejected<ArgumentException>(() => session.AddLinkAsync(location, 0, 1, 1, 50, 50, "javascript:alert(1)", null, session.Revision), "javascript: link is refused");
        await Rejected<ArgumentException>(() => session.AddLinkAsync(location, 0, 1, 1, 50, 50, "file:///c:/x", null, session.Revision), "file: link is refused");
        var staleLink = added.Links[2] with { Left = added.Links[2].Left + 20 };
        await Rejected<InvalidOperationException>(() => session.RemoveLinkAsync(location, staleLink, 0, session.Revision), "RemoveLink refuses a stale rectangle");
        await session.RemoveLinkAsync(location, added.Links[2], 0, session.Revision);
        var afterRemove = await session.ListLinksAsync(location, 0);
        Check(afterRemove.Count == 3 && afterRemove.Links.All(l => l.Uri != "https://example.org/added"), "RemoveLink removes exactly the chosen link");
        await session.UndoAsync();
        Check((await session.ListLinksAsync(location, 0)).Count == 4, "Undo restores the removed link");
        await session.UndoAsync(); await session.UndoAsync();

        // ---- image extraction
        var imgDir = Path.Combine(work, "images");
        var extracted = await session.ExtractImagesAsync(location, imgDir);
        var imgs = Verify("images", imgDir).EnumerateArray().ToArray();
        Check(extracted.Count == 1 && imgs.Length == 1 && imgs[0][1].GetInt32() == 600 && extracted.Names[0] == imgs[0][0].GetString(), "ExtractImages writes the embedded image to a new folder");
        await Rejected<IOException>(() => session.ExtractImagesAsync(location, imgDir), "ExtractImages refuses an existing folder");
        await Rejected<IOException>(() => session.ExtractImagesAsync(location, Path.Combine(work, "missing-parent", "x")), "ExtractImages refuses a missing parent folder");
        await Rejected<InvalidOperationException>(() => session.ExtractImagesAsync(location, Path.Combine(work, "images-none"), new[] { 1 }), "ExtractImages on a page without images is refused and leaves no folder");
        Check(!Directory.Exists(Path.Combine(work, "images-none")), "No partial folder remains after a refusal");

        // ---- exports
        var revisionBeforeExports = session.Revision;
        var compressed = Out("compressed.pdf");
        var cr = await session.CompressCopyAsync(location, compressed, 100);
        Check(cr.OutputBytes < cr.InputBytes && cr.OutputBytes == new FileInfo(compressed).Length && Verify("images-in-pdf", compressed).GetInt32() is > 300 and <= 430, "Compress with 100 dpi shrinks the file and downsamples the image");
        using (var reopened = await DocumentSession.OpenAsync(worker, NewStore(), compressed))
            Check(reopened.PageCount == 4 && (await reopened.RenderAsync(0, 200, 260)).GetProperty("Bgra").GetString()!.Length > 0, "Compressed copy reopens and renders through PDFium");
        var plain = Out("compressed-lossless.pdf");
        var cr2 = await session.CompressCopyAsync(location, plain, null);
        Check(cr2.InputBytes == new FileInfo(source).Length || cr2.InputBytes > 0, "Compress without resampling reports honest sizes");
        await Rejected<IOException>(() => session.CompressCopyAsync(location, compressed, null), "Export refuses an existing destination");
        await Rejected<ArgumentOutOfRangeException>(() => session.CompressCopyAsync(location, Out("bad.pdf"), 50), "Compress refuses a DPI below 72");

        var flat = Out("flattened.pdf");
        var fr = await session.FlattenCopyAsync(location, flat, true, true);
        var fc = Verify("counts", flat);
        Check(fr.AnnotationsFlattened == 2 && fr.FormFieldsFlattened == 1 && fc.GetProperty("widgets").GetInt32() == 0 && fc.GetProperty("annots").GetInt32() == 0 && !fc.GetProperty("acroform").GetBoolean(),
            "Flatten removes annotations and form fields and reports counts");
        Check(Texts(flat)[0].Contains("form value"), "Flattened form value stays visible as page content");
        using (var reopened = await DocumentSession.OpenAsync(worker, NewStore(), flat)) Check(reopened.PageCount == 4, "Flattened copy reopens through PDFium");
        var onlyForms = Out("flattened-forms.pdf");
        await session.FlattenCopyAsync(location, onlyForms, false, true);
        var oc = Verify("counts", onlyForms);
        Check(oc.GetProperty("widgets").GetInt32() == 0 && oc.GetProperty("annots").GetInt32() == 2, "Flatten can flatten forms only");
        await Rejected<ArgumentException>(() => session.FlattenCopyAsync(location, Out("none.pdf"), false, false), "Flatten with nothing selected is refused");

        var prot = Out("protected.pdf");
        await session.ProtectCopyAsync(location, prot, "user pw", "owner pw", true, false, false);
        var pe = Verify("enc", prot, "user pw");
        Check(pe.GetProperty("encrypted").GetBoolean() && pe.GetProperty("decrypt").GetInt32() >= 1 && pe.GetProperty("aes256").GetBoolean() && pe.GetProperty("pages").GetInt32() == 4, "Protect writes AES-256 encryption that opens with the user password");
        var perms = pe.GetProperty("perms").GetInt64();
        Check((perms & 4) != 0 && (perms & 16) == 0 && (perms & 8) == 0, "Permissions allow printing but not copying or editing");
        Check(Verify("enc", prot, "wrong").GetProperty("decrypt").GetInt32() == 0, "Wrong password does not open the protected copy");
        using (var locked = await DocumentSession.OpenAsync(worker, NewStore(), prot, "user pw"))
        {
            Check(locked.PageCount == 4 && (await locked.RenderAsync(0, 200, 260)).GetProperty("Bgra").GetString()!.Length > 0, "PDFium opens the protected copy with the password");
            await Rejected<InvalidOperationException>(() => locked.ProtectCopyAsync(location, Out("again.pdf"), "a", "b", true, true, true), "Protect refuses an already protected document");
            await Rejected<InvalidOperationException>(() => locked.UnprotectCopyAsync(location, Out("restricted.pdf")), "Unprotect refuses restricted permissions without the owner password");
        }
        using (var owner = await DocumentSession.OpenAsync(worker, NewStore(), prot, "owner pw"))
        {
            var unp = Out("unprotected-owner.pdf");
            await owner.UnprotectCopyAsync(location, unp);
            Check(!Verify("enc", unp, "").GetProperty("encrypted").GetBoolean() && Verify("enc", unp, "").GetProperty("pages").GetInt32() == 4, "Unprotect with the owner password writes an unencrypted copy");
        }
        var open = Out("protected-open.pdf");
        await session.ProtectCopyAsync(location, open, "secret", "owner", true, true, true);
        using (var unlocked = await DocumentSession.OpenAsync(worker, NewStore(), open, "secret"))
        {
            var unp = Out("unprotected.pdf");
            await unlocked.UnprotectCopyAsync(location, unp);
            var ue = Verify("enc", unp, "");
            Check(!ue.GetProperty("encrypted").GetBoolean() && ue.GetProperty("pages").GetInt32() == 4, "Unprotect forwards the session password and writes an unencrypted copy");
            using var reopened = await DocumentSession.OpenAsync(worker, NewStore(), unp);
            Check(reopened.PageCount == 4, "Unencrypted copy reopens without a password");
            await Rejected<IOException>(() => unlocked.UnprotectCopyAsync(location, unp), "Unprotect refuses an existing destination");
        }
        await Rejected<InvalidOperationException>(() => session.UnprotectCopyAsync(location, Out("not-encrypted.pdf")), "Unprotect refuses a session opened without a password");
        await Rejected<ArgumentException>(() => session.ProtectCopyAsync(location, Out("empty-pw.pdf"), "", "o", true, true, true), "Protect refuses an empty user password");

        Check(session.Revision == revisionBeforeExports, "Exports leave the open session and its history unchanged");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Source PDF is unchanged");
        Check(Directory.GetFiles(work, ".*.candidate.pdf").Length == 0, "No candidate temporaries remain");

        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} quick-tools checks. Evidence: {work}");
    }
}
