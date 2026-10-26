using System.Runtime.InteropServices;
using System.Text.Json;
using HunterPdf.Core;

/// <summary>Annotations, drawings, images and text boxes on pages with /Rotate 90/180/270 and on pages whose visible box does not start at (0,0).
/// View points are mapped to default user space with PDFium's own FPDF_DeviceToPage, so the expected displayed location is independent of the engine.</summary>
static class RotatedPagesProof
{
    const int Scale = 2;
    sealed record Fixture(string Name, int Rotate, bool Crop);
    static readonly Fixture[] Fixtures =
    [
        new("rot0", 0, false), new("rot90", 90, false), new("rot180", 180, false), new("rot270", 270, false),
        new("crop0", 0, true), new("crop90", 90, true), new("crop180", 180, true), new("crop270", 270, true)
    ];

    public static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "rotated-pages");
        Directory.CreateDirectory(work);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected(Func<Task> action, string message)
        {
            try { await action(); }
            catch (InvalidOperationException) { checks.Add(message); return; }
            throw new Exception(message);
        }

        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var packages = Path.Combine(root, "tools", "python-packages");
        var nativePath = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "pdfium.dll");
        try { NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (_, _, _) => NativeLibrary.Load(nativePath)); }
        catch (InvalidOperationException) { /* another proof in this process already installed the resolver */ }

        var fixtureScript = Path.Combine(work, "make_fixtures.py");
        File.WriteAllText(fixtureScript, """
            import sys
            sys.path.insert(0,sys.argv[3])
            from pypdf import PdfWriter
            from pypdf.generic import RectangleObject
            out=sys.argv[1]
            for name,rot,crop in [('rot0',0,0),('rot90',90,0),('rot180',180,0),('rot270',270,0),('crop0',0,1),('crop90',90,1),('crop180',180,1),('crop270',270,1)]:
                w=PdfWriter(); p=w.add_blank_page(612,792)
                if crop: p.cropbox=RectangleObject((100,200,500,600))
                if rot: p.rotate(rot)
                with open(out+'/'+name+'.pdf','wb') as f: w.write(f)
            """);
        RunPython(python, "fixture generation", fixtureScript, work, "", packages);

        var checkScript = Path.Combine(work, "check_annotations.py");
        File.WriteAllText(checkScript, """
            import sys, json
            sys.path.insert(0,sys.argv[3])
            from pypdf import PdfReader
            from pypdf.generic import ContentStream
            spec=json.load(open(sys.argv[2]))
            page=PdfReader(sys.argv[1],strict=True).pages[0]
            annots=[a.get_object() for a in page.get('/Annots',[])]
            assert len(annots)==len(spec['Annotations']),(len(annots),len(spec['Annotations']))
            def near(a,b,t=0.06): return all(abs(float(x)-float(y))<=t for x,y in zip(a,b))
            for annot,exp in zip(annots,spec['Annotations']):
                assert annot['/Subtype']==exp['Subtype'],(annot['/Subtype'],exp['Subtype'])
                rect=[float(v) for v in annot['/Rect']]
                if exp['Exact']: assert near(rect,exp['Rect']),(exp['Subtype'],rect,exp['Rect'])
                else:
                    l,b,r,t=rect
                    for x,y in exp['Points']: assert l-0.01<=x<=r+0.01 and b-0.01<=y<=t+0.01,(exp['Subtype'],rect,x,y)
                    for stroke in annot['/InkList']:
                        v=[float(q) for q in stroke]
                        for x,y in zip(v[::2],v[1::2]): assert l-0.01<=x<=r+0.01 and b-0.01<=y<=t+0.01,(rect,x,y)
                assert int(annot.get('/F',0))&4
                if exp['Subtype']=='/Highlight':
                    q=[float(v) for v in annot['/QuadPoints']]
                    xs=q[0::2]; ys=q[1::2]
                    assert near([min(xs),min(ys),max(xs),max(ys)],exp['Rect']),(q,exp['Rect'])
            # image placement: the cm in effect for the image Do must equal the matrix derived from the displayed corners
            content=ContentStream(page.get_contents(),page.pdf)
            stack=[]; ctm=[1,0,0,1,0,0]; images=[]
            def mul(m,n): return [m[0]*n[0]+m[1]*n[2],m[0]*n[1]+m[1]*n[3],m[2]*n[0]+m[3]*n[2],m[2]*n[1]+m[3]*n[3],m[4]*n[0]+m[5]*n[2]+n[4],m[4]*n[1]+m[5]*n[3]+n[5]]
            for operands,op in content.operations:
                if op==b'q': stack.append(list(ctm))
                elif op==b'Q': ctm=stack.pop()
                elif op==b'cm': ctm=mul([float(x) for x in operands],ctm)
                elif op==b'Do': images.append(list(ctm))
            assert len(images)==1,len(images)
            assert near(images[0],spec['ImageMatrix']),(images[0],spec['ImageMatrix'])
            # the text box line is real page text inside the border
            hits=[]
            def visit(text,cm,tm,font,size):
                if 'Rotated text' in text: hits.append((tm[4]*cm[0]+tm[5]*cm[2]+cm[4],tm[4]*cm[1]+tm[5]*cm[3]+cm[5]))
            page.extract_text(visitor_text=visit)
            assert hits,'text box text missing'
            l,b,r,t=spec['TextBoxRect']
            x,y=hits[0]
            assert l<=x<=r and b<=y<=t,(x,y,spec['TextBoxRect'])
            """);

        var sourceHashes = Fixtures.ToDictionary(f => f.Name, f => RecoveryStore.Fingerprint(Path.Combine(work, f.Name + ".pdf")));
        var notePatches = new Dictionary<string, byte[]>();
        var imagePatches = new Dictionary<string, byte[]>();
        var noteUpright = new Dictionary<string, bool>();

        foreach (var fixture in Fixtures)
        {
            var path = Path.Combine(work, fixture.Name + ".pdf");
            var geometry = ReadGeometry(path);
            var dw = geometry.Width; var dh = geometry.Height;
            Check(geometry.Rotation == fixture.Rotate / 90, $"{fixture.Name}: PDFium reports page rotation {fixture.Rotate}");
            Check(fixture.Crop ? geometry.Left == 100 && geometry.Bottom == 200 : geometry.Left == 0 && geometry.Bottom == 0, $"{fixture.Name}: visible box origin is as generated");
            Check(dw == (fixture.Rotate % 180 == 0 ? (fixture.Crop ? 400 : 612) : (fixture.Crop ? 400 : 792)), $"{fixture.Name}: displayed width follows rotation");

            (double X, double Y) U(double x, double y) => ToUser(path, dw, dh, x, y);
            (double L, double B, double R, double T) UserRect(double x0, double y0, double x1, double y1)
            {
                var a = U(x0, y0); var b = U(x1, y1);
                return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            }

            var recovery = new RecoveryStore(Path.Combine(work, fixture.Name + "-recovery"));
            using var session = await DocumentSession.OpenAsync(worker, recovery, path);
            var width = dw * Scale; var height = dh * Scale;
            async Task<byte[]> Render() => Convert.FromBase64String((await session.RenderAsync(0, width, height)).GetProperty("Bgra").GetString()!);
            var blank = await Render();
            Check(blank.All((b) => b == 255), $"{fixture.Name}: rendering the blank page is white at the displayed size");
            var spec = new List<object>();
            var checkpoints = 0;
            var previous = blank;

            // Verifies the new pixels' bounding box sits where the dragged view rectangle was.
            async Task<byte[]> Changed(string what, double x0, double y0, double x1, double y1, double tolerance, bool exact = true)
            {
                var now = await Render();
                var box = DiffBox(previous, now, width, height);
                Check(box is not null, $"{fixture.Name}: {what} changes the rendered page");
                var (l, t, r, b) = box!.Value;
                double el = x0 * Scale, et = y0 * Scale, er = x1 * Scale, eb = y1 * Scale;
                var ok = exact
                    ? Math.Abs(l - el) <= tolerance && Math.Abs(t - et) <= tolerance && Math.Abs(r - er) <= tolerance && Math.Abs(b - eb) <= tolerance
                    : l >= el - tolerance && t >= et - tolerance && r <= er + tolerance && b <= eb + tolerance;
                Check(ok, $"{fixture.Name}: {what} appears at the dragged displayed location (pixel box {l},{t},{r},{b} vs expected {el},{et},{er},{eb})");
                previous = now;
                checkpoints++;
                return now;
            }

            // Rectangle
            var rect = UserRect(60, 50, 180, 120);
            await session.AddDrawingAsync(0, "Rectangle", rect.L, rect.B, rect.R, rect.T);
            spec.Add(new { Subtype = "/Square", Exact = true, Rect = new[] { rect.L, rect.B, rect.R, rect.T }, Points = Array.Empty<double[]>() });
            await Changed("rectangle", 60, 50, 180, 120, 6);
            // Ellipse
            var ellipse = UserRect(200, 60, 330, 140);
            await session.AddDrawingAsync(0, "Ellipse", ellipse.L, ellipse.B, ellipse.R, ellipse.T);
            spec.Add(new { Subtype = "/Circle", Exact = true, Rect = new[] { ellipse.L, ellipse.B, ellipse.R, ellipse.T }, Points = Array.Empty<double[]>() });
            await Changed("ellipse", 200, 60, 330, 140, 6);
            // Pen ink
            var penView = new (double X, double Y)[] { (60, 160), (90, 190), (120, 160), (150, 190), (180, 160) };
            var pen = penView.Select(p => U(p.X, p.Y)).ToArray();
            await session.AddInkAsync(0, "Pen", pen);
            spec.Add(new { Subtype = "/Ink", Exact = false, Rect = Array.Empty<double>(), Points = pen.Select(p => new[] { p.X, p.Y }).ToArray() });
            await Changed("ink stroke", 60, 160, 180, 190, 6);
            // Arrow
            var tail = U(60, 250); var tip = U(200, 215);
            await session.AddInkAsync(0, "Arrow", [tail, tip]);
            spec.Add(new { Subtype = "/Ink", Exact = false, Rect = Array.Empty<double>(), Points = new[] { new[] { tail.X, tail.Y }, new[] { tip.X, tip.Y } } });
            await Changed("arrow", 60, 215, 200, 250, 16, exact: false);
            // Highlight
            var highlight = UserRect(60, 300, 200, 320);
            await session.AddMarkupAsync(0, "Highlight", [new { Left = highlight.L, Bottom = highlight.B, Right = highlight.R, Top = highlight.T }]);
            spec.Add(new { Subtype = "/Highlight", Exact = true, Rect = new[] { highlight.L, highlight.B, highlight.R, highlight.T }, Points = Array.Empty<double[]>() });
            var afterHighlight = await Changed("highlight", 60, 300, 200, 320, 4);
            // Sticky note: 20 x 20 pt square at a displayed position
            var note = UserRect(250, 300, 270, 320);
            await session.AddCommentAsync(0, "Rotated page note", note.L, note.B, note.R, note.T);
            spec.Add(new { Subtype = "/Text", Exact = true, Rect = new[] { note.L, note.B, note.R, note.T }, Points = Array.Empty<double[]>() });
            var afterNote = await Changed("sticky note", 250, 300, 270, 320, 4, exact: false);
            notePatches[fixture.Name] = Patch(afterNote, width, 250 * Scale, 300 * Scale, 20 * Scale, 20 * Scale);
            // Image: 80 x 40 px bitmap, top-left quarter green, rest of left half red, right half blue
            var bgra = new byte[80 * 40 * 4];
            for (var y = 0; y < 40; y++)
                for (var x = 0; x < 80; x++)
                {
                    var o = (y * 80 + x) * 4;
                    (byte B, byte G, byte R) c = x >= 40 ? ((byte)255, (byte)0, (byte)0) : y < 20 ? ((byte)0, (byte)160, (byte)0) : ((byte)0, (byte)0, (byte)255);
                    bgra[o] = c.B; bgra[o + 1] = c.G; bgra[o + 2] = c.R; bgra[o + 3] = 255;
                }
            var image = UserRect(60, 330, 140, 370);
            await session.AddImageAsync(0, bgra, 80, 40, image.L, image.B, image.R, image.T);
            var afterImage = await Changed("image", 60, 330, 140, 370, 3);
            Check(Dominant(afterImage, width, 80, 340) == 'G' && Dominant(afterImage, width, 80, 360) == 'R' && Dominant(afterImage, width, 120, 350) == 'B',
                $"{fixture.Name}: image reads upright (green top-left, red bottom-left, blue right) on the displayed page");
            imagePatches[fixture.Name] = Patch(afterImage, width, 60 * Scale, 330 * Scale, 80 * Scale, 40 * Scale);
            // Text box: one line of text inside a bordered rectangle
            var textBox = UserRect(200, 230, 380, 260);
            await session.AddTextBoxAsync(0, "Rotated text", textBox.L, textBox.B, textBox.R, textBox.T);
            spec.Add(new { Subtype = "/Square", Exact = true, Rect = new[] { textBox.L, textBox.B, textBox.R, textBox.T }, Points = Array.Empty<double[]>() });
            var afterText = await Changed("text box", 200, 230, 380, 260, 6);
            Check(CountDark(afterText, width, 204 * Scale, 234 * Scale, 376 * Scale, 256 * Scale) > 40, $"{fixture.Name}: text box text is drawn inside the displayed box");
            var rows = TextRowSpan(afterText, width, 204 * Scale, 234 * Scale, 376 * Scale, 256 * Scale);
            Check(rows is not null && rows.Value.Width > rows.Value.Height, $"{fixture.Name}: text box text runs horizontally on the displayed page (upright, not turned)");

            // Annotation count and refusals outside the visible box
            var extracted = await session.ExtractAsync(0);
            Check(extracted.GetProperty("Annotations").GetArrayLength() == 7, $"{fixture.Name}: seven annotations are present");
            var outside = U(-12, -12);
            var revisionBefore = session.Revision;
            await Rejected(() => session.AddInkAsync(0, "Pen", [outside, U(40, 40)]), $"{fixture.Name}: stroke starting outside the displayed page is refused");
            await Rejected(() => session.AddDrawingAsync(0, "Rectangle", outside.X, outside.Y, outside.X + 30, outside.Y + 30), $"{fixture.Name}: rectangle outside the visible box is refused");
            if (fixture.Crop)
                await Rejected(() => session.AddDrawingAsync(0, "Rectangle", 10, 10, 60, 60), $"{fixture.Name}: zero-origin coordinates outside the cropped box are refused");
            Check(session.Revision == revisionBefore, $"{fixture.Name}: refused edits leave the revision unchanged");

            // Save, independent pypdf check, reopen
            var finalPixels = await Render();
            var saved = Path.Combine(work, fixture.Name + "-saved.pdf");
            await session.SaveAsync(saved);
            var p0 = U(60, 370); var p1 = U(140, 370); var p2 = U(60, 330);
            var specPath = Path.Combine(work, fixture.Name + "-spec.json");
            File.WriteAllText(specPath, JsonSerializer.Serialize(new
            {
                Annotations = spec,
                ImageMatrix = new[] { p1.X - p0.X, p1.Y - p0.Y, p2.X - p0.X, p2.Y - p0.Y, p0.X, p0.Y },
                TextBoxRect = new[] { textBox.L, textBox.B, textBox.R, textBox.T }
            }));
            RunPython(python, $"{fixture.Name} annotation rectangles", checkScript, saved, specPath, packages);
            checks.Add($"{fixture.Name}: independent pypdf check - annotation /Rect, quad, ink points, image matrix and text position match the displayed placement");
            using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, fixture.Name + "-reopen")), saved))
            {
                var reopenedPixels = Convert.FromBase64String((await reopened.RenderAsync(0, width, height)).GetProperty("Bgra").GetString()!);
                Check(DiffBox(finalPixels, reopenedPixels, width, height) is null, $"{fixture.Name}: save and reopen render identically");
                await reopened.CloseAsync();
            }

            // Undo / Redo
            for (var i = 0; i < checkpoints; i++) await session.UndoAsync();
            Check(DiffBox(blank, await Render(), width, height) is null && (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == 0, $"{fixture.Name}: undoing every checkpoint restores the blank page");
            for (var i = 0; i < checkpoints; i++) await session.RedoAsync();
            Check(DiffBox(finalPixels, await Render(), width, height) is null, $"{fixture.Name}: redo restores every annotation");
            Check(RecoveryStore.Fingerprint(path) == sourceHashes[fixture.Name], $"{fixture.Name}: source fixture is byte-for-byte unchanged");
            await session.CloseAsync();
        }

        // The same displayed icon and image must be identical on every rotation and offset (the page rotation is compensated).
        var baseNote = notePatches["rot0"]; var baseImage = imagePatches["rot0"];
        foreach (var fixture in Fixtures.Skip(1))
        {
            Check(MaxDelta(baseImage, imagePatches[fixture.Name]) <= 40, $"{fixture.Name}: image patch matches the unrotated page pixel for pixel (within resampling)");
            var delta = MaxDelta(baseNote, notePatches[fixture.Name]);
            noteUpright[fixture.Name] = delta <= 40;
        }
        var uprightNotes = noteUpright.Count(pair => pair.Value);
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks, StickyNoteIconUpright = noteUpright }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} rotated/offset page checks ({Fixtures.Length} fixtures; sticky-note icon identical to the unrotated page on {uprightNotes} of {noteUpright.Count}). Evidence: {work}");
    }

    static void RunPython(string python, string what, string script, string a, string b, string packages)
    {
        var start = new System.Diagnostics.ProcessStartInfo(python)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", script, a, b, packages }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception($"Could not start {what}.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Independent {what} failed: {stderr}{stdout}");
    }

    static (int Left, int Top, int Right, int Bottom)? DiffBox(byte[] a, byte[] b, int width, int height)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, bt = -1;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                if (Math.Abs(a[o] - b[o]) > 24 || Math.Abs(a[o + 1] - b[o + 1]) > 24 || Math.Abs(a[o + 2] - b[o + 2]) > 24)
                { l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); bt = Math.Max(bt, y + 1); }
            }
        return r < 0 ? null : (l, t, r, bt);
    }

    static char Dominant(byte[] bgra, int width, double x, double y)
    {
        var o = ((int)(y * Scale) * width + (int)(x * Scale)) * 4;
        int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2];
        return r > 150 && g < 100 && b < 100 ? 'R' : g > 100 && r < 100 && b < 100 ? 'G' : b > 150 && r < 100 && g < 100 ? 'B' : '?';
    }

    static byte[] Patch(byte[] bgra, int width, int x, int y, int w, int h)
    {
        var patch = new byte[w * h * 4];
        for (var row = 0; row < h; row++) Array.Copy(bgra, ((y + row) * width + x) * 4, patch, row * w * 4, w * 4);
        return patch;
    }

    static int MaxDelta(byte[] a, byte[] b) { var max = 0; for (var i = 0; i < a.Length; i++) max = Math.Max(max, Math.Abs(a[i] - b[i])); return max; }

    static int CountDark(byte[] bgra, int width, int x0, int y0, int x1, int y1)
    {
        var count = 0;
        for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) { var o = (y * width + x) * 4; if (bgra[o] < 100 && bgra[o + 1] < 100 && bgra[o + 2] < 100) count++; }
        return count;
    }

    static (int Width, int Height)? TextRowSpan(byte[] bgra, int width, int x0, int y0, int x1, int y1)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++) { var o = (y * width + x) * 4; if (bgra[o] < 100 && bgra[o + 1] < 100 && bgra[o + 2] < 100) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); b = Math.Max(b, y); } }
        return r < 0 ? null : (r - l + 1, b - t + 1);
    }

    static (int Width, int Height, int Rotation, double Left, double Bottom) ReadGeometry(string path)
    {
        Native.InitLibrary();
        var document = Native.LoadDocument(path, null);
        if (document == 0) throw new Exception("Native PDFium could not load a generated fixture.");
        try
        {
            var page = Native.LoadPage(document, 0);
            try
            {
                Native.GetPageBoundingBox(page, out var rect);
                return ((int)Math.Round(Native.GetPageWidthF(page)), (int)Math.Round(Native.GetPageHeightF(page)), Native.GetRotation(page), rect.Left, rect.Bottom);
            }
            finally { Native.ClosePage(page); }
        }
        finally { Native.CloseDocument(document); }
    }

    static (double X, double Y) ToUser(string path, int width, int height, double x, double y)
    {
        var document = Native.LoadDocument(path, null);
        try
        {
            var page = Native.LoadPage(document, 0);
            try
            {
                if (Native.DeviceToPage(page, 0, 0, width * Scale, height * Scale, 0, (int)Math.Round(x * Scale), (int)Math.Round(y * Scale), out var px, out var py) == 0)
                    throw new Exception("Native PDFium device-to-page conversion failed.");
                return (px, py);
            }
            finally { Native.ClosePage(page); }
        }
        finally { Native.CloseDocument(document); }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RectF { public float Left, Top, Right, Bottom; }
        [DllImport("pdfium", EntryPoint = "FPDF_InitLibrary")] internal static extern void InitLibrary();
        [DllImport("pdfium", EntryPoint = "FPDF_LoadDocument", CharSet = CharSet.Ansi)] internal static extern nint LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? password);
        [DllImport("pdfium", EntryPoint = "FPDF_CloseDocument")] internal static extern void CloseDocument(nint document);
        [DllImport("pdfium", EntryPoint = "FPDF_LoadPage")] internal static extern nint LoadPage(nint document, int index);
        [DllImport("pdfium", EntryPoint = "FPDF_ClosePage")] internal static extern void ClosePage(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageBoundingBox")] internal static extern int GetPageBoundingBox(nint page, out RectF rect);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageWidthF")] internal static extern float GetPageWidthF(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_GetPageHeightF")] internal static extern float GetPageHeightF(nint page);
        [DllImport("pdfium", EntryPoint = "FPDFPage_GetRotation")] internal static extern int GetRotation(nint page);
        [DllImport("pdfium", EntryPoint = "FPDF_DeviceToPage")] internal static extern int DeviceToPage(nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);
    }
}
