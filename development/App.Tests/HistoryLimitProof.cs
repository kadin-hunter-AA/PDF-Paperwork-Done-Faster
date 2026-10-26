using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class HistoryLimitProof
{
    static string Worker(string root) => Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
    static string Source(string root) => Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");

    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks.Add(name);
        }
        var work = Path.Combine(output, "history-limit");
        Directory.CreateDirectory(work);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var worker = Worker(root);
        var sourceHash = RecoveryStore.Fingerprint(Source(root));
        var seq = 0;
        async Task<string> Text(DocumentSession s) => (await s.ExtractAsync(0)).GetProperty("Text").GetString()!;
        async Task<string> Render(DocumentSession s) => (await s.RenderAsync(0, 306, 396)).GetProperty("Bgra").GetString()!;
        async Task Edit(DocumentSession s, string label) => await s.AddTextAsync(0, label, 72, 120 + (seq++ % 40) * 14);
        string Dir(DocumentSession s) => Path.Combine(store.Root, s.Id.ToString("N"));
        string[] OnDisk(DocumentSession s) => Directory.GetFiles(Dir(s), "*.pdf").Select(f => Path.GetFileName(f)!).Order().ToArray();
        string[] Referenced(DocumentSession s) => store.Read(s.Id).History.Select(c => c.FileName).Order().ToArray();
        bool NoLeaks(DocumentSession s) => OnDisk(s).SequenceEqual(Referenced(s));

        // Timeout formula.
        var mib = 1024L * 1024;
        Check(WorkerTimeouts.Scaled(0, 0) == TimeSpan.FromSeconds(30), "Timeout: empty document uses the 30 s base");
        Check(WorkerTimeouts.Scaled(10 * mib, 0) == TimeSpan.FromSeconds(45), "Timeout: 10 MB adds 15 s");
        Check(WorkerTimeouts.Scaled(0, 20) == TimeSpan.FromSeconds(31), "Timeout: 20 pages add 1 s");
        Check(WorkerTimeouts.Scaled(10 * mib, 200) == TimeSpan.FromSeconds(55), "Timeout: size and pages combine");
        Check(WorkerTimeouts.Scaled(long.MaxValue / 4, int.MaxValue) == TimeSpan.FromMinutes(10), "Timeout: capped at 10 minutes");
        Check(WorkerTimeouts.Scaled(-5, -5) == TimeSpan.FromSeconds(30), "Timeout: negative inputs clamp to base");
        Check(WorkerTimeouts.ForOperation("Render", 500 * mib, 5000) == TimeSpan.FromSeconds(30) &&
              WorkerTimeouts.ForOperation("Extract", 500 * mib, 5000) == TimeSpan.FromSeconds(30) &&
              WorkerTimeouts.ForOperation("PageInfo", 500 * mib, 5000) == TimeSpan.FromSeconds(30), "Timeout: Render, Extract and PageInfo stay at 30 s");
        Check(WorkerTimeouts.ForOperation("AddText", 10 * mib, 0) == TimeSpan.FromSeconds(45) &&
              WorkerTimeouts.ForOperation("SaveCandidate", 10 * mib, 0) == TimeSpan.FromSeconds(45), "Timeout: edit and save operations scale");
        Check(AdvancedEngineClient.ScaledTimeout(10 * mib, 200) == WorkerTimeouts.Scaled(10 * mib, 200) &&
              AdvancedEngineClient.DefaultTimeout == TimeSpan.FromMinutes(3), "Timeout: advanced client keeps 3 min default and exposes the scaling helper");
        var selfArguments = string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? "\"" + typeof(HistoryLimitProof).Assembly.Location + "\" --stall" : "--stall";
        using (var stalled = new WorkerClient(Environment.ProcessPath!, selfArguments))
        {
            stalled.Start();
            var watch = Stopwatch.StartNew();
            try { await stalled.SendAsync(Wire.Create(Guid.NewGuid(), 0, "Extract", new { Page = 0 }), default, TimeSpan.FromMilliseconds(400)); throw new Exception("Per-request timeout ignored"); }
            catch (OperationCanceledException) { Check(watch.Elapsed < TimeSpan.FromSeconds(10) && stalled.ProcessId is null, "Per-request timeout stops a stalled worker"); }
            try { await stalled.SendAsync(Wire.Create(Guid.NewGuid(), 0, "Extract", new { Page = 0 }), default, TimeSpan.Zero); throw new Exception("Zero timeout accepted"); }
            catch (ArgumentOutOfRangeException) { checks.Add("Per-request timeout rejects zero"); }
        }

        // Part A: MaxSteps bound, no leaks, undo/redo across pruned history.
        var source = Path.Combine(work, "source.pdf");
        File.Copy(Source(root), source);
        string originalText;
        using (var s = await DocumentSession.OpenAsync(worker, store, source))
        {
            s.HistoryPolicy = new HistoryPolicy(maxSteps: 5);
            originalText = await Text(s);
            var originalRender = await Render(s);
            var openedFile = Referenced(s)[0];
            var maxCount = 0;
            for (var i = 1; i <= 12; i++)
            {
                await Edit(s, $"HIST{i:00}");
                maxCount = Math.Max(maxCount, s.HistoryCount);
                if (!NoLeaks(s)) throw new Exception("Snapshot files differ from the referenced set after edit " + i);
            }
            Check(maxCount <= 6 && s.HistoryCount == 6, "History count stays bounded by MaxSteps (opened + 5)");
            Check(NoLeaks(s), "Snapshot files on disk equal the referenced set (no leaks)");
            var diskBytes = OnDisk(s).Sum(n => new FileInfo(Path.Combine(Dir(s), n)).Length);
            Check(s.HistoryBytes == diskBytes, "HistoryBytes equals the size of the referenced snapshots");
            Check(store.Read(s.Id).History[0].FileName == openedFile, "Opened checkpoint is retained as History[0]");
            var steps = 0;
            string? beforeLast = null;
            while (s.CanUndo)
            {
                await s.UndoAsync();
                steps++;
                if (steps == 1) beforeLast = await Text(s);
            }
            Check(steps == 5, "Undo walks exactly the kept steps");
            Check(beforeLast!.Contains("HIST11") && !beforeLast.Contains("HIST12"), "First Undo reaches the previous kept checkpoint");
            var undone = await Text(s);
            Check(undone == originalText && !undone.Contains("HIST") && await Render(s) == originalRender && !s.IsDirty,
                "Undo to the oldest kept state restores the opened text and render");
            while (s.CanRedo) await s.RedoAsync();
            var redone = await Text(s);
            Check(redone.Contains("HIST12") && redone.Contains("HIST08") && s.IsDirty, "Redo restores the newest edit with all earlier edits (content is cumulative)");

            // Part B: a new edit after undo discards the redo branch files.
            await s.UndoAsync();
            await s.UndoAsync();
            var rec = store.Read(s.Id);
            var redoBranch = rec.History.Skip(rec.Index + 1).Select(c => c.FileName).ToArray();
            Check(redoBranch.Length == 2 && redoBranch.All(n => File.Exists(Path.Combine(Dir(s), n))), "Redo branch snapshots exist before a new edit");
            await Edit(s, "BRANCH");
            Check(!s.CanRedo && redoBranch.All(n => !File.Exists(Path.Combine(Dir(s), n))), "New edit after undo deletes discarded redo-branch snapshots");
            Check(NoLeaks(s) && s.HistoryCount <= 6 && (await Text(s)).Contains("BRANCH"), "No leaks and bounded history after branching");

            // Part C: save protects the saved checkpoint; compaction keeps opened, saved and current.
            await s.SaveAsync(Path.Combine(work, "saved.pdf"));
            var savedName = store.Read(s.Id).SavedCheckpoint;
            for (var i = 0; i < 8; i++) await Edit(s, $"POST{i}");
            await Text(s); // The next operation reloads the worker, which releases any snapshot it still held open.
            Check(store.Read(s.Id).History.Any(c => c.FileName == savedName) && NoLeaks(s) && s.HistoryCount <= 7, "Pruning never removes the saved checkpoint");
            await s.CompactHistoryAsync();
            var compacted = store.Read(s.Id);
            Check(compacted.History.Length == 3 && compacted.History[0].FileName == openedFile && compacted.History[1].FileName == savedName &&
                  compacted.Index == 2 && s.HistoryCount == 3 && NoLeaks(s) && s.IsDirty && !s.CanRedo, "Compaction keeps opened, saved and current and removes the rest");
            await s.UndoAsync();
            var savedText = await Text(s);
            Check(!s.IsDirty && savedText.Contains("BRANCH") && !savedText.Contains("POST0"), "Undo after compaction reaches the saved checkpoint");
            await s.UndoAsync();
            Check(await Text(s) == originalText && !s.CanUndo, "Undo after compaction reaches the opened checkpoint");
            await s.RedoAsync();
            await s.RedoAsync();
            Check((await Text(s)).Contains("POST7"), "Redo after compaction reaches the current checkpoint");
            await s.SaveAsync(Path.Combine(work, "saved2.pdf"));
            await s.CompactHistoryAsync();
            Check(s.HistoryCount == 2 && NoLeaks(s) && !s.IsDirty, "Compaction of a clean document keeps opened and current (saved merged)");
            await s.UndoAsync();
            Check(await Text(s) == originalText, "Undo after clean compaction reaches the opened state");
            await s.CloseAsync(true);
        }

        // Part D: abandoned-session recovery after pruning, stray sweep, policy honored.
        Guid recoveredId;
        string expectedText;
        int countBefore;
        using (var s = await DocumentSession.OpenAsync(worker, store, source))
        {
            s.HistoryPolicy = new HistoryPolicy(maxSteps: 3);
            for (var i = 1; i <= 8; i++) await Edit(s, $"REC{i}");
            recoveredId = s.Id;
            expectedText = await Text(s);
            countBefore = s.HistoryCount;
            Check(countBefore == 4 && NoLeaks(s), "Recovery fixture: pruned history is consistent before abandoning");
            File.Copy(Source(root), Path.Combine(Dir(s), Guid.NewGuid().ToString("N") + ".pdf"));
            File.WriteAllText(Path.Combine(Dir(s), "keep-me.pdf"), "not a snapshot");
            File.WriteAllText(Path.Combine(Dir(s), "keep-me.txt"), "not a snapshot");
        }
        Check(store.Scan().Entries.Any(e => e.Id == recoveredId && e.IsDirty), "Abandoned pruned session is offered for recovery");
        using (var r = await DocumentSession.RecoverAsync(worker, store, recoveredId))
        {
            var recoveredReferenced = Referenced(r);
            Check(OnDisk(r).Where(n => n != "keep-me.pdf").SequenceEqual(recoveredReferenced) &&
                  File.Exists(Path.Combine(Dir(r), "keep-me.pdf")) && File.Exists(Path.Combine(Dir(r), "keep-me.txt")), "Recovery sweeps only stray snapshot-named files");
            Check(r.HistoryCount == countBefore && await Text(r) == expectedText && r.IsDirty, "Recovered session opens the current checkpoint with the pruned history");
            r.HistoryPolicy = new HistoryPolicy(maxSteps: 3);
            await Edit(r, "REC9");
            Check(r.HistoryCount == 4 && (await Text(r)).Contains("REC9") && OnDisk(r).Count(n => n != "keep-me.pdf") == 4, "Recovered session keeps pruning after new edits");
            while (r.CanUndo) await r.UndoAsync();
            Check(await Text(r) == originalText, "Undo chain of a recovered session reaches the opened state");
            await r.CloseAsync(true);
        }

        // Part E: MaxBytes prunes by size.
        using (var s = await DocumentSession.OpenAsync(worker, store, source))
        {
            await Edit(s, "SIZE1");
            await Edit(s, "SIZE2");
            var limit = s.HistoryBytes * 3 / 2;
            s.HistoryPolicy = new HistoryPolicy(maxBytes: limit);
            for (var i = 3; i <= 9; i++) await Edit(s, $"SIZE{i}");
            Check(s.HistoryBytes <= limit && s.HistoryCount >= 2 && s.HistoryCount < 10 && NoLeaks(s), "MaxBytes prunes the oldest checkpoints and leaves no leaks");
            while (s.CanUndo) await s.UndoAsync();
            Check(!(await Text(s)).Contains("SIZE") && !s.IsDirty, "Undo after size pruning reaches the opened state");
            await s.CloseAsync(true);
        }

        // Format compatibility: no new record fields, version unchanged.
        using (var s = await DocumentSession.OpenAsync(worker, store, source))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir(s), "session.json")));
            var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            Check(json.RootElement.GetProperty("Version").GetInt32() == 1 && names.Length == 13 && !names.Contains("HistoryPolicy"), "Recovery record format is unchanged (version 1, 13 fields)");
            var d = HistoryPolicy.Default;
            Check(d.MaxSteps == 50 && d.MaxBytes == 2L * 1024 * 1024 * 1024 && s.HistoryPolicy == d, "Default policy is 50 steps and 2 GiB");
            await s.CloseAsync(true);
        }
        Check(RecoveryStore.Fingerprint(Source(root)) == sourceHash && RecoveryStore.Fingerprint(source) == sourceHash, "Source files remain unchanged");
        Check(store.Scan().Entries.Count == 0, "No recovery entries remain after the proof");
        File.WriteAllText(Path.Combine(output, "history-limit-results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} history limit checks. Evidence: {output}");
    }
}
