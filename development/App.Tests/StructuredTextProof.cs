using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class StructuredTextProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "structured-text"); Directory.CreateDirectory(work);
        var source = Path.Combine(work, "nested.pdf");
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", "-B", "-c", "import sys; from pathlib import Path; sys.path.insert(0,sys.argv[1]); import nested_checks; nested_checks.fixture(Path(sys.argv[2])); cases=Path(sys.argv[2]).parent/'font-cases'; cases.mkdir(); nested_checks.create(cases)", Path.Combine(root, "tools"), source }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!) { await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Text fixture generation failed"); }
        var originalHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var engine = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, source);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        async Task Reject(Func<Task> operation, string message)
        { try { await operation(); } catch (InvalidOperationException) { checks.Add(message); return; } throw new Exception(message); }
        var revision = session.Revision;
        var data = await session.InspectStructuredTextAsync(engine, 0, 0, false, revision);
        var selected = data.GetProperty("Entries").EnumerateArray().First(e => e.GetProperty("Text").GetString() == "Before");
        var selection = JsonSerializer.SerializeToElement(new { SourceSHA256 = data.GetProperty("SourceSHA256"), FormPath = selected.GetProperty("FormPath"), TextIndex = selected.GetProperty("TextIndex"), Text = "Before" });
        Check(selected.GetProperty("CanEdit").GetBoolean() && selected.GetProperty("FormPath").GetArrayLength() == 2, "Nested text inspection returns an editable structural selection");
        Check(session.Revision == revision && !session.IsDirty, "Inspection does not change history");
        using var preview = await session.PreviewStructuredTextAsync(engine, 0, selection, "After edit", true, false, 600, 600, revision);
        Check(preview.Render.GetProperty("Bgra").GetString()!.Length > 0 && session.Revision == revision, "Actual native candidate preview leaves committed session unchanged");
        await session.ApplyStructuredTextPreviewAsync(preview);
        Check(session.Revision == revision + 1 && session.IsDirty, "Reviewed text candidate creates one durable checkpoint");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After edit"), "Native worker reads persisted nested replacement");
        await Reject(() => session.ApplyStructuredTextPreviewAsync(preview), "Committed preview cannot be reused after revision changes");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Before"), "Undo restores original nested text");
        await session.RedoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After edit"), "Redo restores nested replacement");
        await Reject(() => session.PreviewStructuredTextAsync(engine, 0, selection, "Test", false, false, 300, 300, session.Revision), "Old source-bound selection is refused after edit");
        var id = session.Id; session.Dispose();
        using var recovered = await DocumentSession.RecoverAsync(worker, store, id);
        Check((await recovered.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After edit"), "Recovery restores nested replacement");
        var saved = Path.Combine(work, "saved.pdf"); await recovered.SaveAsync(saved);
        using var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopened")), saved);
        Check((await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After edit"), "Save As and reopen retain replacement");
        var inspected = await reopened.InspectStructuredTextAsync(engine, 0, 0, false, reopened.Revision);
        var entry = inspected.GetProperty("Entries").EnumerateArray().First(e => e.GetProperty("Text").GetString() == "After edit");
        var deleteSelection = JsonSerializer.SerializeToElement(new { SourceSHA256 = inspected.GetProperty("SourceSHA256"), FormPath = entry.GetProperty("FormPath"), TextIndex = entry.GetProperty("TextIndex"), Text = "After edit" });
        using var deleted = await reopened.PreviewStructuredTextAsync(engine, 0, deleteSelection, "", false, false, 300, 300, reopened.Revision);
        await Reject(() => recovered.ApplyStructuredTextPreviewAsync(deleted), "Cross-session preview is refused");
        await reopened.ApplyStructuredTextPreviewAsync(deleted);
        Check(!(await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("After edit"), "Empty replacement deletes selected run");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await reopened.InspectStructuredTextAsync(engine, 0, 0, false, reopened.Revision, cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { checks.Add("Cancelled inspection preserves session"); }
        Check(RecoveryStore.Fingerprint(source) == originalHash, "All text operations preserve original source bytes");
        foreach (var (name, value, supported) in new[] { ("font-embedded-original", "ABCΩЖ", true), ("font-subset-original", "CABCABCAB", true), ("font-subset-original", "ABCΩЖ", false) })
        {
            var fontSource = Path.Combine(work, "font-cases", name + ".pdf"); var fontHash = RecoveryStore.Fingerprint(fontSource);
            using var fontSession = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "font-recovery-" + Guid.NewGuid().ToString("N"))), fontSource);
            var fontData = await fontSession.InspectStructuredTextAsync(engine, 0, 0, false, fontSession.Revision);
            var fontEntry = fontData.GetProperty("Entries").EnumerateArray().First(e => e.GetProperty("Text").GetString() == "ABC");
            var fontSelection = JsonSerializer.SerializeToElement(new { SourceSHA256 = fontData.GetProperty("SourceSHA256"), FormPath = fontEntry.GetProperty("FormPath"), TextIndex = fontEntry.GetProperty("TextIndex"), Text = "ABC" });
            if (supported)
            {
                using var fontPreview = await fontSession.PreviewStructuredTextAsync(engine, 0, fontSelection, value, true, false, 300, 300, fontSession.Revision);
                await fontSession.ApplyStructuredTextPreviewAsync(fontPreview);
                Check((await fontSession.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains(value), "App persists mapped glyphs: " + name + " " + value);
            }
            else
            {
                var beforeRevision = fontSession.Revision;
                await Reject(() => fontSession.PreviewStructuredTextAsync(engine, 0, fontSelection, value, true, false, 300, 300, fontSession.Revision), "App refuses missing subset glyphs");
                Check(fontSession.Revision == beforeRevision, "Missing-glyph refusal leaves history unchanged");
            }
            Check(RecoveryStore.Fingerprint(fontSource) == fontHash, "Embedded/subset font source remains unchanged: " + name + " " + value);
        }
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} structured-text integration checks: {work}");
    }
}
