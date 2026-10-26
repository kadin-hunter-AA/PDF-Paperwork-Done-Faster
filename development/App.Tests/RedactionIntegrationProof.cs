using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class RedactionIntegrationProof
{
    internal static async Task RunAsync(string root, string output)
    {
        var work = Path.Combine(output, "redaction-integration"); Directory.CreateDirectory(work);
        var python = Path.Combine(root, "tools", "advanced-python-3.14.8", "python.exe");
        var engine = new AdvancedEngineLocation(python, Path.Combine(root, "App.Advanced", "worker.py"));
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var source = Path.Combine(work, "source.pdf");
        var factory = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-I", "-B", "-c",
            "import sys; from pathlib import Path; sys.path.insert(0,str(Path(sys.argv[1])/'RedactionProof')); import proof; proof.make_primary_source(Path(sys.argv[2]))", root, source }) factory.ArgumentList.Add(arg);
        using (var process = Process.Start(factory)!)
        {
            _ = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new Exception("Redaction fixture generation failed: " + error);
        }
        var sourceHash = RecoveryStore.Fingerprint(source);
        var checks = new List<string>();
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); checks.Add(message); }
        async Task Reject<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); } catch (T) { checks.Add(message); return; } throw new Exception(message);
        }
        using var session = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "recovery")), source);
        var revision = session.Revision;
        var regions = new[] { new RedactionRegion(0, 65, 95, 350, 130), new RedactionRegion(0, 65, 200, 400, 230),
            new RedactionRegion(0, 100, 320, 130, 370), new RedactionRegion(0, 210, 300, 260, 340) };
        using var preview = await session.PreviewRedactionAsync(engine, regions, 0, 612, 792, revision);
        Check(!session.IsDirty && session.Revision == revision && !session.CanUndo, "Redaction preview leaves edit and save history unchanged");
        Check(preview.Render.GetProperty("Bgra").GetString()!.Length > 0, "Preview renders the actual validated candidate using PDFium");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Creating a preview preserves the source bytes");
        using (var inspected = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "candidate-recovery")), preview.Candidate))
        {
            var text = (await inspected.ExtractAsync(0)).GetProperty("Text").GetString()!;
            Check(!text.Contains("REDACTION_TARGET_ALPHA") && !text.Contains("HIDDEN_OCR_TARGET_GAMMA") && text.Contains("PUBLIC_AFTER_MARKER"),
                "Native extraction confirms removed visible and hidden text and preserved unrelated text");
        }
        var destination = Path.Combine(work, "redacted.pdf");
        var result = await session.PublishRedactedCopyAsync(preview, destination);
        Check(result.MetadataRemoved && RecoveryStore.Fingerprint(destination) == preview.CandidateHash,
            "Export publishes the exact reviewed candidate with stripped metadata");
        Check(!session.IsDirty && session.Revision == revision && RecoveryStore.Fingerprint(source) == sourceHash,
            "Redaction export leaves the original and open session unchanged");
        await Reject<IOException>(() => session.PublishRedactedCopyAsync(preview, destination), "Existing output is refused");
        await Reject<IOException>(() => session.PublishRedactedCopyAsync(preview, source), "Original source path is refused");
        var racing = Path.Combine(work, "racing.pdf");
        session.RedactionPublishProbe = _ => File.WriteAllText(racing, "competing file");
        try { await Reject<IOException>(() => session.PublishRedactedCopyAsync(preview, racing), "Late destination collision prevents replacement"); }
        finally { session.RedactionPublishProbe = null; }
        Check(File.ReadAllText(racing) == "competing file", "Late competing destination bytes are preserved");
        var tampered = Path.Combine(work, "tampered.pdf");
        session.RedactionPublishProbe = path => File.WriteAllText(path, "tampered candidate");
        try { await Reject<IOException>(() => session.PublishRedactedCopyAsync(preview, tampered), "Candidate tampering before publication is refused"); }
        finally { session.RedactionPublishProbe = null; }
        Check(!File.Exists(tampered), "Tampered candidate is never published");
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = Path.Combine(work, "cancelled.pdf");
            session.RedactionPublishProbe = _ => cancel.Cancel();
            try { await Reject<OperationCanceledException>(() => session.PublishRedactedCopyAsync(preview, cancelled, cancel.Token), "Cancellation after validation prevents publication"); }
            finally { session.RedactionPublishProbe = null; }
            Check(!File.Exists(cancelled), "Cancelled export publishes no output");
        }
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await Reject<OperationCanceledException>(() => session.PreviewRedactionAsync(engine, regions, 0, 612, 792, revision, cancel.Token),
                "Pre-cancelled preview is refused");
        }
        using (var other = await DocumentSession.OpenAsync(worker, new RecoveryStore(Path.Combine(work, "other-recovery")), source))
            await Reject<InvalidOperationException>(() => other.PublishRedactedCopyAsync(preview, Path.Combine(work, "other.pdf")), "Another session cannot publish this preview");
        await session.AddTextAsync(0, "NEW REVISION", 72, 500);
        await Reject<InvalidOperationException>(() => session.PublishRedactedCopyAsync(preview, Path.Combine(work, "stale.pdf")), "A document edit invalidates the reviewed redaction preview");
        preview.Dispose();
        Check(!File.Exists(preview.Candidate), "Disposing a preview removes its private candidate");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "All redaction refusal and cancellation paths preserve the original");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} redaction integration checks. Evidence: {work}");
    }
}
