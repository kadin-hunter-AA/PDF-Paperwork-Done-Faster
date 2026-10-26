using System.Text.Json;
using HunterPdf.Core;

/// <summary>Moving and restyling existing native drawings, verified against fresh drawings and an independent parser.</summary>
static class DrawingManagementProof
{
    public static async Task RunAsync(string root, string output)
    {
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

        var work = Path.Combine(output, "drawing-management");
        Directory.CreateDirectory(work);
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var n = 0;
        async Task<DocumentSession> Open() => await DocumentSession.OpenAsync(worker,
            new RecoveryStore(Path.Combine(work, $"recovery-{n++}")), source);
        async Task<string> Pixels(DocumentSession s) => (await s.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString()!;
        async Task<string> PrintPixels(DocumentSession s) => (await s.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString()!;
        async Task<JsonElement> Last(DocumentSession s, int subtype) => (await s.ExtractAsync(0)).GetProperty("Annotations")
            .EnumerateArray().Last(a => a.GetProperty("Subtype").GetInt32() == subtype);

        var cases = new (string Name, int Subtype, Func<DocumentSession, double, double, Task> Draw)[]
        {
            ("rectangle", 5, (s, dx, dy) => s.AddDrawingAsync(0, "Rectangle", 100 + dx, 500 + dy, 220 + dx, 580 + dy)),
            ("ellipse", 6, (s, dx, dy) => s.AddDrawingAsync(0, "Ellipse", 100 + dx, 500 + dy, 220 + dx, 580 + dy)),
            ("reversed arrow", 15, (s, dx, dy) => s.AddInkAsync(0, "Arrow", [(300 + dx, 300 + dy), (200 + dx, 250 + dy)])),
            ("pen stroke", 15, (s, dx, dy) => s.AddInkAsync(0, "Pen",
                Enumerable.Range(0, 30).Select(i => (100d + dx + i * 5, 400d + dy + 20 * Math.Sin(i / 4d))))),
        };
        foreach (var (name, subtype, draw) in cases)
        {
            using var moving = await Open();
            await draw(moving, 0, 0);
            var original = await Last(moving, subtype);
            var beforePixels = await Pixels(moving);
            await moving.MoveDrawingAsync(0, original.GetProperty("Id").GetString()!, 40, -25);
            var moved = await Last(moving, subtype);
            Check(Math.Abs(moved.GetProperty("Left").GetDouble() - original.GetProperty("Left").GetDouble() - 40) < .01 &&
                  Math.Abs(moved.GetProperty("Bottom").GetDouble() - original.GetProperty("Bottom").GetDouble() + 25) < .01,
                $"Moved {name} bounds shift by exactly the requested distance");
            var movedPixels = await Pixels(moving);
            Check(movedPixels != beforePixels, $"Moved {name} renders in its new place");
            using var fresh = await Open();
            await draw(fresh, 40, -25);
            Check(await Pixels(fresh) == movedPixels, $"Moved {name} is pixel-identical to a fresh drawing at the new position");
            Check(await PrintPixels(fresh) == await PrintPixels(moving), $"Moved {name} matches in print rendering");
            Check(moving.CanUndo, $"Moving a {name} is a reversible checkpoint");
            await moving.UndoAsync();
            Check(await Pixels(moving) == beforePixels, $"Undo returns the {name} to its original place");
            await moving.RedoAsync();

            var savePath = Path.Combine(work, $"moved-{name.Replace(' ', '-')}.pdf");
            await moving.SaveAsync(savePath);
            using (var reopened = await Open2(worker, work, n++, savePath))
                Check(await Pixels(reopened) == movedPixels, $"Moved {name} survives save and reopen");
            VerifyMoved(root, savePath, subtype, 40, -25);
            checks.Add($"Independent pypdf check: moved {name} Rect and InkList agree and stay in bounds");

            await Rejected(() => moving.MoveDrawingAsync(0, moved.GetProperty("Id").GetString()!, 0, 0), $"Zero {name} move refused");
            await Rejected(() => moving.MoveDrawingAsync(0, moved.GetProperty("Id").GetString()!, 5000, 0), $"Off-page {name} move refused");
            await Rejected(() => moving.MoveDrawingAsync(0, moved.GetProperty("Id").GetString()!, double.NaN, 1), $"Non-finite {name} move refused");
        }

        // Restyle
        using (var styling = await Open())
        {
            await styling.AddDrawingAsync(0, "Rectangle", 100, 500, 220, 580);
            await styling.AddInkAsync(0, "Pen", [(100, 300), (160, 340), (220, 300)]);
            foreach (var subtype in new[] { 5, 15 })
            {
                var target = await Last(styling, subtype);
                var before = await Pixels(styling);
                await styling.RestyleDrawingAsync(0, target.GetProperty("Id").GetString()!, 200, 30, 30, 5);
                var after = await Pixels(styling);
                Check(after != before, $"Restyled subtype {subtype} renders with a new color and width");
                await Rejected(() => styling.RestyleDrawingAsync(0, (Last(styling, subtype).Result).GetProperty("Id").GetString()!, 300, 0, 0, 2),
                    $"Out-of-range color refused for subtype {subtype}");
                await Rejected(() => styling.RestyleDrawingAsync(0, (Last(styling, subtype).Result).GetProperty("Id").GetString()!, 0, 0, 0, 40),
                    $"Out-of-range width refused for subtype {subtype}");
                await styling.UndoAsync();
                Check(await Pixels(styling) == before, $"Undo restores the previous style of subtype {subtype}");
                await styling.RedoAsync();
            }
            var stylePath = Path.Combine(work, "restyled.pdf");
            var styledPixels = await Pixels(styling);
            await styling.SaveAsync(stylePath);
            using (var reopened = await Open2(worker, work, n++, stylePath))
                Check(await Pixels(reopened) == styledPixels, "Restyled drawings survive save and reopen");
            VerifyStyle(root, stylePath);
            checks.Add("Independent pypdf check: saved colors are 200/30/30 and border width is 5");

            var bytes = File.ReadAllBytes(stylePath);
            Check(bytes.Length > 0, "Restyled output is a nonempty file");
        }

        // Protected annotations stay protected.
        using (var protectedSession = await Open())
        {
            await protectedSession.AddDrawingAsync(0, "Rectangle", 100, 500, 220, 580);
            var shape = await Last(protectedSession, 5);
            var lockedPath = Path.Combine(work, "locked.pdf");
            await protectedSession.SaveAsync(Path.Combine(work, "to-lock.pdf"));
            MakeLocked(root, Path.Combine(work, "to-lock.pdf"), lockedPath);
            using var locked = await Open2(worker, work, n++, lockedPath);
            var item = await Last(locked, 5);
            await Rejected(() => locked.MoveDrawingAsync(0, item.GetProperty("Id").GetString()!, 10, 10), "Locked drawing cannot be moved");
            await Rejected(() => locked.RestyleDrawingAsync(0, item.GetProperty("Id").GetString()!, 1, 2, 3, 2), "Locked drawing cannot be restyled");
            Check(!locked.IsDirty, "Refused drawing changes create no checkpoint");
            var comment = (await protectedSession.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().First();
            _ = shape; _ = comment;
        }
        using (var notDrawing = await Open())
        {
            await notDrawing.AddMarkupAsync(0, "Highlight", [new { Left = 100d, Bottom = 600d, Right = 200d, Top = 620d }]);
            var highlight = await Last(notDrawing, 9);
            await Rejected(() => notDrawing.MoveDrawingAsync(0, highlight.GetProperty("Id").GetString()!, 10, 10), "Highlights are not movable drawings");
            await Rejected(() => notDrawing.RestyleDrawingAsync(0, highlight.GetProperty("Id").GetString()!, 1, 2, 3, 2), "Highlights are not restylable drawings");
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Drawing management preserves the frozen source");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(
            new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} drawing management checks. Evidence: {work}");
    }

    static Task<DocumentSession> Open2(string worker, string work, int n, string path) =>
        DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, $"recovery-{n}")), path);

    static void Python(string root, string script, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, Path.Combine(root, "tools", "python-packages") }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start independent verifier.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Independent verification failed: {stderr}{stdout}");
    }

    static void VerifyMoved(string root, string path, int subtype, double dx, double dy) => Python(root, """
        import sys
        sys.path.insert(0,sys.argv[1])
        from pypdf import PdfReader
        page=PdfReader(sys.argv[2],strict=True).pages[0]
        subtype=int(sys.argv[3])
        name={5:'/Square',6:'/Circle',15:'/Ink'}[subtype]
        annots=[r.get_object() for r in page.get('/Annots',[]) if r.get_object().get('/Subtype')==name]
        assert len(annots)==1,len(annots)
        a=annots[0]
        left,bottom,right,top=map(float,a['/Rect'])
        assert 0<=left<right<=float(page.mediabox.width) and 0<=bottom<top<=float(page.mediabox.height)
        if subtype==15:
            for stroke in a['/InkList']:
                v=list(map(float,stroke))
                for x,y in zip(v[::2],v[1::2]): assert left-0.01<=x<=right+0.01 and bottom-0.01<=y<=top+0.01,(left,bottom,right,top,x,y)
        """, path, subtype.ToString());

    static void VerifyStyle(string root, string path) => Python(root, """
        import sys
        sys.path.insert(0,sys.argv[1])
        from pypdf import PdfReader
        page=PdfReader(sys.argv[2],strict=True).pages[0]
        annots=[r.get_object() for r in page.get('/Annots',[]) if r.get_object().get('/Subtype') in ('/Square','/Ink')]
        assert len(annots)==2,len(annots)
        for a in annots:
            c=[round(float(v)*255) for v in a['/C']]
            assert c==[200,30,30],c
            border=a.get('/Border')
            assert border is not None and float(border[2])==5.0,border
        """, path);

    static void MakeLocked(string root, string source, string destination) => Python(root, """
        import sys
        sys.path.insert(0,sys.argv[1])
        from pypdf import PdfReader, PdfWriter
        from pypdf.generic import NameObject, NumberObject
        writer=PdfWriter(clone_from=PdfReader(sys.argv[2])); page=writer.pages[0]
        for ref in page['/Annots']:
            if ref.get_object().get('/Subtype')=='/Square': ref.get_object()[NameObject('/F')]=NumberObject(128)
        with open(sys.argv[3],'wb') as s: writer.write(s)
        """, source, destination);
}
