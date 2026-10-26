using HunterPdf.Core;

static class DrawingProof
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

        var work = Path.Combine(output, "drawing");
        Directory.CreateDirectory(work);
        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        using var session = await DocumentSession.OpenAsync(worker, recovery, source);
        var initial = await session.ExtractAsync(0);
        var initialCount = initial.GetProperty("Annotations").GetArrayLength();
        var originalPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var originalPrintPixels = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        await Rejected(() => session.AddDrawingAsync(0, "Rectangle", -1, 10, 30, 50), "Out-of-page drawing rejected");
        await Rejected(() => session.AddDrawingAsync(0, "Ellipse", 10, 10, 10, 50), "Empty drawing bounds rejected");
        await Rejected(() => session.AddDrawingAsync(0, "Other", 10, 10, 30, 50), "Unsupported drawing type rejected");
        Check(!session.IsDirty, "Rejected drawings leave checkpoint unchanged");
        foreach (var (right, top, label) in new[] { (300d, 11d, "shallow"), (11d, 300d, "steep") })
        {
            var arrowPath = Path.Combine(work, $"arrow-{label}.pdf");
            using (var arrowSession = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(work, $"arrow-{label}-recovery")), source))
            {
                await arrowSession.AddDrawingAsync(0, "Arrow", 10, 10, right, top);
                await arrowSession.SaveAsync(arrowPath);
            }
            VerifyArrowInkBounds(root, arrowPath);
            Check(File.Exists(arrowPath), $"Saved {label} arrow output is available for independent geometry inspection");
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Shallow and steep arrow save checks preserve the frozen source");

        await session.AddDrawingAsync(0, "Line", 90, 600, 220, 690);
        var linePixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var linePrintPixels = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(linePixels != originalPixels, "Line tool renders a native Ink stroke annotation");
        Check(linePrintPixels != originalPrintPixels, "Native line stroke appears in PDFium print rendering");
        await session.AddDrawingAsync(0, "Arrow", 90, 550, 220, 590);
        var arrowPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var arrowPrintPixels = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(arrowPixels != linePixels, "Arrow tool renders a native Ink line with two arrowhead strokes");
        Check(arrowPrintPixels != linePrintPixels, "Native arrow appears in PDFium print rendering");
        await session.AddDrawingAsync(0, "Rectangle", 250, 600, 370, 690);
        var rectanglePixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var rectanglePrintPixels = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(rectanglePixels != arrowPixels, "Rectangle tool renders a native Square annotation");
        Check(rectanglePrintPixels != arrowPrintPixels, "Native rectangle appears in PDFium print rendering");
        await session.AddDrawingAsync(0, "Ellipse", 400, 600, 520, 690);
        var ellipsePixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var ellipsePrintPixels = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(ellipsePixels != rectanglePixels, "Ellipse tool renders a native Circle annotation");
        Check(ellipsePrintPixels != rectanglePrintPixels, "Native ellipse appears in PDFium print rendering");
        var live = (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().ToArray();
        var shapes = live.Where(a => a.GetProperty("Subtype").GetInt32() is 5 or 6 or 15).ToArray();
        Check(shapes.Length == 4, "Line, arrow, square and circle are persisted as native PDF annotations");
        Check(shapes.Select(a => a.GetProperty("Subtype").GetInt32()).ToHashSet().SetEquals([5, 6, 15]),
            "Drawings retain native Ink, Square and Circle PDF annotation subtypes");
        Check(shapes.All(item => (item.GetProperty("Flags").GetInt32() & 4) != 0),
            "All created drawing annotations set the PDF Print flag");
        Check(session.IsDirty && session.CanUndo, "Drawing annotations commit as reversible checkpoints");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == initialCount + 3,
            "Undo removes the last drawing checkpoint only");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == rectanglePixels,
            "Undo restores the preceding drawing appearance");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == initialCount + 2,
            "A second Undo removes the rectangle checkpoint");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == initialCount + 1,
            "Undo removes the arrow checkpoint");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == linePixels,
            "Undo restores line appearance after removing arrow");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == initialCount,
            "Undo removes the line checkpoint");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == originalPixels,
            "Undo restores the original rendered page appearance");
        Check((await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString() == originalPrintPixels,
            "Undo restores the original PDFium print rendering");
        await session.RedoAsync(); await session.RedoAsync(); await session.RedoAsync(); await session.RedoAsync();

        var destination = Path.Combine(work, "drawing-save.pdf");
        await session.SaveAsync(destination);
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Drawing save preserves the original PDF");
        var bytes = File.ReadAllBytes(destination);
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        bool HasSubtype(string subtype) => System.Text.RegularExpressions.Regex.IsMatch(raw, @"/Subtype\s*/" + subtype + @"\b");
        Check(HasSubtype("Ink"),
            "Saved PDF contains native Ink stroke annotation dictionaries for line and arrow tools");
        Check(HasSubtype("Square"),
            "Saved PDF contains a raw native Square annotation dictionary");
        Check(HasSubtype("Circle"),
            "Saved PDF contains a raw native Circle annotation dictionary");
        using (var reopened = await DocumentSession.OpenAsync(worker, recovery, destination))
        {
            var saved = (await reopened.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray()
                .Where(a => a.GetProperty("Subtype").GetInt32() is 5 or 6 or 15).ToArray();
            Check(saved.Length == 4 && saved.Count(item => item.GetProperty("Subtype").GetInt32() == 15) == 2 &&
                saved.Select(a => a.GetProperty("Subtype").GetInt32()).ToHashSet().SetEquals([5, 6, 15]),
                "Line, arrow, rectangle and ellipse survive independent worker save/reopen");
            Check(saved.All(item => (item.GetProperty("Flags").GetInt32() & 4) != 0),
                "All saved drawing annotations retain the PDF Print flag after reopen");
            Check((await reopened.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() != originalPixels,
                "Saved drawing annotations retain their rendered appearance after reopen");
            var reopenedPrintPixels = (await reopened.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
            Check(reopenedPrintPixels != originalPrintPixels,
                "Saved print-enabled annotations remain visible in PDFium print rendering after reopen");
            Check((await reopened.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == ellipsePixels,
                "Saved arrow and drawing annotations retain rendered appearance after reopen");
            Check(saved.All(item => item.GetProperty("CanDelete").GetBoolean()),
                "Unlocked native line strokes, rectangles and ellipses are eligible for annotation management");
            foreach (var subtype in new[] { 15, 5, 6 })
            {
                var beforeDelete = (await reopened.ExtractAsync(0)).GetProperty("Annotations");
                var current = beforeDelete.EnumerateArray().First(item => item.GetProperty("Subtype").GetInt32() == subtype);
                await reopened.DeleteAnnotationAsync(0, current.GetProperty("Id").GetString()!);
                Check((await reopened.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == beforeDelete.GetArrayLength() - 1,
                    $"Annotation manager deletes the selected native drawing subtype {subtype}");
                await reopened.UndoAsync();
                Check((await reopened.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == beforeDelete.GetArrayLength(),
                    $"Undo restores native drawing subtype {subtype} after manager deletion");
            }
            await reopened.CloseAsync();
        }
        foreach (var (mode, subtype) in new[] { ("locked", 6), ("popup", 15) })
        {
            var fixture = Path.Combine(work, $"drawing-{mode}.pdf");
            MakeShapeFixture(root, destination, fixture, mode, subtype);
            using var restricted = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(work, $"{mode}-recovery")), fixture);
            var item = (await restricted.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray()
                .First(value => value.GetProperty("Subtype").GetInt32() == subtype);
            Check(!item.GetProperty("CanDelete").GetBoolean(), $"Locked or linked drawing subtype {subtype} is protected from deletion");
            await Rejected(() => restricted.DeleteAnnotationAsync(0, item.GetProperty("Id").GetString()!),
                $"Protected drawing subtype {subtype} cannot be deleted");
            Check(!restricted.IsDirty, $"Protected drawing subtype {subtype} creates no checkpoint");
            if (mode == "popup")
                Check(!(await restricted.GetAnnotationManagementStatusAsync()).GetProperty("Supported").GetBoolean(),
                    "Linked drawing popup protects annotation management for the document");
            await restricted.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Source fixture remains byte-for-byte unchanged");
        File.WriteAllText(Path.Combine(work, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} drawing checks. Evidence: {work}");
    }

    static void MakeShapeFixture(string root, string source, string destination, string mode, int subtype)
    {
        const string script = """
            import sys
            sys.path.insert(0,sys.argv[5])
            from pypdf import PdfReader, PdfWriter
            from pypdf.generic import NameObject, NumberObject, DictionaryObject, ArrayObject
            reader=PdfReader(sys.argv[1]); writer=PdfWriter(clone_from=reader); page=writer.pages[0]
            subtype=int(sys.argv[3]); mode=sys.argv[4]
            ref=next(ref for ref in page['/Annots'] if ref.get_object().get('/Subtype')==('/Circle' if subtype==6 else '/Ink'))
            item=ref.get_object()
            if mode=='locked': item[NameObject('/F')]=NumberObject(128)
            if mode=='popup':
                popup=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/Popup'),NameObject('/Rect'):ArrayObject([NumberObject(n) for n in (1,1,20,20)]),NameObject('/Parent'):ref})
                popup_ref=writer._add_object(popup); item[NameObject('/Popup')]=popup_ref; page['/Annots'].append(popup_ref)
            with open(sys.argv[2],'wb') as stream: writer.write(stream)
            """;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, source, destination, subtype.ToString(), mode,
                     Path.Combine(root, "tools", "python-packages") }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start pypdf fixture generator.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Drawing protection fixture generator failed: {stderr}{stdout}");
    }

    static void VerifyArrowInkBounds(string root, string path)
    {
        const string script = """
            import sys
            sys.path.insert(0,sys.argv[2])
            from pypdf import PdfReader
            page=PdfReader(sys.argv[1],strict=True).pages[0]
            arrow=next(ref.get_object() for ref in page.get('/Annots',[]) if ref.get_object().get('/Subtype')=='/Ink')
            left,bottom,right,top=map(float,arrow['/Rect'])
            paths=arrow['/InkList']
            assert len(paths)==3, paths
            for path in paths:
                values=list(map(float,path))
                assert len(values)>=4 and len(values)%2==0, values
                for x,y in zip(values[::2],values[1::2]): assert left<=x<=right and bottom<=y<=top,(left,bottom,right,top,x,y)
            """;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, path, Path.Combine(root, "tools", "python-packages") }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start independent arrow geometry verifier.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Arrow strokes escaped their annotation bounds in {path}: {stderr}{stdout}");
    }
}
