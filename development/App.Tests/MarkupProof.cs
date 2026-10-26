using HunterPdf.Core;

static class MarkupProof
{
    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected<T>(Func<Task> action, string message, string? messagePart = null) where T : Exception
        {
            try { await action(); }
            catch (T e)
            {
                if (messagePart is not null && !e.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase)) throw;
                checks.Add(message); return;
            }
            throw new Exception(message);
        }

        var work = Path.Combine(output, "markup");
        Directory.CreateDirectory(work);
        var recovery = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        using var session = await DocumentSession.OpenAsync(worker, recovery, source);
        var original = await session.ExtractAsync(0);
        var originalCount = original.GetProperty("Annotations").GetArrayLength();
        var originalPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        await Rejected<InvalidOperationException>(
            () => session.AddMarkupAsync(0, "Note", [new { Left = 72d, Bottom = 700d, Right = 180d, Top = 720d }]),
            "Unsupported note kind rejected");
        await Rejected<InvalidOperationException>(
            () => session.AddCommentAsync(0, "  ", 72, 700, 96, 724),
            "Empty comment rejected");
        await Rejected<InvalidOperationException>(
            () => session.AddCommentAsync(0, "Comment", -1, 700, 96, 724),
            "Out-of-page comment rejected");
        await Rejected<InvalidOperationException>(
            () => session.AddMarkupAsync(0, "Highlight", [new { Left = -1d, Bottom = 700d, Right = 180d, Top = 720d }]),
            "Out-of-page markup rejected");
        await Rejected<InvalidOperationException>(
            () => session.AddMarkupAsync(0, "Highlight", [new { Left = 72d, Bottom = 700d, Right = 72d, Top = 720d }]),
            "Empty markup rectangle rejected");
        await Rejected<InvalidOperationException>(
            () => session.AddMarkupAsync(0, "Highlight", [new { Left = "NaN", Bottom = 700d, Right = 180d, Top = 720d }]),
            "Non-numeric markup coordinate rejected");
        var tooMany = Enumerable.Range(0, 101).Select(i => (object)new { Left = 72d, Bottom = 700d - i, Right = 80d, Top = 701d - i }).ToArray();
        await Rejected<InvalidOperationException>(() => session.AddMarkupAsync(0, "Highlight", tooMany), "More than 100 markup rectangles rejected");
        Check(!session.IsDirty, "Rejected markup leaves checkpoint unchanged");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Rejected<OperationCanceledException>(() => session.AddMarkupAsync(0, "Highlight",
                [new { Left = 72d, Bottom = 700d, Right = 180d, Top = 720d }], canceled.Token), "Canceled markup is surfaced");
        }
        Check(!session.IsDirty, "Canceled markup does not create a checkpoint");

        var previousPrint = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        async Task CheckPrintChange(string kind)
        {
            var current = (await session.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString();
            Check(current != previousPrint, kind + " appears in the print render");
            previousPrint = current;
        }
        await session.AddMarkupAsync(0, "Highlight", [new { Left = 72d, Bottom = 700d, Right = 180d, Top = 720d }]);
        await CheckPrintChange("Highlight");
        await session.AddMarkupAsync(0, "Underline", [new { Left = 72d, Bottom = 690d, Right = 180d, Top = 692d }]);
        await CheckPrintChange("Underline");
        await session.AddMarkupAsync(0, "Strikeout", [new { Left = 72d, Bottom = 680d, Right = 180d, Top = 682d }]);
        await CheckPrintChange("Strikeout");
        const string comment = "Review this clause — confirm the date.\nSecond line.";
        await session.AddCommentAsync(0, comment, 220, 650, 244, 674);
        await CheckPrintChange("Sticky note icon");
        var marked = await session.ExtractAsync(0);
        var annotations = marked.GetProperty("Annotations");
        Check(annotations.GetArrayLength() == originalCount + 4, "Markup and sticky note calls create four persisted PDF annotations");
        Check(annotations.EnumerateArray().Skip(originalCount).All(a => (a.GetProperty("Flags").GetInt32() & 4) != 0),
            "New markup and comment annotations carry the PDF Print flag");
        var subtypes = annotations.EnumerateArray().Select(a => a.GetProperty("Subtype").GetInt32()).ToHashSet();
        Check(subtypes.Contains(9) && subtypes.Contains(10) && subtypes.Contains(12), "Highlight, underline, and strikeout subtypes are reported");
        var highlightBounds = annotations.EnumerateArray().First(a => a.GetProperty("Subtype").GetInt32() == 9);
        Check(Math.Abs(highlightBounds.GetProperty("Left").GetDouble() - 72) < 0.01 &&
              Math.Abs(highlightBounds.GetProperty("Right").GetDouble() - 180) < 0.01,
            "Markup annotation bounds retain the requested page coordinates");
        var note = annotations.EnumerateArray().Single(a => a.GetProperty("Subtype").GetInt32() == 1);
        Check(note.GetProperty("Contents").GetString() == comment, "Sticky note comment text and multiline Unicode content are retained in the live annotation");
        Check(Math.Abs(note.GetProperty("Left").GetDouble() - 220) < 0.01 && Math.Abs(note.GetProperty("Top").GetDouble() - 674) < 0.01,
            "Sticky note uses the checked requested page rectangle");
        var markedPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        Check(markedPixels != originalPixels, "PDFium render displays created markup appearance");
        Check(session.IsDirty && session.CanUndo, "Markup and sticky note are committed as reversible checkpoints");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().All(a => a.GetProperty("Subtype").GetInt32() != 1), "Undo removes the sticky note checkpoint");
        await session.UndoAsync(); await session.UndoAsync(); await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == originalCount, "Undo removes markup by restoring the prior full checkpoint");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == originalPixels, "Undo restores the original rendered page appearance");
        await session.RedoAsync(); await session.RedoAsync(); await session.RedoAsync(); await session.RedoAsync();
        var destination = Path.Combine(work, "markup-save.pdf");
        await session.SaveAsync(destination);
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Markup save preserves the original PDF");
        using (var reopened = await DocumentSession.OpenAsync(worker, recovery, destination))
        {
            var saved = (await reopened.ExtractAsync(0)).GetProperty("Annotations");
            var savedSubtypes = saved.EnumerateArray().Select(a => a.GetProperty("Subtype").GetInt32()).ToHashSet();
            Check((await reopened.RenderForPrintAsync(0, 612, 792)).GetProperty("Bgra").GetString() == previousPrint,
                "Saved markup and comment print appearances survive reopen");
            var savedNote=saved.EnumerateArray().Single(a=>a.GetProperty("Subtype").GetInt32()==1);
            Check(saved.GetArrayLength() == originalCount + 4 && savedSubtypes.Contains(9) && savedSubtypes.Contains(10) && savedSubtypes.Contains(12) &&
                  savedNote.GetProperty("Contents").GetString()==comment && Math.Abs(savedNote.GetProperty("Left").GetDouble()-220)<0.01,
                "Saved sticky note contents, subtype, and bounds survive independent worker reopen");
            await reopened.CloseAsync();
        }

        var signed = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        using var signedSession = await DocumentSession.OpenAsync(worker, recovery, signed);
        await Rejected<SignedDocumentException>(
            () => signedSession.AddMarkupAsync(0, "Highlight", [new { Left = 72d, Bottom = 700d, Right = 180d, Top = 720d }]),
            "Signed document markup requires explicit signed-copy consent");
        await signedSession.CloseAsync();

        using (var rotated = await DocumentSession.OpenAsync(worker, recovery, source))
        {
            await rotated.RotatePageAsync(0, 1);
            var rotatedRevision = rotated.Revision;
            // Rotated pages are supported: the quad is in default user space, and the edit is its own checkpoint.
            var rotatedHighlights = (await rotated.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Count(a => a.GetProperty("Subtype").GetInt32() == 9);
            await rotated.AddMarkupAsync(0, "Highlight", [new { Left = 72d, Bottom = 700d, Right = 180d, Top = 720d }]);
            Check(rotated.Revision == rotatedRevision + 1 && (await rotated.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().Count(a => a.GetProperty("Subtype").GetInt32() == 9) == rotatedHighlights + 1,
                "Rotated page markup is applied as one more checkpoint");
            await Rejected<InvalidOperationException>(() => rotated.AddMarkupAsync(0, "Highlight",
                [new { Left = 72d, Bottom = 700d, Right = 9000d, Top = 720d }]), "Rotated page markup outside the visible page is refused", "inside the selected page");
            Check(rotated.Revision == rotatedRevision + 1 && rotated.IsDirty, "Rotated-page rejection preserves the prior checkpoints");
            await rotated.UndoAsync();
            await rotated.UndoAsync();
            await rotated.CloseAsync();
        }

        File.WriteAllText(Path.Combine(work, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} markup checks. Evidence: {work}");
    }
}
