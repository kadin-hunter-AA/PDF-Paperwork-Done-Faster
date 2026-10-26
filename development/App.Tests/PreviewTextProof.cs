using HunterPdf.Core;

static class PreviewTextProof
{
    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        async Task Rejected<T>(Func<Task> action, string name, string? messagePart = null) where T : Exception
        {
            try { await action(); }
            catch (T e)
            {
                if (messagePart is not null && !e.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase)) throw;
                checks.Add(name); return;
            }
            throw new Exception(name);
        }

        var work = Path.Combine(output, "text-preview"); Directory.CreateDirectory(work);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var worker = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        using var session = await DocumentSession.OpenAsync(worker, store, source);
        var first = await session.ExtractAsync(0);
        var oldText = first.GetProperty("Objects").EnumerateArray().First(x => x.GetProperty("Type").GetInt32() == 1);
        var oldId = oldText.GetProperty("Id").GetString()!;
        var beforeRevision = session.Revision;
        var originalRender = await session.RenderAsync(0, 612, 792);
        var originalPixels = originalRender.GetProperty("Bgra").GetString();
        var candidate = await session.PreviewTextAsync(0, oldId, "Preview page one", 612, 792);
        Check(candidate.GetProperty("Bgra").GetString() != originalPixels, "Preview render shows the proposed text change");
        Check(session.Revision == beforeRevision && !session.IsDirty && !session.CanUndo, "Preview leaves session revision, dirty state, and undo history unchanged");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString() == first.GetProperty("Text").GetString(), "Preview leaves live document extraction unchanged");

        await session.ReplaceTextAsync(0, oldId, "Committed page one");
        var committed = await session.ExtractAsync(0);
        var committedText = committed.GetProperty("Text").GetString()!;
        Check(committedText.Contains("Committed page one", StringComparison.Ordinal), "Unsaved committed edit is present before preview");
        var currentText = committed.GetProperty("Objects").EnumerateArray().First(x => x.GetProperty("Type").GetInt32() == 1);
        var currentId = currentText.GetProperty("Id").GetString()!;
        var currentRevision = session.Revision;
        var committedPixels = (await session.RenderAsync(0, 612, 792)).GetProperty("Bgra").GetString();
        var proposed = await session.PreviewTextAsync(0, currentId, "Candidate page one", 612, 792);
        Check(proposed.GetProperty("Bgra").GetString() != committedPixels, "Preview applies proposed text over the current unsaved checkpoint");
        Check(session.Revision == currentRevision && session.IsDirty && session.CanUndo, "Preview leaves committed checkpoint and undo availability intact");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString() == committedText, "Preview candidate does not replace live committed text");

        using (var activeCancellation = new CancellationTokenSource())
        {
            var activePreview = session.PreviewTextAsync(0, currentId, "Canceled candidate", 612, 792, activeCancellation.Token);
            activeCancellation.Cancel();
            await Rejected<OperationCanceledException>(() => activePreview, "Cancellation during an active preview is surfaced");
        }
        var afterCancellation = await session.ExtractAsync(0);
        Check(afterCancellation.GetProperty("Text").GetString() == committedText && session.Revision == currentRevision,
            "Active preview cancellation preserves the live worker document and revision");
        var afterCancellationId = afterCancellation.GetProperty("Objects").EnumerateArray().First(x => x.GetProperty("Type").GetInt32() == 1).GetProperty("Id").GetString()!;
        Check(afterCancellationId == currentId &&
              (await session.PreviewTextAsync(0, currentId, "After cancellation", 612, 792)).GetProperty("Bgra").GetString() != committedPixels,
            "The current text object remains usable after isolated preview cancellation");

        await Rejected<InvalidOperationException>(() => session.PreviewTextAsync(0, oldId, "Stale", 612, 792), "Stale object ID rejected", "stale");
        await Rejected<InvalidOperationException>(() => session.PreviewTextAsync(0, currentId, new string('W', 1000), 612, 792), "Overflowing preview rejected", "extends beyond the page");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Rejected<OperationCanceledException>(() => session.PreviewTextAsync(0, currentId, "Canceled", 612, 792, canceled.Token), "Canceled preview is surfaced");
        }
        Check(session.Revision == currentRevision && (await session.ExtractAsync(0)).GetProperty("Text").GetString() == committedText,
            "Failed and canceled previews preserve the current checkpoint");
        Check(RecoveryStore.Fingerprint(source) == sourceHash, "Text preview leaves original PDF bytes unchanged");

        File.WriteAllText(Path.Combine(work, "results.json"), System.Text.Json.JsonSerializer.Serialize(
            new { Passed = checks }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} text preview checks. Evidence: {work}");
    }
}
