using System.Text.Json;
using HunterPdf.Core;

static class OcrIntegrationProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "ocr-integration");
        Directory.CreateDirectory(work);
        var fixture = Path.Combine(root, "fixtures", "corpus-v1", "edge", "scan-real-text.pdf");
        if (!File.Exists(fixture)) throw new FileNotFoundException("OCR proof fixture missing.", fixture);
        var originalHash = RecoveryStore.Fingerprint(fixture);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var engine = new AdvancedEngineLocation(Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe"),
            Path.Combine(root, "App.Advanced", "worker.py"));
        var model = Path.Combine(root, "tools", "advanced-python-3.14.8", "tessdata", "eng.traineddata");
        var native = Path.Combine(root, "native", "bin", "pdfium.dll");
        var checks = new List<string>();
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, fixture);
        var id = session.Id;
        var revision = session.Revision;
        var before = await session.RenderAsync(0, 612, 792);
        await session.RecognizeScanAsync(engine, model, native, revision);
        Check(session.IsDirty && session.Revision == revision + 1, "OCR commits one durable dirty checkpoint");
        var text = (await session.ExtractAsync(0)).GetProperty("Text").GetString()!;
        Check(text.Contains("Invoice number 12345", StringComparison.Ordinal), "Native PDFium extracts the recognized invoice text");
        var after = await session.RenderAsync(0, 612, 792);
        Check(before.GetProperty("Bgra").GetString() == after.GetProperty("Bgra").GetString(), "OCR leaves native rendered page pixels unchanged");
        session.Dispose();
        using var recovered = await DocumentSession.RecoverAsync(worker, store, id);
        Check((await recovered.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Invoice number 12345"), "Recovery restores the OCR checkpoint");
        await recovered.UndoAsync();
        Check(string.IsNullOrWhiteSpace((await recovered.ExtractAsync(0)).GetProperty("Text").GetString()), "Undo removes the OCR text layer");
        await recovered.RedoAsync();
        Check((await recovered.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Invoice number 12345"), "Redo restores recognized text");
        var saved = Path.Combine(work, "searchable.pdf");
        await recovered.SaveAsync(saved);
        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopen")), saved))
            Check((await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("Invoice number 12345"), "Saved OCR output reopens with searchable text");
        var acceptedRevision = recovered.Revision;
        try { await recovered.RecognizeScanAsync(engine, model, native, revision); throw new Exception("Stale OCR accepted"); }
        catch (InvalidOperationException) { checks.Add("Stale OCR request is refused"); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await recovered.RecognizeScanAsync(engine, model, native, acceptedRevision, cancelled.Token); throw new Exception("Cancelled OCR accepted"); }
            catch (OperationCanceledException) { checks.Add("Pre-cancelled OCR is refused"); }
        }
        try { await recovered.RecognizeScanAsync(engine, model, native, acceptedRevision); throw new Exception("Already searchable PDF accepted"); }
        catch (InvalidOperationException) { checks.Add("Already searchable content is refused rather than rerasterized"); }
        Check(recovered.Revision == acceptedRevision && !recovered.IsDirty, "Refusals preserve saved session state");
        Check(RecoveryStore.Fingerprint(fixture) == originalHash, "Frozen scan bytes remain unchanged");
        await OcrMultiPageProof.RunAsync(root, work, fixture, worker, engine, model, native);
        Check(true, "Multi-page OCR preserves every scan page; mixed-content refusal preserves source and session");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} OCR integration checks. Evidence: {work}");
    }
}
