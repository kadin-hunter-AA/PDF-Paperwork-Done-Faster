using System.Diagnostics;
using System.Text.Json;
using HunterPdf.Core;

static class SessionProof
{
    public static async Task AbandonAsync(string root, string recovery, string marker)
    {
        var store = new RecoveryStore(recovery);
        var session = await DocumentSession.OpenAsync(Worker(root), store, Source(root));
        await session.AddTextAsync(0, "ABNORMAL EXIT RECOVERY", 72, 180);
        File.WriteAllText(marker, session.Id.ToString("N"));
        Environment.Exit(71); // Deliberately bypass all disposal and clean-close logic.
    }

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
        async Task Rejected<T>(Func<Task> action, string name) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(name); return; }
            throw new Exception(name);
        }
        string Hash(string path) => RecoveryStore.Fingerprint(path);
        var work = Path.Combine(output, "sessions");
        Directory.CreateDirectory(work);
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var source = Path.Combine(work, "source.pdf");
        File.Copy(Source(root), source);
        var sourceHash = Hash(source);
        var worker = Worker(root);
        using var session = await DocumentSession.OpenAsync(worker, store, source);
        var originalText = (await session.ExtractAsync(0)).GetProperty("Text").GetString();
        Check(!session.IsDirty && session.Revision == 0 && !session.CanUndo, "Open establishes clean immutable baseline");
        Check(store.Scan().Entries.Count == 0, "Live sessions excluded from recovery prompts");
        await Rejected<IOException>(() => DocumentSession.RecoverAsync(worker, store, session.Id), "Recovery cannot take over a live session");
        await session.AddTextAsync(0, "SESSION EDIT ONE", 72, 120);
        Check(session.IsDirty && session.Revision == 1 && session.CanUndo, "Committed edit updates dirty state and revision");
        var edited = (await session.ExtractAsync(0)).GetProperty("Text").GetString()!;
        Check(edited.Contains("SESSION EDIT ONE"), "Edit visible through current session");
        await session.UndoAsync();
        Check(!session.IsDirty && session.CanRedo && session.Revision == 2, "Undo to initial baseline clears dirty state");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString() == originalText, "Undo restores actual PDF text");
        await session.RedoAsync();
        Check(session.IsDirty && (await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("SESSION EDIT ONE"), "Redo restores actual PDF text");
        var firstSave = Path.Combine(work, "save-as.pdf");
        await session.SaveAsync(firstSave);
        Check(!session.IsDirty && session.LastSavePath == firstSave && Hash(source) == sourceHash, "Save As clears dirty state and preserves original");
        using (var reopened = await DocumentSession.OpenAsync(worker, store, firstSave))
        {
            Check((await reopened.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("SESSION EDIT ONE"), "Saved PDF independently reopens with edit");
            await reopened.CloseAsync();
        }
        await session.UndoAsync();
        Check(session.IsDirty, "Undo away from saved checkpoint is dirty");
        await session.RedoAsync();
        Check(!session.IsDirty, "Redo back to saved checkpoint is clean");
        var rotationBefore = (await session.ExtractAsync(0)).GetProperty("Rotation").GetInt32();
        await session.RotatePageAsync(0, (rotationBefore + 1) % 4);
        Check((await session.ExtractAsync(0)).GetProperty("Rotation").GetInt32() != rotationBefore, "Page rotation checkpoint changes actual page metadata");
        await session.UndoAsync();
        Check((await session.ExtractAsync(0)).GetProperty("Rotation").GetInt32() == rotationBefore, "Undo restores page metadata from full checkpoint");
        await session.AddTextAsync(0, "BRANCH EDIT", 72, 150);
        Check(!session.CanRedo, "New edit after undo removes redo branch");
        await Rejected<InvalidOperationException>(() => session.CloseAsync(), "Dirty close requires explicit save or discard");
        await Rejected<IOException>(() => session.SaveAsync(firstSave), "Existing destination requires explicit overwrite");
        var previousDestination = Hash(firstSave);
        var overwrite = await session.SaveAsync(firstSave, overwrite: true);
        Check(overwrite.Backup is not null && Hash(overwrite.Backup) == previousDestination, "Atomic replacement retains the previous PDF backup");
        Check(!session.IsDirty && Hash(firstSave) != previousDestination, "Successful overwrite publishes current checkpoint");

        File.AppendAllText(firstSave, "\n% external save\n");
        var externalHash = Hash(firstSave);
        await Rejected<DocumentConflictException>(() => session.SaveAsync(firstSave, true), "Externally modified saved destination is rejected");
        Check(Hash(firstSave) == externalHash, "External destination survives rejected overwrite");
        File.AppendAllText(source, "\n% external source\n");
        var externalSourceHash = Hash(source);
        await Rejected<DocumentConflictException>(() => session.SaveAsync(source, true), "Externally modified original is rejected");
        Check(Hash(source) == externalSourceHash, "External original survives rejected overwrite");

        var target = Path.Combine(work, "failure-target.pdf");
        File.Copy(Source(root), target);
        var targetHash = Hash(target);
        await session.AddTextAsync(0, "UNSAVED FAILURE CASE", 72, 210);
        session.SaveProbe = (stage, candidate) =>
        {
            if (stage == SaveStage.CandidateCreated) File.WriteAllText(candidate, "not a PDF");
        };
        await Rejected<InvalidOperationException>(() => session.SaveAsync(target, true), "Malformed candidate is rejected before publication");
        Check(Hash(target) == targetHash && session.IsDirty, "Validation failure preserves target and dirty state");
        session.SaveProbe = (stage, _) =>
        {
            if (stage != SaveStage.CandidateCreated) return;
            using var process = Process.GetProcessById(session.WorkerProcessId!.Value);
            process.Kill(); process.WaitForExit();
        };
        await Rejected<WorkerUnavailableException>(() => session.SaveAsync(target, true), "Worker failure during save is surfaced");
        Check(Hash(target) == targetHash && session.IsDirty, "Worker save failure preserves target and unsaved checkpoint");
        Check((await session.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("UNSAVED FAILURE CASE"), "Worker restart restores latest committed unsaved state");
        session.SaveProbe = (stage, _) =>
        {
            if (stage == SaveStage.BeforePublish) throw new IOException("Simulated publication failure");
        };
        await Rejected<IOException>(() => session.SaveAsync(target, true), "Publication failure does not overwrite target");
        Check(Hash(target) == targetHash, "Failed publication retains previous valid copy");
        session.SaveProbe = (stage, candidate) =>
        {
            if (stage == SaveStage.BeforePublish) File.AppendAllText(candidate, "\n% late corruption\n");
        };
        await Rejected<IOException>(() => session.SaveAsync(target, true), "Candidate mutation after validation is rejected");
        Check(Hash(target) == targetHash, "Post-validation candidate corruption cannot reach target");

        FileStream? destinationLock = null;
        session.SaveProbe = (stage, _) =>
        {
            if (stage == SaveStage.BeforePublish)
                destinationLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try { await Rejected<IOException>(() => session.SaveAsync(target, true), "Destination lock rejects atomic replacement"); }
        finally { destinationLock?.Dispose(); }
        Check(Hash(target) == targetHash, "Locked destination survives failed save");
        session.SaveProbe = null;
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        try { await Rejected<UnauthorizedAccessException>(() => session.SaveAsync(target, true), "Read-only destination is rejected"); }
        finally { File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly); }
        Check(Hash(target) == targetHash, "Read-only destination remains intact");
        var missingFolder = Path.Combine(work, "absent", "output.pdf");
        await Rejected<DirectoryNotFoundException>(() => session.SaveAsync(missingFolder), "Unavailable save folder fails without losing state");
        Check(session.IsDirty, "Failed writes leave session dirty");
        Check(Directory.GetFiles(work, "*.candidate.pdf").Length == 0, "Failed-save temporary candidates are cleaned up");

        var late = Path.Combine(work, "late-destination.pdf");
        session.SaveProbe = (stage, _) =>
        {
            if (stage == SaveStage.BeforePublish) File.Copy(Source(root), late);
        };
        await Rejected<DocumentConflictException>(() => session.SaveAsync(late), "Destination appearing during Save As is rejected");
        Check(Hash(late) == Hash(Source(root)), "Competing Save As file survives intact");
        session.SaveProbe = null;
        var revisionBeforeFailure = session.Revision;
        var textBeforeFailure = (await session.ExtractAsync(0)).GetProperty("Text").GetString();
        store.BeforeManifestCommit = () => throw new IOException("Simulated recovery write failure");
        await Rejected<IOException>(() => session.AddTextAsync(0, "MUST ROLL BACK", 72, 260), "Checkpoint commit failure rejects the edit");
        store.BeforeManifestCommit = null;
        Check(session.Revision == revisionBeforeFailure &&
            (await session.ExtractAsync(0)).GetProperty("Text").GetString() == textBeforeFailure, "Uncommitted native edit rolls back to last durable state");
        store.BeforeManifestCommit = () => throw new IOException("Simulated recovery write failure");
        await Rejected<IOException>(() => session.UndoAsync(), "Failed undo checkpoint commit is surfaced");
        store.BeforeManifestCommit = null;
        Check(session.Revision == revisionBeforeFailure &&
            (await session.ExtractAsync(0)).GetProperty("Text").GetString() == textBeforeFailure, "Failed undo retains current document state");

        var completed = Path.Combine(work, "saved-before-manifest-failure.pdf");
        store.BeforeManifestCommit = () => throw new IOException("Simulated recovery write failure");
        await Rejected<SaveCompletedException>(() => session.SaveAsync(completed), "Save success with recovery failure is reported distinctly");
        store.BeforeManifestCommit = null;
        Check(File.Exists(completed) && !session.IsDirty, "Published save is not misreported as unsaved");
        await session.CloseAsync();
        Check(!Directory.Exists(store.DirectoryFor(session.Id)), "Clean close removes only its recovery directory");

        // Real abrupt parent exit: no session Dispose or CloseAsync.
        var marker = Path.Combine(work, "abandoned-session.txt");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(SessionProof).Assembly.Location);
        foreach (var arg in new[] { "--abandon-session", root, store.Root, marker }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            await process.WaitForExitAsync();
            Check(process.ExitCode == 71, "Abnormal exit test bypasses all clean-close logic");
        }
        var abandonedId = Guid.Parse(File.ReadAllText(marker));
        var scan = store.Scan();
        Check(scan.Entries.Any(e => e.Id == abandonedId && e.IsDirty), "Startup discovers durable edits after abnormal termination");
        using (var restored = await DocumentSession.RecoverAsync(worker, store, abandonedId))
        {
            Check((await restored.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("ABNORMAL EXIT RECOVERY"), "Recovery restores actual unsaved PDF content");
            await restored.UndoAsync();
            Check(!restored.IsDirty, "Recovery preserves undo history and saved baseline");
            await restored.RedoAsync();
            Check(restored.IsDirty, "Recovery preserves redo history");
            var recoveredSave = Path.Combine(work, "recovered-save.pdf");
            await restored.SaveAsync(recoveredSave);
            await restored.CloseAsync();
        }

        var signed = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        var signedHash = Hash(signed);
        using (var signedSession = await DocumentSession.OpenAsync(worker, store, signed))
        {
            Check(signedSession.SignatureCount > 0 && !signedSession.SignaturesInvalidated, "Signed PDF detected without claiming signature validity");
            await Rejected<SignedDocumentException>(() => signedSession.AddTextAsync(0, "SIGNED EDIT", 72, 180), "Signed edit requires explicit copy consent");
            await signedSession.AuthorizeSignedCopyAsync();
            await signedSession.AddTextAsync(0, "SIGNED EDIT", 72, 180);
            Check(signedSession.SignaturesInvalidated, "Edited signed copy is explicitly marked invalidated");
            await Rejected<IOException>(() => signedSession.SaveAsync(signed, true), "Signed original cannot be overwritten");
            await signedSession.SaveAsync(Path.Combine(work, "signed-edited-copy.pdf"));
            Check(Hash(signed) == signedHash, "Signed original bytes remain unchanged");
            await signedSession.CloseAsync();
        }

        var encrypted = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "password-aes256.pdf");
        Guid encryptedId;
        using (var encryptedSession = await DocumentSession.OpenAsync(worker, store, encrypted, "corpus-user"))
        {
            await encryptedSession.AddTextAsync(0, "ENCRYPTED RECOVERY", 72, 180);
            encryptedId = encryptedSession.Id;
        }
        var manifest = File.ReadAllText(Path.Combine(store.DirectoryFor(encryptedId), "session.json"));
        Check(!manifest.Contains("corpus-user"), "Recovery metadata does not persist PDF passwords");
        await Rejected<InvalidOperationException>(() => DocumentSession.RecoverAsync(worker, store, encryptedId), "Encrypted recovery still requires the password");
        using (var recoveredEncrypted = await DocumentSession.RecoverAsync(worker, store, encryptedId, "corpus-user"))
        {
            Check((await recoveredEncrypted.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("ENCRYPTED RECOVERY"), "Password-protected edited checkpoint recovers with supplied credentials");
            await recoveredEncrypted.CloseAsync(discardUnsaved: true);
        }

        Guid corruptId;
        using (var corruptSession = await DocumentSession.OpenAsync(worker, store, Source(root)))
        {
            await corruptSession.AddTextAsync(0, "INTEGRITY CASE", 72, 180);
            corruptId = corruptSession.Id;
        }
        var corruptRecord = store.Read(corruptId);
        File.AppendAllText(store.SnapshotPath(corruptId, corruptRecord.History[corruptRecord.Index].FileName), "\n% damage\n");
        await Rejected<IOException>(() => DocumentSession.RecoverAsync(worker, store, corruptId), "Corrupt recovery checkpoint rejected by hash");
        Check(store.Scan().Errors.Count > 0 && Directory.Exists(store.DirectoryFor(corruptId)), "Unreadable recovery is reported and retained");
        store.Remove(corruptId);
        // Invalid metadata must not break startup or resolve checkpoint paths outside recovery.
        Guid metadataId;
        using (var metadataSession = await DocumentSession.OpenAsync(worker, store, Source(root))) metadataId = metadataSession.Id;
        var metadata = store.Read(metadataId);
        var invalid = metadata with { History = [new Checkpoint("../source.pdf", metadata.History[0].Sha256, "Open")] };
        store.Write(invalid);
        await Rejected<IOException>(() => DocumentSession.RecoverAsync(worker, store, metadataId), "Recovery rejects checkpoint path traversal");
        Check(store.Scan().Errors.Count > 0, "Invalid checkpoint paths produce startup recovery errors");
        invalid = metadata with { History = [null!] };
        store.Write(invalid);
        await Rejected<IOException>(() => DocumentSession.RecoverAsync(worker, store, metadataId), "Null recovery checkpoint is rejected safely");
        Check(store.Scan().Errors.Count > 0, "Malformed recovery metadata cannot crash the startup scan");
        store.Remove(metadataId);

        // Explicit source overwrite updates conflict tracking while retaining each previous valid version.
        var ownSource = Path.Combine(work, "explicit-overwrite.pdf");
        File.Copy(Source(root), ownSource);
        using (var own = await DocumentSession.OpenAsync(worker, store, ownSource))
        {
            await own.AddTextAsync(0, "FIRST EXPLICIT OVERWRITE", 72, 180);
            var first = await own.SaveAsync(ownSource, true);
            Check(first.Backup is not null && Hash(first.Backup) == Hash(Source(root)), "Explicit original overwrite retains original bytes in backup");
            await own.AddTextAsync(0, "SECOND EXPLICIT OVERWRITE", 72, 210);
            await own.SaveAsync(ownSource, true);
            Check(!own.IsDirty, "Own source saves update fingerprint for later overwrites");
            await own.CloseAsync();
        }
        var removable = Path.Combine(work, "removed-source.pdf");
        File.Copy(Source(root), removable);
        Guid removedId;
        using (var removed = await DocumentSession.OpenAsync(worker, store, removable))
        {
            await removed.AddTextAsync(0, "SOURCE CAN DISAPPEAR", 72, 180);
            removedId = removed.Id;
        }
        File.Delete(removable);
        using (var recoveredRemoved = await DocumentSession.RecoverAsync(worker, store, removedId))
        {
            Check((await recoveredRemoved.ExtractAsync(0)).GetProperty("Text").GetString()!.Contains("SOURCE CAN DISAPPEAR"),
                "Recovery works when original source is missing");
            await recoveredRemoved.SaveAsync(Path.Combine(work, "missing-source-recovered.pdf"));
            await recoveredRemoved.CloseAsync();
        }
        File.WriteAllText(Path.Combine(output, "session-results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} document-state/save/recovery checks. Evidence: {output}");
    }
}
