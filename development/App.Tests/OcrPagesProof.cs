using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HunterPdf.Core;

static class OcrPagesProof
{
    const string GenerateScript = """
import pymupdf, sys
out = sys.argv[1]
LINES = ["Quarterly invoice summary", "Invoice number 12345 for Acme Supply", "Payment due within thirty days of receipt", "Thank you for your business"]
def scan(doc, angle, quarter):
    tmp = pymupdf.open(); p = tmp.new_page(width=612, height=792); y = 100
    for l in LINES:
        p.insert_text((72, y), l, fontsize=18, fontname="helv"); y += 40
    pix = tmp[0].get_pixmap(matrix=pymupdf.Matrix(150/72, 150/72).prerotate(angle + quarter * 90), alpha=False, colorspace=pymupdf.csRGB)
    pix.set_dpi(150, 150)
    page = doc.new_page(width=pix.width * 72 / 150, height=pix.height * 72 / 150)
    page.insert_image(page.rect, pixmap=pix)
doc = pymupdf.open()
p = doc.new_page(width=612, height=792)
p.insert_text((72, 100), "Native text page Ledger 2026", fontsize=18, fontname="helv")
scan(doc, 0.0, 1)
scan(doc, 2.0, 0)
doc.save(out)
""";
    const string CheckScript = """
import sys, json, hashlib
from pypdf import PdfReader
r = PdfReader(sys.argv[1])
print(json.dumps([{"text": (p.extract_text() or ""), "contents": hashlib.sha256(p.get_contents().get_data() if p.get_contents() else b"").hexdigest()} for p in r.pages]))
""";

    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "ocr-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var engine = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var tessdata = Path.Combine(root, "tools", "advanced-python-3.14.8", "tessdata");
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); } catch (T) { checks.Add(message); return; }
            throw new Exception(message);
        }
        string Run(string script, params string[] args)
        {
            var path = Path.Combine(work, "helper-" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(path, script);
            var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-I", "-B", path }.Concat(args)) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Helper failed: " + stderr);
            return stdout;
        }
        JsonElement Pypdf(string pdf) => JsonDocument.Parse(Run(CheckScript, pdf)).RootElement.Clone();

        var input = Path.Combine(work, "mixed.pdf");
        Run(GenerateScript, input);
        var inputHash = RecoveryStore.Fingerprint(input);
        var original = Pypdf(input);
        Check(original[0].GetProperty("text").GetString()!.Contains("Native text page") &&
              original[1].GetProperty("text").GetString()!.Trim().Length == 0, "Generated document has a text page and image-only scan pages");

        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var session = await DocumentSession.OpenAsync(worker, store, input);
        var inspection = await session.InspectOcrAsync(engine);
        Check(inspection.PageCount == 3 && inspection.HasText == 1 && inspection.ImageOnly == 2 && inspection.Empty == 0 &&
              inspection.Pages[0].Kind == "HasText" && inspection.Pages[1].Kind == "ImageOnly" && inspection.Pages[2].Kind == "ImageOnly" &&
              inspection.ImageOnlyPages.SequenceEqual([1, 2]) && inspection.Pages[1].Coverage >= 0.9 && inspection.Pages[1].Pixels > 1_000_000,
            "Inspection classifies text, scanned and pixel counts per page");
        var languages = await session.ListOcrLanguagesAsync(engine, [tessdata]);
        Check(languages.Any(l => l.Code == "eng" && l.Name == "English"), "Engine lists the installed English pack");

        var render = new string[3];
        for (var i = 0; i < 3; i++) render[i] = (await session.RenderAsync(i, 306, 396)).GetProperty("Bgra").GetString()!;
        var options = new OcrPagesOptions(null, ["eng"], [tessdata], Deskew: true, ChunkSize: 1);
        var revision = session.Revision;

        // Cancellation midway: first chunk finishes, then the token fires.
        using (var cts = new CancellationTokenSource())
        {
            var seen = new List<(int, int)>();
            var cancelProgress = new SyncProgress(p => { seen.Add(p); if (p.done >= 1) cts.Cancel(); });
            await Rejected<OperationCanceledException>(() => session.RecognizePagesAsync(engine, options, revision, cancelProgress, cts.Token),
                "Cancellation midway aborts the run");
            Check(seen.Any(p => p.Item1 == 1), "First chunk completed before cancellation");
        }
        Check(session.Revision == revision && !session.IsDirty && RecoveryStore.Fingerprint(input) == inputHash, "Cancelled OCR leaves revision, dirty state and source unchanged");
        Check(!Directory.EnumerateFiles(store.Root, "*.part*", SearchOption.AllDirectories).Any(), "Cancelled OCR leaves no partial candidates");

        var reports = new List<(int done, int total)>();
        var result = await session.RecognizePagesAsync(engine, options, revision, new SyncProgress(reports.Add));
        Check(reports.Count >= 3 && reports[0] == (0, 2) && reports[^1] == (2, 2) &&
              reports.Zip(reports.Skip(1)).All(pair => pair.Second.done >= pair.First.done), "Progress increases to the total");
        Check(result.Recognized.Select(r => r.Page).Order().SequenceEqual([1, 2]) && result.SkippedHasText.Count == 0, "Only scanned pages were processed");
        Check(session.Revision == revision + 1 && session.IsDirty, "Whole run is one durable checkpoint");
        var rotated = result.Recognized.Single(r => r.Page == 1);
        Check(rotated.RotationDegrees != 0 || result.OsdAvailable == false && result.OrientationMethod == "trial",
            $"Sideways scan orientation detected ({rotated.RotationDegrees} degrees, method {result.OrientationMethod}, osd.traineddata present: {result.OsdAvailable})");
        Check(result.Recognized.Single(r => r.Page == 2).SkewDegrees != 0, "Small skew was measured and corrected for recognition");
        for (var i = 0; i < 3; i++)
            Check((await session.RenderAsync(i, 306, 396)).GetProperty("Bgra").GetString() == render[i], $"PDFium page {i + 1} pixels unchanged by OCR");
        Check(RecoveryStore.Fingerprint(input) == inputHash, "Source file bytes unchanged");

        var saved = Path.Combine(work, "searchable.pdf");
        await session.SaveAsync(saved);
        var after = Pypdf(saved);
        Check(after[0].GetProperty("text").GetString() == original[0].GetProperty("text").GetString() &&
              after[0].GetProperty("contents").GetString() == original[0].GetProperty("contents").GetString(), "Text page text and content are unchanged (pypdf)");
        foreach (var i in new[] { 1, 2 })
        {
            var text = after[i].GetProperty("text").GetString()!;
            Check(text.Contains("Invoice number 12345") || (text.Contains("Invoice") && text.Contains("12345")), $"pypdf extracts recognized words from scanned page {i + 1}");
        }
        using (var reopened = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "reopen")), saved))
        {
            Check((await reopened.ExtractAsync(2)).GetProperty("Text").GetString()!.Contains("Invoice"), "Saved document reopens with searchable scan page");
            Check(reopened.PageCount == 3, "Reopened page count is unchanged");
        }

        await session.UndoAsync();
        Check(string.IsNullOrWhiteSpace((await session.ExtractAsync(1)).GetProperty("Text").GetString()) &&
              string.IsNullOrWhiteSpace((await session.ExtractAsync(2)).GetProperty("Text").GetString()), "One Undo removes all recognized text");
        await session.RedoAsync();
        Check((await session.ExtractAsync(1)).GetProperty("Text").GetString()!.Length > 10 && (await session.ExtractAsync(2)).GetProperty("Text").GetString()!.Length > 10, "Redo restores recognized text on both pages");
        await session.UndoAsync();

        // Explicit page list including a page that already has text reports it as skipped.
        var explicitResult = await session.RecognizePagesAsync(engine, new OcrPagesOptions([0, 2], ["eng"], [tessdata], AutoRotate: false), session.Revision);
        Check(explicitResult.SkippedHasText.SequenceEqual([0]) && explicitResult.Recognized.Single().Page == 2 && explicitResult.OrientationMethod == "off",
            "Pages that already have text are skipped and reported");
        await session.UndoAsync();
        await Rejected<InvalidOperationException>(() => session.RecognizePagesAsync(engine, new OcrPagesOptions([0], ["eng"], [tessdata]), session.Revision),
            "A run with nothing to recognize is refused without a checkpoint");

        await Rejected<ArgumentException>(() => session.RecognizePagesAsync(engine, new OcrPagesOptions(null, ["xyz"], [tessdata]), session.Revision), "Unknown language refused");
        await Rejected<ArgumentException>(() => session.RecognizePagesAsync(engine, new OcrPagesOptions(null, ["deu"], [tessdata]), session.Revision), "Language that is not installed refused");
        await Rejected<ArgumentOutOfRangeException>(() => session.RecognizePagesAsync(engine, new OcrPagesOptions([9], ["eng"], [tessdata]), session.Revision), "Out-of-range page refused");
        await Rejected<InvalidOperationException>(() => session.RecognizePagesAsync(engine, options, revision - 1), "Stale revision refused");

        // Catalog: pinned list, consent and integrity.
        Check(TessdataCatalog.Languages.Count >= 7 && TessdataCatalog.Languages.All(l => l.Url.StartsWith("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/" + TessdataCatalog.SourceCommit + "/") && l.Sha256.Length == 64),
            "Catalog pins official URLs at a fixed commit with SHA-256 hashes");
        Check(TessdataCatalog.Installed([tessdata]).Any(l => l.Code == "eng" && l.Verified), "Bundled English pack matches its pinned hash");
        var deu = TessdataCatalog.Languages.Single(l => l.Code == "deu");
        var target = Path.Combine(work, "downloads");
        await Rejected<InvalidOperationException>(() => TessdataCatalog.DownloadAsync(deu, consent: false, folder: target), "Download without consent refused");
        Check(!Directory.Exists(target), "Refused download touches nothing");
        var tampered = new byte[deu.SizeBytes]; new Random(1).NextBytes(tampered);
        await Rejected<IOException>(() => TessdataCatalog.DownloadAsync(deu, true, folder: target, handler: new FakeHandler(HttpStatusCode.OK, tampered)), "Download with wrong hash refused");
        Check(!Directory.EnumerateFileSystemEntries(target).Any(), "Failed download leaves no file behind");
        await Rejected<IOException>(() => TessdataCatalog.DownloadAsync(deu, true, folder: target, handler: new FakeHandler(HttpStatusCode.Redirect, [])), "Redirected download refused");

        session.Dispose();
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} OCR pages checks. Evidence: {work}");
    }

    sealed class SyncProgress(Action<(int done, int total)> action) : IProgress<(int done, int total)>
    {
        public void Report((int done, int total) value) => action(value);
    }

    sealed class FakeHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });
    }
}
