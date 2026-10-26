using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HunterPdf.Core;

static class ImportTier2Proof
{
    const string FixtureScript = """
import sys, os, pymupdf
out = sys.argv[1]
os.makedirs(out, exist_ok=True)
def doc(texts):
    d = pymupdf.open()
    for t in texts:
        d.new_page(width=612, height=792).insert_text((72, 100), t, fontsize=18)
    return d
h = doc(["NESTED TAGGED"]); hp = h[0]; hp.clean_contents()
x = hp.get_contents()[0]; h.update_stream(x, b"/Span <</MCID 0>> BDC " + h.xref_stream(x) + b" EMC")
rich = doc(["RICH ONE", "RICH TWO", "RICH THREE"])
p0, p1, p2 = rich[0], rich[1], rich[2]
a = p0.add_rect_annot(pymupdf.Rect(300, 300, 400, 400)); a.set_colors(stroke=(1, 0, 0), fill=(1, 0, 0)); a.update()
p0.insert_link({"kind": pymupdf.LINK_URI, "from": pymupdf.Rect(72, 120, 200, 140), "uri": "https://example.com/a"})
w = pymupdf.Widget(); w.field_name = "f1"; w.field_type = pymupdf.PDF_WIDGET_TYPE_TEXT
w.rect = pymupdf.Rect(72, 200, 300, 230); w.field_value = "FORMVALUE"; p1.add_widget(w)
p2.show_pdf_page(pymupdf.Rect(72, 200, 372, 400), h, 0)
p2.clean_contents(); c = p2.get_contents()[0]
rich.update_stream(c, b"/P <</MCID 1>> BDC " + rich.xref_stream(c) + b" EMC")
rich.set_toc([[1, "Intro", 1], [2, "Detail", 2], [1, "End", 3]])
rich.set_page_labels([{"startpage": 0, "prefix": "R-", "style": "D", "firstpagenum": 1}])
cat = rich.pdf_catalog()
rich.xref_set_key(cat, "MarkInfo", "<</Marked true>>")
s = rich.get_new_xref(); rich.update_object(s, "<</Type/StructTreeRoot>>"); rich.xref_set_key(cat, "StructTreeRoot", "%d 0 R" % s)
rich.xref_set_key(cat, "OpenAction", "<</S/%s/JS(0)>>" % ("Java" + "Script"))
rich.embfile_add("note.txt", b"attached")
rich.save(os.path.join(out, "rich.pdf"))
doc(["DEST ONE", "DEST TWO"]).save(os.path.join(out, "dest.pdf"))
doc(["SECRET ONE"]).save(os.path.join(out, "enc.pdf"), encryption=pymupdf.PDF_ENCRYPT_AES_256, user_pw="pw", owner_pw="own")
for name, texts, toc in (("a", ["A1", "A2"], [[1, "A-ch1", 1], [1, "A-ch2", 2]]),
                         ("b", ["B1", "B2", "B3"], [[1, "B-sec", 1], [2, "B-sub", 3]]), ("c", ["C1"], [])):
    d = doc(texts)
    if toc: d.set_toc(toc)
    d.save(os.path.join(out, name + ".pdf"))
""";

    const string VerifyScript = """
import sys, json, pypdf
cmd, path = sys.argv[1], sys.argv[2]
r = pypdf.PdfReader(path)
def walk(items):
    out = []
    for it in items:
        if isinstance(it, list): out[-1]["children"] = walk(it)
        else: out.append({"title": it.title, "page": r.get_destination_page_number(it), "children": []})
    return out
if cmd == "text": print(json.dumps([p.extract_text().strip() for p in r.pages]))
elif cmd == "outline": print(json.dumps(walk(r.outline)))
elif cmd == "structure":
    root = r.trailer["/Root"]
    marked = any(b"BDC" in p.get_contents().get_data() or b"BMC" in p.get_contents().get_data() for p in r.pages)
    print(json.dumps({"root": sorted(root.keys()), "annots": sum(len(p.get("/Annots", [])) for p in r.pages), "marked": marked,
                      "structparents": any("/StructParents" in p for p in r.pages), "pages": len(r.pages)}))
""";

    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "import-tier2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var location = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        async Task Throws(Func<Task> action, string message)
        {
            try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException) { checks.Add(message); return; }
            throw new Exception(message);
        }
        var fixtureScript = Path.Combine(work, "make-fixtures.py"); var verifyScript = Path.Combine(work, "verify.py");
        File.WriteAllText(fixtureScript, FixtureScript); File.WriteAllText(verifyScript, VerifyScript);
        string Run(params string[] args)
        {
            var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-I", "-B" }.Concat(args)) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception($"Python check failed ({p.ExitCode}, {args[1]}): " + e + o);
            return o;
        }
        JsonElement Verify(string cmd, string pdf) => JsonDocument.Parse(Run(verifyScript, cmd, pdf)).RootElement.Clone();
        string[] Texts(string pdf) => Verify("text", pdf).EnumerateArray().Select(t => t.GetString()!).ToArray();
        RecoveryStore NewStore() => new(Path.Combine(work, "recovery-" + Guid.NewGuid().ToString("N")));
        Run(fixtureScript, work);
        string F(string name) => Path.Combine(work, name);
        var rich = F("rich.pdf"); var dest = F("dest.pdf"); var enc = F("enc.pdf");
        var hashes = new[] { "rich.pdf", "dest.pdf", "enc.pdf", "a.pdf", "b.pdf", "c.pdf" }.ToDictionary(n => n, n => RecoveryStore.Fingerprint(F(n)));
        bool Unchanged() => hashes.All(h => RecoveryStore.Fingerprint(F(h.Key)) == h.Value);
        async Task<(int R, int G, int B)> Pixel(DocumentSession s, int page, int x, int y)
        {
            var bytes = Convert.FromBase64String((await s.RenderAsync(page, 612, 792)).GetProperty("Bgra").GetString()!);
            var i = (y * 612 + x) * 4; return (bytes[i + 2], bytes[i + 1], bytes[i]);
        }
        async Task<string> PageText(DocumentSession s, int page) => (await s.ExtractAsync(page)).GetProperty("Text").GetString()!;
        int SessionFiles(RecoveryStore store, DocumentSession s) => Directory.GetFiles(store.DirectoryFor(s.Id), "*.pdf").Length;

        // ---- strict path still refuses, best-effort accepts
        var store1 = NewStore();
        using var session = await DocumentSession.OpenAsync(worker, store1, dest);
        Check(session.PageCount == 2, "Destination PDF opens with 2 pages");
        var strict = await ImportAssert.ThrowsAsync<InvalidOperationException>(() => session.InsertPagesAsync(rich, [0, 1, 2], 1));
        Check(DocumentSession.IsSourceStructureRefusal(strict) && session.PageCount == 2 && !session.IsDirty, "Strict insert refuses the rich source and leaves the document untouched");
        var filesBefore = SessionFiles(store1, session);
        var report = await session.InsertPagesBestEffortAsync(location, rich, [0, 1, 2], 1);
        var d = report.Dropped;
        Check(report.Tier == "BestEffort" && report.PagesInserted == 3 && session.PageCount == 5 && session.IsDirty, "Best-effort insert adds 3 pages as one edit and reports tier BestEffort");
        Check(d["Bookmarks"] == 3 && d["PageLabels"] == 1 && d["Attachments"] == 1 && d["StructureTree"] == 1 && d["JavaScriptAndActions"] >= 1 && d["Links"] == 1 &&
              d["AnnotationsFlattened"] == 1 && d["FormFieldsFlattened"] == 1 && d["Annotations"] == 0 && d["FormFields"] == 0 && d["MarkedContentSections"] >= 2,
              "Report counts every dropped category (bookmarks, labels, attachment, tags, scripts, link) and the flattened annotation and form field");
        Check(report.Describe().Contains("3 bookmarks") && report.Describe().Contains("kept as page content"), "Report renders a plain-language notice");
        Check(SessionFiles(store1, session) == filesBefore + 1, "Temporary source and sanitized copies are deleted (only the new checkpoint remains)");
        Check(Unchanged(), "Source and destination files are byte-identical after import");
        Check((await PageText(session, 0)).Contains("DEST ONE") && (await PageText(session, 1)).Contains("RICH ONE") && (await PageText(session, 2)).Contains("RICH TWO") &&
              (await PageText(session, 3)).Contains("RICH THREE") && (await PageText(session, 4)).Contains("DEST TWO"), "PDFium shows source page content at the insertion point with destination pages intact");
        Check((await PageText(session, 2)).Contains("FORMVALUE") && (await PageText(session, 3)).Contains("NESTED TAGGED"), "Flattened form value and nested tagged form content are present as text");
        var red = await Pixel(session, 1, 350, 350);
        Check(red.R > 240 && red.G < 20 && red.B < 20, "PDFium renders the flattened annotation as red pixels in its area");
        var saved = F("saved-besteffort.pdf");
        await session.SaveAsync(saved);
        var texts = Texts(saved);
        Check(texts.Length == 5 && texts[1].Contains("RICH ONE") && texts[2].Contains("FORMVALUE") && texts[3].Contains("NESTED TAGGED"), "Independent reader (pypdf) finds the imported text");
        var structure = Verify("structure", saved);
        var rootKeys = structure.GetProperty("root").EnumerateArray().Select(k => k.GetString()).ToArray();
        Check(structure.GetProperty("annots").GetInt32() == 0 && !structure.GetProperty("marked").GetBoolean() && !structure.GetProperty("structparents").GetBoolean() &&
              !rootKeys.Contains("/Outlines") && !rootKeys.Contains("/StructTreeRoot") && !rootKeys.Contains("/AcroForm") && !rootKeys.Contains("/OpenAction") && !rootKeys.Contains("/PageLabels"),
              "Saved result has no annotations, marked content, tags, outline, form, label or action entries");
        await session.UndoAsync();
        Check(session.PageCount == 2 && (await PageText(session, 1)).Contains("DEST TWO"), "Undo removes all inserted pages in one step");
        await session.RedoAsync();
        Check(session.PageCount == 5, "Redo restores the imported pages");
        using (var reopened = await DocumentSession.OpenAsync(worker, NewStore(), saved)) Check(reopened.PageCount == 5, "Saved result reopens with 5 pages");

        // ---- options off, page order, subset
        using (var plain = await DocumentSession.OpenAsync(worker, NewStore(), dest))
        {
            var none = await plain.InsertPagesBestEffortAsync(location, rich, [2, 0], 2, new ImportOptions(KeepAnnotationsAsContent: false, KeepFormValues: false));
            Check(plain.PageCount == 4 && none.PagesInserted == 2, "Options-off import adds the 2 selected pages");
            Check((await PageText(plain, 2)).Contains("RICH THREE") && (await PageText(plain, 3)).Contains("RICH ONE"), "Selected pages keep the requested order");
            var white = await Pixel(plain, 3, 350, 350);
            Check(white.R > 240 && white.G > 240 && white.B > 240, "Annotation is not rendered when KeepAnnotationsAsContent is false");
            Check(none.Dropped["AnnotationsFlattened"] == 0 && none.Dropped["Annotations"] == 1, "Report counts the dropped (not flattened) annotation");
        }
        using (var form = await DocumentSession.OpenAsync(worker, NewStore(), dest))
        {
            var formReport = await form.InsertPagesBestEffortAsync(location, rich, [1], 0, new ImportOptions(KeepAnnotationsAsContent: false, KeepFormValues: false));
            Check(formReport.Dropped["FormFields"] == 1 && formReport.Dropped["FormFieldsFlattened"] == 0 && !(await PageText(form, 0)).Contains("FORMVALUE"),
                "Form values are dropped (and counted) when KeepFormValues is false");
        }

        // ---- encrypted source
        using (var secure = await DocumentSession.OpenAsync(worker, NewStore(), dest))
        {
            await Throws(() => secure.InsertPagesAsync(enc, [0], 0), "Strict insert refuses an encrypted source");
            await Throws(() => secure.InsertPagesBestEffortAsync(location, enc, [0], 0), "Best-effort refuses an encrypted source without its password");
            Check(secure.PageCount == 2, "Refusal leaves the document unchanged");
            await Throws(() => secure.InsertPagesBestEffortAsync(location, enc, [0], 0, new ImportOptions(SourcePassword: "wrong")), "A wrong source password is refused");
            var withPassword = await secure.InsertPagesBestEffortAsync(location, enc, [0], 0, new ImportOptions(SourcePassword: "pw"));
            Check(secure.PageCount == 3 && (await PageText(secure, 0)).Contains("SECRET ONE") && withPassword.Tier == "BestEffort", "Supplying the password imports the decrypted page content");
        }
        Check(Unchanged(), "Encrypted source file is unchanged");

        // ---- auto helper and failure behavior
        using (var auto = await DocumentSession.OpenAsync(worker, NewStore(), dest))
        {
            var easy = await auto.InsertPagesAutoAsync(location, F("c.pdf"), [0], 2);
            Check(easy.Tier == "Strict" && easy.Dropped.Count == 0 && auto.PageCount == 3, "Auto import uses the strict path when the source is simple");
            var hard = await auto.InsertPagesAutoAsync(location, rich, [0], 0);
            Check(hard.Tier == "BestEffort" && auto.PageCount == 4, "Auto import falls back to best-effort when the strict path refuses the source structure");
            var missing = new AdvancedEngineLocation(F("no-python.exe"), F("no-worker.py"));
            await Throws(() => auto.InsertPagesBestEffortAsync(missing, rich, [0], 0), "Missing companion engine is refused");
            await Throws(() => auto.InsertPagesBestEffortAsync(location, rich, [7], 0), "Out-of-range source page is refused by the engine");
            await Throws(() => auto.InsertPagesBestEffortAsync(location, rich, [0, 0], 0), "Repeated source pages are refused");
            Check(auto.PageCount == 4, "Failed imports leave the document unchanged");
        }

        // ---- combine with bookmarks
        var combined = F("combined.pdf");
        var inputs = new[] { new CombineInput(F("a.pdf")), new CombineInput(F("b.pdf"), "3,1"), new CombineInput(F("c.pdf"), "") };
        var result = await CombineWithBookmarks.RunAsync(location, worker, inputs, combined);
        Check(result.PageCount == 5 && File.Exists(combined), "Combine with bookmarks writes the expected 5 pages");
        Check(Unchanged(), "Combine leaves every input byte-identical");
        Check(Texts(combined).SequenceEqual(["A1", "A2", "B3", "B1", "C1"]), "Combined pages keep file order and selected page order (pypdf)");
        var outline = Verify("outline", combined).EnumerateArray().ToArray();
        string Title(JsonElement e) => e.GetProperty("title").GetString()!;
        int Page(JsonElement e) => e.GetProperty("page").GetInt32();
        JsonElement[] Kids(JsonElement e) => e.GetProperty("children").EnumerateArray().ToArray();
        Check(outline.Select(Title).SequenceEqual(["a", "b", "c"]) && outline.Select(Page).SequenceEqual([0, 2, 4]), "Each file gets a top-level bookmark named after it pointing at its first combined page");
        Check(Kids(outline[0]).Select(Title).SequenceEqual(["A-ch1", "A-ch2"]) && Kids(outline[0]).Select(Page).SequenceEqual([0, 1]), "First file's own bookmarks are nested with correct pages");
        var bKids = Kids(outline[1]);
        Check(bKids.Select(Title).SequenceEqual(["B-sec"]) && bKids.Select(Page).SequenceEqual([3]) && Kids(bKids[0]).Select(Title).SequenceEqual(["B-sub"]) &&
              Kids(bKids[0]).Select(Page).SequenceEqual([2]) && Kids(outline[2]).Length == 0,
            "Second file's nested bookmarks follow its page selection and the file without an outline has only its group bookmark");
        using (var opened = await DocumentSession.OpenAsync(worker, NewStore(), combined)) Check(opened.PageCount == 5, "PDFium opens the combined result with 5 pages");
        var combinedHash = RecoveryStore.Fingerprint(combined);
        await Throws(() => CombineWithBookmarks.RunAsync(location, worker, inputs, combined), "Existing combine output is refused");
        Check(RecoveryStore.Fingerprint(combined) == combinedHash && !Directory.GetFiles(work).Any(f => Path.GetFileName(f).StartsWith('.')), "Refusal leaves the existing output intact and no hidden candidate behind");
        var flat = F("combined-flat.pdf");
        await CombineWithBookmarks.RunAsync(location, worker, inputs, flat, groupByFile: false);
        var flatOutline = Verify("outline", flat).EnumerateArray().ToArray();
        Check(flatOutline.Select(Title).SequenceEqual(["A-ch1", "A-ch2", "B-sec"]) && flatOutline.Select(Page).SequenceEqual([0, 1, 3]) && Kids(flatOutline[2]).Select(Page).SequenceEqual([2]), "Without grouping, each file's bookmarks are merged flat with correct pages");
        var refused = F("refused.pdf");
        await Throws(() => CombineWithBookmarks.RunAsync(location, worker, [new CombineInput(F("a.pdf")), new CombineInput(enc)], refused), "Encrypted combine input is refused");
        await Throws(() => CombineWithBookmarks.RunAsync(location, worker, [new CombineInput(F("a.pdf"), "1,1"), new CombineInput(F("b.pdf"))], refused), "Repeated pages in a range are refused");
        await Throws(() => CombineWithBookmarks.RunAsync(location, worker, [new CombineInput(F("a.pdf")), new CombineInput(F("b.pdf"))], F("a.pdf")), "An input cannot be the output");
        Check(!File.Exists(refused) && Unchanged(), "Refused combines publish nothing and change no input");

        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} import-tier2 checks. Evidence: {work}");
    }
}

static class ImportAssert
{
    internal static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T e) { return e; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
