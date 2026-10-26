using HunterPdf.Core;

/// <summary>Freehand pen and any-direction line/arrow strokes written as native Ink annotations.</summary>
static class InkProof
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

        var work = Path.Combine(output, "ink");
        Directory.CreateDirectory(work);
        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        using var session = await DocumentSession.OpenAsync(worker, recovery, source);
        var initialCount = (await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength();
        var originalPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();

        await Rejected(() => session.AddInkAsync(0, "Pen", [(10, 10)]), "Single-point stroke rejected");
        await Rejected(() => session.AddInkAsync(0, "Pen", [(10, 10), (10, 10)]), "Zero-length stroke rejected");
        await Rejected(() => session.AddInkAsync(0, "Pen", [(10, 10), (-5, 30)]), "Out-of-page stroke rejected");
        await Rejected(() => session.AddInkAsync(0, "Pen", [(10, 10), (double.NaN, 30)]), "Non-finite stroke rejected");
        await Rejected(() => session.AddInkAsync(0, "Line", [(10, 10), (20, 20), (30, 30)]), "Multi-point line rejected");
        await Rejected(() => session.AddInkAsync(0, "Rectangle", [(10, 10), (30, 30)]), "Unsupported ink kind rejected");
        await Rejected(() => session.AddInkAsync(0, "Pen", Enumerable.Range(0, 5001).Select(i => (10d + i % 100, 10d + i % 7))),
            "Oversized stroke rejected");
        Check(!session.IsDirty, "Rejected strokes leave the checkpoint unchanged");

        var pen = Enumerable.Range(0, 60).Select(i => (100d + i * 4, 400d + 40 * Math.Sin(i / 6d))).ToArray();
        await session.AddInkAsync(0, "Pen", pen);
        var penPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(penPixels != originalPixels, "Freehand pen stroke renders");
        await session.AddInkAsync(0, "Line", [(500, 300), (300, 200)]);
        var linePixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(linePixels != penPixels, "Top-right to bottom-left line renders");
        await session.AddInkAsync(0, "Arrow", [(500, 600), (350, 700)]);
        var arrowPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(arrowPixels != linePixels, "Down-right to up-left arrow renders");
        await session.AddInkAsync(0, "Arrow", [(5, 5), (1, 1)]);
        Check(session.IsDirty && session.CanUndo, "Ink strokes commit as reversible checkpoints");

        var ink = (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray()
            .Where(a => a.GetProperty("Subtype").GetInt32() == 15).ToArray();
        Check(ink.Length == 4, "Four native Ink annotations are persisted");
        Check(ink.All(a => (a.GetProperty("Flags").GetInt32() & 4) != 0), "All ink annotations carry the Print flag");

        var destination = Path.Combine(work, "ink-save.pdf");
        await session.SaveAsync(destination);
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Ink save preserves the original PDF");
        VerifyInk(root, destination);
        checks.Add("Independent pypdf check: pen is one many-point stroke; arrows have three in-bounds strokes");
        using (var reopened = await DocumentSession.OpenAsync(worker, recovery, destination))
        {
            Check((await reopened.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() != originalPixels,
                "Saved ink strokes retain their appearance after reopen");
            await reopened.CloseAsync();
        }

        for (var i = 0; i < 4; i++) await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == initialCount,
            "Undo removes every ink checkpoint");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == originalPixels,
            "Undo restores the original page appearance");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Source fixture remains byte-for-byte unchanged");

        // Text box: bordered rectangle plus one line of text, one checkpoint.
        using var boxSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "box-recovery")), source);
        var boxBefore = await boxSession.ExtractAsync(0);
        var boxPixels = (await boxSession.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var boxPrintPixels = (await boxSession.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        await Rejected(() => boxSession.AddTextBoxAsync(0, "", 100, 300, 300, 340), "Empty text box rejected");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Café", 100, 300, 300, 340), "Non-ASCII text box rejected");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "line\nbreak", 100, 300, 300, 340), "Multi-line text box rejected");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Too long for this small box", 100, 300, 140, 340), "Overflowing text refused instead of clipped");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Short", 100, 300, 300, 305), "Box shorter than the text refused");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Hi", -5, 300, 200, 340), "Text box outside the page refused");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Hi", 200, 300, 100, 340), "Inverted text box refused");
        await Rejected(() => boxSession.AddTextBoxAsync(0, "Hi", 100, 300, double.PositiveInfinity, 340), "Non-finite text box refused");
        Check(!boxSession.IsDirty, "Rejected text boxes leave the checkpoint unchanged");
        await boxSession.AddTextBoxAsync(0, "Reviewed by Accounts", 100, 300, 300, 340);
        var boxAfter = await boxSession.ExtractAsync(0);
        Check(boxAfter.GetProperty("Text").GetString()!.Contains("Reviewed by Accounts"), "Text box line is saved as real page text");
        Check(boxAfter.GetProperty("Annotations").GetArrayLength() == boxBefore.GetProperty("Annotations").GetArrayLength() + 1,
            "Text box adds one border annotation");
        Check((await boxSession.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() != boxPixels, "Text box renders");
        Check((await boxSession.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString() != boxPrintPixels, "Text box border and text appear in print rendering");
        var boxPath = Path.Combine(work, "textbox-save.pdf");
        await boxSession.SaveAsync(boxPath);
        VerifyTextBox(root, boxPath);
        checks.Add("Independent pypdf check: text sits inside the Square border rectangle");
        await boxSession.UndoAsync();
        var undone = await boxSession.ExtractAsync(0);
        Check(undone.GetProperty("Annotations").GetArrayLength() == boxBefore.GetProperty("Annotations").GetArrayLength() &&
              undone.GetProperty("Text").GetString() == boxBefore.GetProperty("Text").GetString(),
            "A single Undo removes both the text and its border");
        Check((await boxSession.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == boxPixels, "Undo restores the original page appearance after a text box");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Text box checks preserve the frozen source");
        File.WriteAllText(Path.Combine(work, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} ink checks. Evidence: {work}");
    }

    static void VerifyTextBox(string root, string path)
    {
        const string script = """
            import sys
            sys.path.insert(0,sys.argv[2])
            from pypdf import PdfReader
            page=PdfReader(sys.argv[1],strict=True).pages[0]
            squares=[r.get_object() for r in page.get('/Annots',[]) if r.get_object().get('/Subtype')=='/Square']
            assert len(squares)==1, len(squares)
            left,bottom,right,top=map(float,squares[0]['/Rect'])
            assert (left,bottom,right,top)==(100,300,300,340),(left,bottom,right,top)
            assert int(squares[0].get('/F',0))&4
            hits=[]
            def visit(text,cm,tm,font,size):
                if 'Reviewed by Accounts' in text: hits.append((tm[4]*cm[0]+cm[4],tm[5]*cm[3]+cm[5]))
            page.extract_text(visitor_text=visit)
            assert hits,'text missing'
            x,y=hits[0]
            assert left<=x<=right and bottom<=y<=top,(x,y)
            """;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, path, Path.Combine(root, "tools", "python-packages") }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start independent text box verifier.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Independent text box verification failed for {path}: {stderr}{stdout}");
    }

    static void VerifyInk(string root, string path)
    {
        const string script = """
            import sys
            sys.path.insert(0,sys.argv[2])
            from pypdf import PdfReader
            page=PdfReader(sys.argv[1],strict=True).pages[0]
            inks=[r.get_object() for r in page.get('/Annots',[]) if r.get_object().get('/Subtype')=='/Ink']
            assert len(inks)==4, len(inks)
            counts=sorted(len(i['/InkList']) for i in inks)
            assert counts==[1,1,3,3], counts
            for ink in inks:
                left,bottom,right,top=map(float,ink['/Rect'])
                for stroke in ink['/InkList']:
                    v=list(map(float,stroke))
                    for x,y in zip(v[::2],v[1::2]): assert left<=x<=right and bottom<=y<=top,(left,bottom,right,top,x,y)
            assert any(len(ink['/InkList'][0])==120 for ink in inks)
            """;
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, path, Path.Combine(root, "tools", "python-packages") }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new Exception("Could not start independent ink verifier.");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception($"Independent ink verification failed for {path}: {stderr}{stdout}");
    }
}
