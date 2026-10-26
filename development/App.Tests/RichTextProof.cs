using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class RichTextProof
{
    const string FixtureScript = """
        import sys, pymupdf
        out, base = sys.argv[1], sys.argv[2]
        doc = pymupdf.open()
        for rot in (0, 90, 180, 270):
            page = doc.new_page(width=612, height=792); page.set_rotation(rot)
        page = doc.new_page(width=612, height=792)
        doc.xref_set_key(page.xref, "MediaBox", "[10 20 622 812]"); doc.xref_set_key(page.xref, "CropBox", "[60 70 560 700]")
        page = doc.new_page(width=612, height=792)
        a = page.add_freetext_annot(pymupdf.Rect(72, 100, 300, 160), "Foreign text", fontsize=10, fontname="cobo", text_color=(0, 0, 1))
        doc.xref_set_key(a.xref, "F", "128"); doc.xref_set_key(a.xref, "DA", "(0 0 1 rg /CoBo 10 Tf)")
        doc.save(out)
        b = pymupdf.open()
        for w, h in ((612, 792), (792, 612), (612, 792), (792, 612), (500, 630)):
            b.new_page(width=w, height=h)
        b.save(base)
        """;

    const string CheckScript = """
        import sys, json
        from pypdf import PdfReader
        path, page_index, spec = sys.argv[1], int(sys.argv[2]), json.loads(sys.argv[3])
        reader = PdfReader(path)
        problems = []
        page = reader.pages[page_index]
        found = [a.get_object() for a in page.get("/Annots", []) if a.get_object().get("/Subtype") == "/FreeText"]
        ann = None
        for a in found:
            if str(a.get("/Contents", "")).replace("\r", "\n") == spec["text"]:
                ann = a
        if ann is None:
            problems.append("no matching /FreeText")
        else:
            da = str(ann.get("/DA", ""))
            if f"/{spec['daFont']} {spec['size']:g} Tf" not in da: problems.append("DA font/size: " + da)
            r, g, b = (c / 255 for c in spec["color"])
            if not all(abs(float(x) - y) < 0.01 for x, y in zip(da.split(" rg")[0].split()[-3:], (r, g, b))): problems.append("DA color: " + da)
            if not int(ann.get("/F", 0)) & 4: problems.append("print flag missing")
            rect = [float(x) for x in ann["/Rect"]]
            if any(abs(x - y) > 0.06 for x, y in zip(rect, spec["rect"])): problems.append("rect " + str(rect))
            ap = ann.get("/AP", {}).get("/N")
            if ap is None: problems.append("no /AP /N")
            else:
                data = ap.get_object().get_data()
                if data.count(b" Tj") < spec["minLines"]: problems.append("AP lines %d" % data.count(b" Tj"))
        print(json.dumps({"ok": not problems, "problems": problems, "freetextCount": len(found)}))
        """;

    const string TextScript = """
        import sys, json
        from pypdf import PdfReader
        path, page_index, expected = sys.argv[1], int(sys.argv[2]), json.loads(sys.argv[3])
        reader = PdfReader(path)
        page = reader.pages[page_index]
        text = page.extract_text()
        fonts = page["/Resources"]["/Font"]
        embedded, tounicode, subtypes = False, False, []
        for key, ref in fonts.items():
            font = ref.get_object()
            if font.get("/Subtype") != "/Type0": continue
            tounicode = tounicode or "/ToUnicode" in font
            descriptor = font["/DescendantFonts"][0].get_object()["/FontDescriptor"].get_object()
            if "/FontFile2" in descriptor or "/FontFile3" in descriptor: embedded = True
            subtypes.append(str(font.get("/BaseFont")))
        print(json.dumps({"contains": [line in text for line in expected], "embedded": embedded, "toUnicode": tounicode, "fonts": subtypes}))
        """;

    const string FsTypeScript = """
        import sys, json, struct
        out = []
        for path in json.loads(sys.argv[1]):
            try:
                data = open(path, "rb").read()
                bases = [0]
                if data[:4] == b"ttcf":
                    bases = [struct.unpack_from(">I", data, 12 + 4 * i)[0] for i in range(struct.unpack_from(">I", data, 8)[0])]
                for index, base in enumerate(bases):
                    count = struct.unpack_from(">H", data, base + 4)[0]
                    tables = {}
                    for i in range(count):
                        tag, _, off, length = struct.unpack_from(">4sIII", data, base + 12 + 16 * i)
                        tables[tag] = (off, length)
                    fs = struct.unpack_from(">H", data, tables[b"OS/2"][0] + 8)[0] if b"OS/2" in tables else 0
                    outlines = (b"glyf" in tables and b"loca" in tables) or b"CFF " in tables
                    restricted = bool(fs & 2) and not (fs & 0xC)
                    out.append([path, index, fs, outlines and not restricted and not (fs & 0x200)])
            except Exception:
                pass
        print(json.dumps(out))
        """;

    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "rich-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var helper = Path.Combine(root, "App.Advanced", "worker.py");
        var location = new AdvancedEngineLocation(python, helper);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var checks = new List<string>();
        var notes = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception(message + " (no refusal)");
        }
        string Py(string script, params string[] args)
        {
            var file = Path.Combine(work, "script-" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(file, script);
            var start = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8 };
            start.ArgumentList.Add("-I"); start.ArgumentList.Add("-B"); start.ArgumentList.Add(file);
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Python helper failed: " + stderr);
            return stdout;
        }

        var input = Path.Combine(work, "rich-source.pdf");
        var baseline = Path.Combine(work, "baseline.pdf");
        Py(FixtureScript, input, baseline);
        var inputHash = RecoveryStore.Fingerprint(input);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, input);
        var baseSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "baseline-recovery")), baseline);
        Check(session.PageCount == 6, "Fixture has rotated, offset-CropBox and foreign-annotation pages");

        // ---- add on /Rotate 0, 90, 180, 270 and an offset CropBox page
        const string text = "Hello caf\u00e9 world\nSecond line that wraps because it is long enough to need several lines in this box";
        var rect = (L: 100.0, B: 400.0, R: 300.0, T: 560.0);
        var pageGeometry = new (double W, double H, int Rotation, double X0, double Y0, double X1, double Y1)[]
        {
            (612, 792, 0, 0, 0, 612, 792), (612, 792, 90, 0, 0, 612, 792), (612, 792, 180, 0, 0, 612, 792),
            (612, 792, 270, 0, 0, 612, 792), (500, 630, 0, 60, 70, 560, 700)
        };
        (double dl, double dt, double dr, double db) Display(int page, (double L, double B, double R, double T) r)
        {
            var g = pageGeometry[page];
            (double x, double y) Map(double x, double y) => g.Rotation switch
            {
                0 => (x - g.X0, g.Y1 - y), 90 => (y - g.Y0, x - g.X0), 180 => (g.X1 - x, y - g.Y0), _ => (g.Y1 - y, g.X1 - x)
            };
            var a = Map(r.L, r.B); var b = Map(r.R, r.T);
            return (Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Max(a.x, b.x), Math.Max(a.y, b.y));
        }
        (int W, int H) DisplaySize(int page) => g(page);
        (int, int) g(int page) { var p = pageGeometry[page]; return p.Rotation is 90 or 270 ? ((int)p.H, (int)p.W) : ((int)p.W, (int)p.H); }
        var options = new FreeTextOptions
        {
            Page = 0, Left = rect.L, Bottom = rect.B, Right = rect.R, Top = rect.T, Text = text, Font = "Times", Bold = true, FontSize = 14,
            Color = new FreeTextColor(200, 0, 0), Align = "Center", BorderWidth = 1.5, BorderColor = new FreeTextColor(0, 0, 0),
            FillColor = new FreeTextColor(255, 255, 200)
        };
        var ids = new long[5];
        for (var page = 0; page < 5; page++)
        {
            var result = await session.AddFreeTextAsync(location, options with { Page = page }, session.Revision);
            ids[page] = result.Id;
            Check(result.Id > 0 && result.Page == page && !result.Grown && Math.Abs(result.Left - rect.L) < 0.05 && Math.Abs(result.Top - rect.T) < 0.05,
                $"AddFreeText on page {page} ({pageGeometry[page].Rotation} deg{(page == 4 ? ", offset CropBox" : "")}) returns Id and the requested box");
            var baseResult = await baseSession.AddFreeTextAsync(location, MirrorToBaseline(options, Display(page, rect), DisplaySize(page).H, page), baseSession.Revision);
            Check(baseResult.Id > 0, $"Upright baseline box added for page {page}");
        }
        static FreeTextOptions MirrorToBaseline(FreeTextOptions o, (double dl, double dt, double dr, double db) d, int displayHeight, int page) =>
            o with { Page = page, Left = d.dl, Right = d.dr, Bottom = displayHeight - d.db, Top = displayHeight - d.dt };
        Check(RecoveryStore.Fingerprint(input) == inputHash, "Source PDF unchanged after adding text boxes");

        // ---- independent pypdf structure check (saved copy)
        var saved = Path.Combine(work, "saved-text-boxes.pdf");
        await session.SaveAsync(saved);
        var spec = (string)JsonSerializer.Serialize(new { text, daFont = "TiBo", size = 14.0, color = new[] { 200, 0, 0 }, rect = new[] { rect.L, rect.B, rect.R, rect.T }, minLines = 3 });
        for (var page = 0; page < 5; page++)
        {
            var check = JsonDocument.Parse(Py(CheckScript, saved, page.ToString(), spec)).RootElement;
            Check(check.GetProperty("ok").GetBoolean(),
                $"pypdf confirms /FreeText, /DA font+size+color, /Contents, /AP /N, print flag, rect on page {page}" +
                (check.GetProperty("ok").GetBoolean() ? "" : ": " + check.GetProperty("problems")));
        }

        // ---- PDFium rendering: region, and upright against the baseline
        foreach (var page in Enumerable.Range(0, 5))
        {
            var (w, h) = DisplaySize(page);
            var shot = await Render(session, page, w * 2, h * 2);
            var bbox = BoundingBox(shot, w * 2, h * 2);
            var d = Display(page, rect);
            Check(bbox is { } b && Math.Abs(b.x0 - d.dl * 2) <= 4 && Math.Abs(b.y0 - d.dt * 2) <= 4 && Math.Abs(b.x1 - d.dr * 2) <= 4 && Math.Abs(b.y1 - d.db * 2) <= 4,
                $"PDFium draws page {page}'s box inside the expected displayed region and nowhere else");
            var reference = await Render(baseSession, page, w * 2, h * 2);
            var differing = 0; var inked = 0;
            for (var i = 0; i < shot.Length; i += 4)
            {
                if (reference[i] < 250 || reference[i + 1] < 250 || reference[i + 2] < 250) inked++;
                if (Math.Abs(shot[i] - reference[i]) > 80 || Math.Abs(shot[i + 1] - reference[i + 1]) > 80 || Math.Abs(shot[i + 2] - reference[i + 2]) > 80) differing++;
            }
            Check(inked > 1000 && differing * 100 < inked * 3,
                $"Page {page} text is upright to the reader (PDFium render matches an unrotated box; {differing} of {inked} px differ)");
        }

        // ---- list
        var listed = await session.ListFreeTextAsync(location, null);
        Check(listed.Count == 6, "ListFreeText lists all pages including the foreign annotation");
        var first = listed.Single(i => i.Id == ids[0]);
        Check(first.Page == 0 && first.Font == "Times" && first.Bold && !first.Italic && first.FontSize == 14 && first.Red == 200 && first.Green == 0 &&
              first.Blue == 0 && first.Align == "Center" && !first.Locked && first.Text == text && first.BorderWidth == 1.5 &&
              first.FillColor == new FreeTextColor(255, 255, 200) && Math.Abs(first.Left - rect.L) < 0.05,
            "ListFreeText returns text, font, size, color, alignment, border, fill and rectangle");
        var onePage = await session.ListFreeTextAsync(location, 2);
        Check(onePage.Count == 1 && onePage[0].Id == ids[2], "ListFreeText filters by page");
        var foreign = listed.Single(i => i.Page == 5);
        Check(foreign.Locked && foreign.Font == "Courier" && foreign.Bold && foreign.FontSize == 10 && foreign.Blue == 255 && foreign.Text == "Foreign text",
            "A FreeText made by another app is listed, /DA parsed, and its lock reported");
        var lockedRevision = session.Revision;
        await Rejected<InvalidOperationException>(() => session.UpdateFreeTextAsync(location, foreign, new FreeTextOptions { Text = "x" }, session.Revision), "Locked annotations cannot be updated");
        await Rejected<InvalidOperationException>(() => session.DeleteFreeTextAsync(location, foreign, session.Revision), "Locked annotations cannot be deleted");
        await Rejected<InvalidOperationException>(() => new AdvancedEngineClient(python, helper).RunAsync(new
        {
            Operation = "DeleteFreeText", Input = AdvancedEngineClient.FilePath(input), Output = AdvancedEngineClient.FilePath(Path.Combine(work, "locked-out.pdf")),
            Id = foreign.Id, Page = 5, ExpectedText = foreign.Text, ExpectedLeft = foreign.Left, ExpectedBottom = foreign.Bottom, ExpectedRight = foreign.Right, ExpectedTop = foreign.Top
        }), "The engine itself refuses a locked annotation");

        // ---- update text/size/color and move/resize, undo/redo
        var changed = await session.UpdateFreeTextAsync(location, first, new FreeTextOptions
        {
            Text = "Updated text", FontSize = 20, Color = new FreeTextColor(0, 0, 255), Left = 120, Bottom = 450, Right = 320, Top = 520, Align = "Right"
        }, session.Revision);
        Check(changed.Id == first.Id && Math.Abs(changed.Left - 120) < 0.05 && Math.Abs(changed.Top - 520) < 0.05, "UpdateFreeText moves and resizes the box");
        var after = (await session.ListFreeTextAsync(location, 0)).Single();
        Check(after.Text == "Updated text" && after.FontSize == 20 && after.Blue == 255 && after.Red == 0 && after.Align == "Right" && after.Font == "Times" && after.Bold &&
              after.FillColor == first.FillColor, "Update changes text, size, color and alignment and keeps unchanged style");
        var updatedShot = await Render(session, 0, 1224, 1584);
        var updatedBox = BoundingBox(updatedShot, 1224, 1584);
        Check(updatedBox is { } ub && Math.Abs(ub.x0 - 240) <= 4 && Math.Abs(ub.x1 - 640) <= 4, "The regenerated appearance renders in the new position");
        var spec2 = JsonSerializer.Serialize(new { text = "Updated text", daFont = "TiBo", size = 20.0, color = new[] { 0, 0, 255 }, rect = new[] { 120.0, 450, 320, 520 }, minLines = 1 });
        var saved2 = Path.Combine(work, "saved-updated.pdf");
        await session.SaveAsync(saved2);
        Check(JsonDocument.Parse(Py(CheckScript, saved2, "0", spec2)).RootElement.GetProperty("ok").GetBoolean(), "pypdf confirms the updated annotation");
        await session.UndoAsync();
        Check((await session.ListFreeTextAsync(location, 0)).Single().Text == text, "Undo restores the previous text box");
        await session.RedoAsync();
        Check((await session.ListFreeTextAsync(location, 0)).Single().Text == "Updated text", "Redo reapplies the update");

        // ---- stale guards and refusals
        var revision = session.Revision;
        await Rejected<InvalidOperationException>(() => session.UpdateFreeTextAsync(location, first, new FreeTextOptions { Text = "stale" }, session.Revision), "Stale expected text/rectangle is refused");
        await Rejected<InvalidOperationException>(() => session.DeleteFreeTextAsync(location, first with { Text = "wrong" }, session.Revision), "Stale text guard refuses delete");
        await Rejected<InvalidOperationException>(() => session.DeleteFreeTextAsync(location, after with { Left = after.Left + 20 }, session.Revision), "Stale rectangle guard refuses delete");
        await Rejected<InvalidOperationException>(() => session.UpdateFreeTextAsync(location, after, new FreeTextOptions { Text = "late" }, revision - 1), "Stale document revision is refused");
        await Rejected<InvalidOperationException>(() => session.AddFreeTextAsync(location, options with
        { Page = 0, Left = 100, Right = 160, Bottom = 40, Top = 90, Text = string.Join(' ', Enumerable.Repeat("word", 700)), Align = "Left" }, session.Revision), "Text that cannot fit the box or the page is refused, never clipped");
        await Rejected<InvalidOperationException>(() => session.AddFreeTextAsync(location, options with { Page = 0, Text = "\u4e2d\u6587" }, session.Revision), "A text box refuses characters outside WinAnsi");
        await Rejected<InvalidOperationException>(() => session.AddFreeTextAsync(location, options with { Page = 0, Left = 600, Right = 700 }, session.Revision), "A box outside the visible page is refused");
        await Rejected<ArgumentException>(() => session.AddFreeTextAsync(location, options with { Font = "Comic" }, session.Revision), "Unknown fonts are refused");
        await Rejected<ArgumentException>(() => session.AddFreeTextAsync(location, options with { FontSize = 500 }, session.Revision), "Out-of-range font size is refused");
        Check(session.Revision == revision, "Refusals leave the session revision unchanged");

        // ---- grow downward
        var grow = await session.AddFreeTextAsync(location, options with
        { Page = 0, Left = 100, Right = 300, Top = 320, Bottom = 300, Text = string.Join(' ', Enumerable.Repeat("grow", 40)), FillColor = null, BorderWidth = 0, BorderColor = null }, session.Revision);
        Check(grow.Grown && grow.Bottom < 300 - 20 && Math.Abs(grow.Top - 320) < 0.05, "A too-short box grows downward within the page");
        var growSpec = JsonSerializer.Serialize(new { text = string.Join(' ', Enumerable.Repeat("grow", 40)), daFont = "TiBo", size = 14.0, color = new[] { 200, 0, 0 }, rect = new[] { grow.Left, grow.Bottom, grow.Right, grow.Top }, minLines = 5 });
        var savedGrow = Path.Combine(work, "saved-grown.pdf");
        await session.SaveAsync(savedGrow);
        Check(JsonDocument.Parse(Py(CheckScript, savedGrow, "0", growSpec)).RootElement.GetProperty("ok").GetBoolean(), "pypdf confirms the grown, wrapped multi-line box");

        // ---- delete, undo, save/reopen
        var beforeDelete = (await session.ListFreeTextAsync(location, null)).Count;
        var victim = (await session.ListFreeTextAsync(location, 4)).Single();
        await session.DeleteFreeTextAsync(location, victim, session.Revision);
        Check((await session.ListFreeTextAsync(location, null)).Count == beforeDelete - 1 && (await session.ListFreeTextAsync(location, 4)).Count == 0, "DeleteFreeText removes only the selected box");
        var deletedShot = BoundingBox(await Render(session, 4, 1000, 1260), 1000, 1260);
        Check(deletedShot is null, "PDFium no longer draws the deleted box");
        await session.UndoAsync();
        Check((await session.ListFreeTextAsync(location, 4)).Count == 1, "Undo restores the deleted box");
        await session.RedoAsync();
        await session.UndoAsync();
        var finalSave = Path.Combine(work, "final.pdf");
        await session.SaveAsync(finalSave);
        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopen-recovery")), finalSave))
        {
            var again = await reopened.ListFreeTextAsync(location, null);
            Check(again.Count == beforeDelete && again.Any(i => i.Text == "Updated text" && i.Page == 0), "Saved PDF reopens with the same editable text boxes");
            var reopenedBox = BoundingBox(await Render(reopened, 1, 1584, 1224), 1584, 1224);
            Check(reopenedBox is not null, "Saved PDF renders its text boxes through PDFium");
        }

        // ---- font catalog
        FontCatalog.Refresh();
        var watch = Stopwatch.StartNew();
        var fonts = FontCatalog.List();
        watch.Stop();
        notes.Add($"FontCatalog: {fonts.Count} faces in {watch.ElapsedMilliseconds} ms (cold)");
        Check(fonts.Count > 0 && fonts.Any(f => f.Embeddable) && watch.Elapsed < TimeSpan.FromSeconds(5), "FontCatalog lists installed faces, at least one embeddable, within seconds" + (fonts.Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(5) ? "" : $" ({fonts.Count} faces, {watch.ElapsedMilliseconds} ms)"));
        var cached = Stopwatch.StartNew(); FontCatalog.List(); cached.Stop();
        Check(ReferenceEquals(FontCatalog.List(), fonts) && cached.ElapsedMilliseconds < 50, "FontCatalog caches its result");
        var files = fonts.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var truth = JsonDocument.Parse(Py(FsTypeScript, JsonSerializer.Serialize(files))).RootElement.EnumerateArray()
            .ToDictionary(e => (e[0].GetString()!.ToLowerInvariant(), e[1].GetInt32()), e => (FsType: e[2].GetInt32(), Embeddable: e[3].GetBoolean()));
        var mismatches = fonts.Count(f => !truth.TryGetValue((f.Path.ToLowerInvariant(), f.FaceIndex), out var t) || t.Embeddable != f.Embeddable);
        var distinctFs = truth.Values.Select(v => v.FsType).Distinct().OrderBy(v => v).ToArray();
        notes.Add($"fsType values among installed fonts: {string.Join(",", distinctFs.Select(v => "0x" + v.ToString("X")))}; restricted/bitmap faces: {fonts.Count(f => !f.Embeddable)}");
        Check(mismatches == 0, $"Embeddable flags match an independent fsType parse for all {fonts.Count} faces");
        Check(fonts.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1 && group.Select(f => f.FaceIndex).Distinct().Count() == group.Count()) ||
              !files.Any(path => path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)), "TrueType collections yield one entry per face");
        var junk = Path.Combine(work, "junk.ttf");
        File.WriteAllBytes(junk, new byte[200]);
        var truncated = Path.Combine(work, "truncated.ttf");
        var arialPath = fonts.FirstOrDefault(f => f.Family == "Arial" && f.Style == "Regular")?.Path;
        var segoePath = fonts.FirstOrDefault(f => f.Family == "Segoe UI" && f.Style == "Regular")?.Path;
        var chosen = fonts.FirstOrDefault(f => f.Embeddable && f.Family == "Arial" && f.Style == "Regular") ??
                     fonts.FirstOrDefault(f => f.Embeddable && f.Family == "Segoe UI" && f.Style == "Regular") ??
                     throw new Exception("Neither Arial nor Segoe UI is installed for the Unicode check");
        File.WriteAllBytes(truncated, File.ReadAllBytes(chosen.Path).Take(300).ToArray());
        var skipped = 0;
        foreach (var bad in new[] { junk, truncated })
            try { skipped += FontCatalog.InspectFile(bad).Count == 0 ? 1 : 0; } catch (Exception) { skipped++; }
        Check(skipped == 2, "Malformed fonts are skipped by inspection rather than listed");

        // ---- Unicode text
        const string unicode = "\u0391\u03b2\u03b3\u03b4 \u0395\u03bb\u03bb\u03b7\u03bd\u03b9\u03ba\u03ac\n\u0416\u0443\u043a \u041f\u0440\u0438\u0432\u0435\u0442 Hello";
        var expected = unicode.Split('\n');
        var inputSize = new FileInfo(input).Length;
        var unicodeRevision = session.Revision;
        var sizeBefore = (await SavedSize(session, work, "before-unicode"));
        var u0 = await session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 72, 300, unicode, chosen.Path, chosen.FaceIndex, 20, new FreeTextColor(10, 20, 30)), session.Revision);
        var u1 = await session.AddUnicodeTextAsync(location, new UnicodeTextOptions(1, 72, 300, unicode, chosen.Path, chosen.FaceIndex, 20), session.Revision);
        var sizeAfter = await SavedSize(session, work, "after-unicode");
        notes.Add($"Unicode with {chosen.Family} {chosen.Style}: font file {u0.FontFileBytes} bytes, embedded {u0.FontBytes} bytes (subset={u0.Subset}, {u0.GlyphsUsed} glyphs); PDF grew {sizeAfter - sizeBefore} bytes for two additions");
        Check(u0.Subset && u0.FontBytes < u0.FontFileBytes / 4 && sizeAfter - sizeBefore < u0.FontFileBytes / 2, "Pure-Python TrueType subsetting works and keeps growth small");
        var unicodePdf = Path.Combine(work, "unicode.pdf");
        await session.SaveAsync(unicodePdf);
        foreach (var page in new[] { 0, 1 })
        {
            var verdict = JsonDocument.Parse(Py(TextScript, unicodePdf, page.ToString(), JsonSerializer.Serialize(expected))).RootElement;
            Check(verdict.GetProperty("contains").EnumerateArray().All(v => v.GetBoolean()), $"pypdf extract_text returns the exact Greek, Cyrillic and Latin lines on page {page}");
            Check(verdict.GetProperty("embedded").GetBoolean() && verdict.GetProperty("toUnicode").GetBoolean(), $"The Unicode font on page {page} is embedded (FontFile2/3) with a ToUnicode map");
        }
        foreach (var page in new[] { 0, 1 })
        {
            var (w, h) = DisplaySize(page);
            var box = BoundingBox(await Render(session, page, w * 2, h * 2), w * 2, h * 2, ignoreFill: false);
            var origin = Display(page, (72, 300, 72, 300));
            Check(box is { } bb && bb.x0 >= origin.dl * 2 - 4 && bb.x0 <= origin.dl * 2 + 10, $"PDFium renders the Unicode text on page {page} at its baseline origin");
        }
        await Rejected<InvalidOperationException>(() => session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 72, 300, "\u4e2d\u6587 a", chosen.Path, chosen.FaceIndex, 20), session.Revision), "A character missing from the font is refused");
        var copy = Path.Combine(work, "arial-copy.ttf");
        File.Copy(chosen.Path, copy);
        await Rejected<ArgumentException>(() => session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 72, 300, "a", copy, 0, 20), session.Revision), "A font path outside the font catalog is refused");
        await Rejected<ArgumentException>(() => session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 72, 300, "a", @"C:\Windows\System32\notepad.exe", 0, 20), session.Revision), "A non-font path is refused");
        await Rejected<InvalidOperationException>(() => new AdvancedEngineClient(python, helper).RunAsync(new
        {
            Operation = "AddUnicodeText", Input = AdvancedEngineClient.FilePath(input), Output = AdvancedEngineClient.FilePath(Path.Combine(work, "bad-font.pdf")),
            Page = 0, X = 72, Y = 300, Text = "a", FontFile = copy, FaceIndex = 0, FontSize = 20
        }), "The engine re-validates the font location and refuses a path outside the Windows font folders");
        await Rejected<InvalidOperationException>(() => session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 590, 300, "A long line that runs off the page edge", chosen.Path, 0, 40), session.Revision), "Text that would run off the page is refused");
        var restricted = fonts.FirstOrDefault(f => !f.Embeddable);
        if (restricted is not null)
            await Rejected<InvalidOperationException>(() => session.AddUnicodeTextAsync(location, new UnicodeTextOptions(0, 72, 300, "a", restricted.Path, restricted.FaceIndex, 20), session.Revision), "A non-embeddable face is refused");
        else notes.Add("No non-embeddable face installed; refusal path covered by flag comparison only");
        foreach (var (value, wantEmbeddable) in new[] { (0x0000, true), (0x0002, false), (0x0200, false), (0x0202, false), (0x0004, true), (0x0008, true), (0x000A, true), (0x0100, true) })
        {
            var patched = Path.Combine(work, $"fstype-{value:X4}.ttf");
            File.WriteAllBytes(patched, PatchFsType(File.ReadAllBytes(chosen.Path), value));
            var face = FontCatalog.InspectFile(patched).Single();
            Check(face.Font.Embeddable == wantEmbeddable && face.FsType == value, $"fsType 0x{value:X4} is read and marked {(wantEmbeddable ? "embeddable" : "non-embeddable")}");
        }

        // ---- undo covers Unicode text
        await session.UndoAsync();
        Check((await session.ListFreeTextAsync(location, null)).Count == beforeDelete, "Unicode text additions are undoable checkpoints");
        Check(RecoveryStore.Fingerprint(input) == inputHash, "Source PDF unchanged at the end");

        session.Dispose(); baseSession.Dispose();
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks, Notes = notes }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var note in notes) Console.WriteLine("NOTE " + note);
        Console.WriteLine($"PASS {checks.Count} rich-text checks. Evidence: {work}");
    }

    static byte[] PatchFsType(byte[] font, int fsType)
    {
        var copy = (byte[])font.Clone();
        var count = (copy[4] << 8) | copy[5];
        for (var i = 0; i < count; i++)
        {
            var entry = 12 + 16 * i;
            if (System.Text.Encoding.Latin1.GetString(copy, entry, 4) != "OS/2") continue;
            var offset = (int)(((uint)copy[entry + 8] << 24) | ((uint)copy[entry + 9] << 16) | ((uint)copy[entry + 10] << 8) | copy[entry + 11]);
            copy[offset + 8] = (byte)(fsType >> 8); copy[offset + 9] = (byte)fsType;
            return copy;
        }
        throw new Exception("The test font has no OS/2 table");
    }

    static async Task<long> SavedSize(DocumentSession session, string work, string name)
    {
        var path = Path.Combine(work, name + ".pdf");
        await session.SaveAsync(path);
        return new FileInfo(path).Length;
    }

    static async Task<byte[]> Render(DocumentSession session, int page, int width, int height) =>
        Convert.FromBase64String((await session.RenderAsync(page, width, height)).GetProperty("Bgra").GetString()!);

    static (int x0, int y0, int x1, int y1)? BoundingBox(byte[] bgra, int width, int height, bool ignoreFill = false)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = 4 * (y * width + x);
                if (bgra[i] >= 250 && bgra[i + 1] >= 250 && bgra[i + 2] >= 250) continue;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x + 1); y1 = Math.Max(y1, y + 1);
            }
        return x1 < 0 ? null : (x0, y0, x1, y1);
    }
}
