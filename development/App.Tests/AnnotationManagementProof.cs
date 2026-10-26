using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class AnnotationManagementProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "annotation-management"); Directory.CreateDirectory(work);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        async Task Reject<T>(Func<Task> action, string message) where T : Exception
        { try { await action(); } catch (T) { checks.Add(message); return; } throw new Exception(message); }
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, source);
        var originalText = (await session.ExtractAsync(0)).GetProperty("Text").GetString();
        await session.AddCommentAsync(0, "First note", 220, 650, 244, 674);
        await session.AddCommentAsync(0, "Middle note", 250, 650, 274, 674);
        await session.AddCommentAsync(0, "Last note", 280, 650, 304, 674);
        await session.AddMarkupAsync(0, "Highlight", [new { Left = 72, Bottom = 700, Right = 180, Top = 720 }]);
        async Task<JsonElement[]> Annotations() => (await session.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().ToArray();
        var note = (await Annotations()).Single(a => a.GetProperty("Contents").GetString() == "Middle note");
        var oldId = note.GetProperty("Id").GetString()!;
        Check(note.GetProperty("CanDelete").GetBoolean() && note.GetProperty("ContentsAvailable").GetBoolean(), "Unlocked app-created sticky notes expose managed annotation references");
        var beforePixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        await session.RenderForPrintAsync(0, 612, 792);
        const string replacement = "Updated — café 🖋\nSecond line.";
        await session.UpdateCommentAsync(0, oldId, replacement);
        var updated = (await Annotations()).Single(a => a.GetProperty("Contents").GetString() == replacement);
        Check(updated.GetProperty("Left").GetDouble() == 250 && updated.GetProperty("Right").GetDouble() == 274,
            "Updating sticky-note contents preserves its rectangle");
        Check((await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() == beforePixels, "Updating comment text preserves the page's visible appearance");
        var revision = session.Revision;
        await Reject<InvalidOperationException>(() => session.DeleteAnnotationAsync(0, oldId), "A stale annotation reference cannot delete a different annotation");
        Check(session.Revision == revision, "Stale-reference refusal leaves revision unchanged");
        await session.UndoAsync();
        Check((await Annotations()).Any(a => a.GetProperty("Contents").GetString() == "Middle note"), "Undo restores the previous comment value");
        await session.RedoAsync();
        Check((await Annotations()).Any(a => a.GetProperty("Contents").GetString() == replacement), "Redo restores the Unicode multiline update");
        var selected = (await Annotations()).Single(a => a.GetProperty("Contents").GetString() == replacement);
        await session.DeleteAnnotationAsync(0, selected.GetProperty("Id").GetString()!);
        var remaining = await Annotations();
        Check(remaining.Length == 3 && remaining.Any(a => a.GetProperty("Contents").GetString() == "First note") &&
            remaining.Any(a => a.GetProperty("Contents").GetString() == "Last note") && remaining.All(a => a.GetProperty("Contents").GetString() != replacement),
            "Deleting a middle annotation removes only the selected note");
        await session.UndoAsync();
        Check((await Annotations()).Length == 4, "Undo restores the deleted annotation");
        await session.RedoAsync();
        var id = session.Id; session.Dispose();
        session = await DocumentSession.RecoverAsync(worker, store, id);
        using (session)
        {
            Check((await Annotations()).Length == 3, "Recovery preserves annotation deletion");
            Check((await session.ExtractAsync(0)).GetProperty("Text").GetString() == originalText, "Annotation management preserves page text");
            var highlight = (await Annotations()).Single(a => a.GetProperty("Subtype").GetInt32() == 9);
            await Reject<InvalidOperationException>(() => session.UpdateCommentAsync(0, highlight.GetProperty("Id").GetString()!, "Wrong type"), "Text-markup contents cannot be changed through the sticky-note editor");
            var noteId = (await Annotations()).First(a => a.GetProperty("Subtype").GetInt32() == 1).GetProperty("Id").GetString()!;
            await Reject<InvalidOperationException>(() => session.UpdateCommentAsync(0, noteId, " "), "Empty replacement comment is refused");
            var freshId = (await Annotations()).First(a => a.GetProperty("Subtype").GetInt32() == 1).GetProperty("Id").GetString()!;
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); await Reject<OperationCanceledException>(() => session.DeleteAnnotationAsync(0, freshId, cancel.Token), "Pre-cancelled annotation deletion is refused"); }
            var saved = Path.Combine(work, "managed.pdf"); await session.SaveAsync(saved);
            VerifySavedGeometry(root, saved);
            checks.Add("Independent pypdf inspection confirms viewer/print rendering and later edits did not normalize surviving note rectangles");
            using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopen")), saved))
                Check((await reopened.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == 3, "Managed annotations survive save and reopen");
            using (var other = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "cross-session")), saved))
            {
                await Reject<InvalidOperationException>(() => other.DeleteAnnotationAsync(0, freshId), "An annotation reference from another session is refused");
                Check(!other.IsDirty && (await other.ExtractAsync(0)).GetProperty("Annotations").GetArrayLength() == 3, "Cross-session refusal preserves the other document's annotations");
            }
            foreach (var mode in new[] { "locked", "readonly", "lockedcontents", "popup", "reply", "long", "many" })
            {
                var fixture = Path.Combine(work, mode + ".pdf"); MakeFixture(root, saved, fixture, mode);
                using var restricted = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, mode + "-recovery")), fixture);
                var annotations = (await restricted.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().ToArray();
                if (mode == "many")
                {
                    var data = await restricted.ExtractAsync(0);
                    Check(data.GetProperty("AnnotationCount").GetInt32() > 1000 && data.GetProperty("AnnotationsTruncated").GetBoolean() &&
                        data.GetProperty("Annotations").GetArrayLength() == 1000, "Annotation metadata explicitly bounds large lists to the first 1,000 annotations");
                    continue;
                }
                var target = annotations.First(a => a.GetProperty("Subtype").GetInt32() == 1);
                if (mode == "reply")
                {
                    Check(!(await restricted.GetAnnotationManagementStatusAsync()).GetProperty("Supported").GetBoolean(), "Incoming annotation replies protect their parent even when the parent has no outgoing popup/reply key");
                    await Reject<InvalidOperationException>(() => restricted.DeleteAnnotationAsync(0, target.GetProperty("Id").GetString()!), "A parent of an incoming reply cannot be deleted");
                    Check(!restricted.IsDirty, "Reply-parent refusal creates no checkpoint");
                    continue;
                }
                if (mode == "long") { Check(!target.GetProperty("ContentsAvailable").GetBoolean(), "Oversized saved note contents are explicitly marked unavailable instead of silently appearing empty"); continue; }
                Check(!target.GetProperty("CanDelete").GetBoolean(), "Locked or linked notes are exposed as protected: " + mode);
                await Reject<InvalidOperationException>(() => restricted.DeleteAnnotationAsync(0, target.GetProperty("Id").GetString()!), "Protected annotation deletion is refused: " + mode);
                Check(!restricted.IsDirty, "Protected refusal creates no checkpoint: " + mode);
            }
        }
        var interactive = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "interactive-features.pdf");
        var interactiveHash = RecoveryStore.Fingerprint(interactive);
        using (var protectedSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "interactive")), interactive))
        {
            await protectedSession.AuthorizeSignedCopyAsync();
            foreach (var subtype in new[] { 2, 20 })
            {
                var target = (await protectedSession.ExtractAsync(0)).GetProperty("Annotations").EnumerateArray().First(a => a.GetProperty("Subtype").GetInt32() == subtype);
                Check(!target.GetProperty("CanDelete").GetBoolean(), "Links and widgets are exposed as protected: " + subtype);
                await Reject<InvalidOperationException>(() => protectedSession.DeleteAnnotationAsync(0, target.GetProperty("Id").GetString()!), "Links and widgets cannot be deleted through annotation management: " + subtype);
            }
            Check(!protectedSession.IsDirty, "Protected interactive structures remain unchanged");
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash && RecoveryStore.Fingerprint(interactive) == interactiveHash, "All annotation edits preserve frozen source PDFs");
        var signed = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        var signedHash = RecoveryStore.Fingerprint(signed);
        using (var signedSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "signed")), signed))
        {
            await Reject<SignedDocumentException>(() => signedSession.DeleteAnnotationAsync(0, "untrusted reference"), "Signed originals require explicit copy consent before annotation changes");
            Check(!signedSession.IsDirty && RecoveryStore.Fingerprint(signed) == signedHash, "Signed-copy refusal preserves signed source and session state");
        }
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} annotation management checks. Evidence: {work}");
    }
    static void MakeFixture(string root, string source, string destination, string mode)
    {
        const string script = """
            import sys
            from pypdf import PdfReader,PdfWriter
            from pypdf.generic import NameObject,NumberObject,DictionaryObject,ArrayObject,TextStringObject
            w=PdfWriter(clone_from=PdfReader(sys.argv[1])); page=w.pages[0]
            ref=next(r for r in page['/Annots'] if r.get_object().get('/Subtype')=='/Text'); a=ref.get_object()
            if sys.argv[3]=='locked': a[NameObject('/F')]=NumberObject(128)
            if sys.argv[3]=='readonly': a[NameObject('/F')]=NumberObject(64)
            if sys.argv[3]=='lockedcontents': a[NameObject('/F')]=NumberObject(512)
            if sys.argv[3]=='long': a[NameObject('/Contents')]=TextStringObject('L'*9000)
            if sys.argv[3]=='popup':
                popup=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/Popup'),NameObject('/Rect'):ArrayObject([NumberObject(n) for n in (100,100,200,200)]),NameObject('/Parent'):ref})
                p=w._add_object(popup); a[NameObject('/Popup')]=p; page['/Annots'].append(p)
            if sys.argv[3]=='reply':
                reply=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/Text'),NameObject('/Rect'):a['/Rect'],NameObject('/Contents'):TextStringObject('Reply'),NameObject('/IRT'):ref})
                page['/Annots'].append(w._add_object(reply))
            if sys.argv[3]=='many':
                for n in range(1001):
                    item=DictionaryObject({NameObject('/Type'):NameObject('/Annot'),NameObject('/Subtype'):NameObject('/Text'),NameObject('/Rect'):a['/Rect'],NameObject('/Contents'):TextStringObject('Extra note')})
                    page['/Annots'].append(w._add_object(item))
            with open(sys.argv[2],'wb') as stream: w.write(stream)
            """;
        var start = new ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, source, destination, mode }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; _ = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Annotation fixture generator failed: " + error);
    }
    static void VerifySavedGeometry(string root, string path)
    {
        const string script = """
            import sys
            from pypdf import PdfReader
            notes={a.get_object().get('/Contents'):list(a.get_object()['/Rect']) for a in PdfReader(sys.argv[1]).pages[0]['/Annots'] if a.get_object().get('/Subtype')=='/Text'}
            assert notes=={'First note':[220,650,244,674],'Last note':[280,650,304,674]}
            """;
        var start = new ProcessStartInfo(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; _ = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Saved annotation geometry verification failed: " + error);
    }
}
