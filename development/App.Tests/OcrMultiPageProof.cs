using System.Diagnostics;
using HunterPdf.Core;

static class OcrMultiPageProof
{
    internal static async Task RunAsync(string root, string work, string fixture, string worker,
        AdvancedEngineLocation engine, string model, string native)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks.Add(message);
        }
        var evidence = Path.Combine(work, "multipage-ocr-evidence");
        Directory.CreateDirectory(evidence);
        var source = Path.Combine(work, "two-scan-pages.pdf");
        var mixed = Path.Combine(work, "scan-and-text-page.pdf");
        var overBudget = Path.Combine(work, "over-budget-scans.pdf");
        var python = engine.PythonExecutable;
        var script = "import sys; from pathlib import Path; from pypdf import PdfReader, PdfWriter; import pymupdf; src=Path(sys.argv[1]); out=Path(sys.argv[2]); mixed=Path(sys.argv[3]); over=Path(sys.argv[4]); reader=PdfReader(str(src), strict=True); writer=PdfWriter(); writer.add_page(reader.pages[0]); writer.add_page(reader.pages[0]); metadata={str(k):str(v) for k,v in (reader.metadata or {}).items() if v is not None}; metadata['/Title']='OCR multi-page source proof'; writer.add_metadata(metadata); writer.write(str(out)); capped=PdfWriter(); [capped.add_page(reader.pages[0]) for _ in range(13)]; capped.write(str(over)); doc=pymupdf.open(); original=pymupdf.open(src); doc.insert_pdf(original); original.close(); page=doc.new_page(width=612,height=792); page.insert_text((72,72),'Mixed content text page'); doc.save(mixed); doc.close()";
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-I", "-B", "-c", script, fixture, source, mixed, overBudget }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception("Multi-page OCR proof fixture generation failed");
        }

        var sourceHash = RecoveryStore.Fingerprint(source);
        var store = new RecoveryStore(Path.Combine(work, "two-page-recovery"));
        using (var session = await DocumentSession.OpenAsync(worker, store, source))
        {
            var revision = session.Revision;
            var before0 = await session.RenderAsync(0, 612, 792);
            var before1 = await session.RenderAsync(1, 612, 792);
            await session.RecognizeScanAsync(engine, model, native, revision);
            Check(session.IsDirty && session.Revision == revision + 1 && session.CanUndo,
                "Multi-page OCR commits one reversible checkpoint");
            var recognizedText = new string[2];
            for (var page = 0; page < 2; page++)
            {
                recognizedText[page] = (await session.ExtractAsync(page)).GetProperty("Text").GetString() ?? "";
                Check(!string.IsNullOrWhiteSpace(recognizedText[page]),
                    $"Multi-page OCR adds searchable text to page {page + 1}");
            }
            var after0 = await session.RenderAsync(0, 612, 792);
            var after1 = await session.RenderAsync(1, 612, 792);
            var pixels0 = before0.GetProperty("Bgra").GetString();
            var pixels1 = before1.GetProperty("Bgra").GetString();
            Check(pixels0 == after0.GetProperty("Bgra").GetString() && pixels1 == after1.GetProperty("Bgra").GetString(),
                "OCR text layers preserve both pages' visible pixels");

            await session.UndoAsync();
            Check(session.CanRedo && session.PageCount == 2, "Undo returns the two-page OCR session to its original checkpoint");
            for (var page = 0; page < 2; page++)
                Check(string.IsNullOrWhiteSpace((await session.ExtractAsync(page)).GetProperty("Text").GetString()),
                    $"Undo removes OCR text from page {page + 1}");
            Check(pixels0 == (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() &&
                  pixels1 == (await session.RenderAsync(1, 612, 792)).GetProperty("Bgra").GetString(),
                "Undo restores pixel-identical renders for both source pages");

            await session.RedoAsync();
            for (var page = 0; page < 2; page++)
                Check((await session.ExtractAsync(page)).GetProperty("Text").GetString() == recognizedText[page],
                    $"Redo restores the searchable OCR layer on page {page + 1}");
            Check(session.PageCount == 2, "Redo preserves the two-page document count");
            var saved = Path.Combine(evidence, "two-page-ocr-saved.pdf");
            await session.SaveAsync(saved);
            using var reopened = await DocumentSession.OpenAsync(worker,
                new RecoveryStore(Path.Combine(evidence, "reopened-recovery")), saved);
            Check(reopened.PageCount == 2, "Saved OCR copy reopens with both pages");
            for (var page = 0; page < 2; page++)
                Check((await reopened.ExtractAsync(page)).GetProperty("Text").GetString() == recognizedText[page],
                    $"Saved OCR text survives independent worker reopen on page {page + 1}");
            Check(pixels0 == (await reopened.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString() &&
                  pixels1 == (await reopened.RenderAsync(1, 612, 792)).GetProperty("Bgra").GetString(),
                "Saved OCR copy reopens with pixel-identical renders on both pages");
            await reopened.CloseAsync();
        }
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Multi-page OCR save and recovery preserve its source PDF");

        var mixedHash = RecoveryStore.Fingerprint(mixed);
        using (var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "mixed-recovery")), mixed))
        {
            var revision = session.Revision;
            try
            {
                await session.RecognizeScanAsync(engine, model, native, revision);
                throw new Exception("OCR accepted a document containing a non-scan page");
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            Check(session.Revision == revision && !session.IsDirty, "Mixed-page refusal leaves the session unchanged");
        }
        Check(RecoveryStore.Fingerprint(mixed) == mixedHash, "Mixed-page refusal preserves its source PDF");

        var overBudgetHash = RecoveryStore.Fingerprint(overBudget);
        using (var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "over-budget-recovery")), overBudget))
        {
            var revision = session.Revision;
            try
            {
                await session.RecognizeScanAsync(engine, model, native, revision);
                throw new Exception("OCR accepted a document over the aggregate raster processing limit");
            }
            catch (InvalidOperationException) { }
            Check(session.Revision == revision && !session.IsDirty, "Raster-budget refusal leaves the session unchanged");
        }
        Check(RecoveryStore.Fingerprint(overBudget) == overBudgetHash, "Raster-budget refusal preserves its source PDF");

        File.WriteAllText(Path.Combine(evidence, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} multi-page OCR checks. Evidence: {evidence}");
    }
}
