using System.Security.Cryptography;
using System.Text.Json;
using HunterPdf.Core;

static class ExternalCandidateProof
{
    public static async Task RunAsync(string root, string output)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
            checks.Add(message);
        }
        async Task Rejected<T>(Func<Task> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { checks.Add(message); return; }
            throw new Exception(message);
        }

        var work = Path.Combine(output, "external-candidate");
        Directory.CreateDirectory(work);
        var source = Path.Combine(root, "fixtures", "corpus-v1", "engine", "original.pdf");
        var sourceHash = RecoveryStore.Fingerprint(source);
        var workerPath = Path.Combine(root, "App.Worker", "bin", "Debug", "net10.0", "App.Worker.exe");
        var store = new RecoveryStore(Path.Combine(work, "recovery"));
        var sourceCopy = Path.Combine(work, "input.pdf");
        File.Copy(source, sourceCopy);
        var copyHash = RecoveryStore.Fingerprint(sourceCopy);
        var session = await DocumentSession.OpenAsync(workerPath, store, sourceCopy);
        var initialRevision = session.Revision;
        var originalText = (await session.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";

        static async Task MakeCandidate(string workerPath, string input, string outputPath, string text, CancellationToken cancellation = default)
        {
            using var client = new WorkerClient(workerPath);
            client.Start();
            var id = Guid.NewGuid();
            async Task<Response> Send(string operation, long revision, object args)
            {
                var response = await client.SendAsync(Wire.Create(id, revision, operation, args), cancellation);
                if (!response.Success) throw new InvalidOperationException(response.Error);
                return response;
            }
            await Send("Open", 0, new { Path = input });
            await Send("AddText", 0, new { Page = 0, Text = text, X = 72d, Y = 120d });
            await Send("SaveCandidate", 1, new { Path = outputPath });
            await Send("ValidateCandidate", 1, new { Path = outputPath });
        }

        var transformCalls = 0;
        var marker = "EXTERNAL_CANDIDATE_DURABLE_MARKER";
        await session.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, initialRevision, async (input, candidate, token) =>
        {
            transformCalls++;
            Check(Path.GetFullPath(input) != Path.GetFullPath(session.OriginalPath), "Transform receives a disposable copy, not the selected source");
            Check(Path.GetFullPath(input) != Path.GetFullPath(candidate), "Transform input and candidate are separate paths");
            Check(RecoveryStore.Fingerprint(input) == copyHash, "Transform input begins as the exact committed checkpoint bytes");
            await MakeCandidate(workerPath, input, candidate, marker, token);
        });
        Check(transformCalls == 1, "External transform callback runs exactly once");
        Check(session.Revision == initialRevision + 1 && session.IsDirty, "Accepted candidate advances revision and marks the session dirty");
        Check(RecoveryStore.Fingerprint(source) == sourceHash && RecoveryStore.Fingerprint(sourceCopy) == copyHash,
            "Accepted candidate leaves both original and selected source bytes unchanged");
        var editedText = (await session.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";
        Check(editedText.Contains(marker, StringComparison.Ordinal) && editedText.Contains(originalText, StringComparison.Ordinal),
            "Committed checkpoint contains transformed and preserved page text");

        var idForRecovery = session.Id;
        session.Dispose();
        using (var recovered = await DocumentSession.RecoverAsync(workerPath, store, idForRecovery))
        {
            var recoveredText = (await recovered.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";
            Check(recovered.Revision == initialRevision + 1 && recovered.IsDirty && recoveredText.Contains(marker, StringComparison.Ordinal),
                "Crash-style recovery reopens the committed external candidate");
            await recovered.UndoAsync();
            var undoText = (await recovered.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";
            Check(recovered.CanRedo && !undoText.Contains(marker, StringComparison.Ordinal), "Undo restores the preceding checkpoint");
            await recovered.RedoAsync();
            var redoText = (await recovered.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";
            Check(redoText.Contains(marker, StringComparison.Ordinal), "Redo restores the external candidate");
            var saved = Path.Combine(work, "saved-candidate.pdf");
            await recovered.SaveAsync(saved);
            Check(!recovered.IsDirty && File.Exists(saved), "Saving the candidate clears the dirty state");
            using var reopened = await DocumentSession.OpenAsync(workerPath, new RecoveryStore(Path.Combine(work, "saved-recovery")), saved);
            var savedText = (await reopened.ExtractAsync(0)).GetProperty("Text").GetString() ?? "";
            Check(savedText.Contains(marker, StringComparison.Ordinal), "Saved candidate reopens with transformed content");
        }

        using var failures = await DocumentSession.OpenAsync(workerPath, new RecoveryStore(Path.Combine(work, "failure-recovery")), source);
        var baselineRevision = failures.Revision;
        var failureStore = new RecoveryStore(Path.Combine(work, "failure-recovery"));
        // The session above owns a different store instance; reading the durable record is still independent of its memory state.
        var baselineCheckpoint = ReadCurrentCheckpointHash(failureStore, failures);
        var callbackCalls = 0;
        await Rejected<InvalidOperationException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.FillForm, baselineRevision - 1,
            (_, _, _) => { callbackCalls++; return Task.CompletedTask; }), "Stale revision rejected before transform callback");
        Check(callbackCalls == 0, "Stale request never invokes external transform");

        var failureCandidate = Path.Combine(work, "callback-failure.pdf");
        await Rejected<InvalidOperationException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.FillForm, baselineRevision,
            (_, _, _) => throw new InvalidOperationException("synthetic transform failure")), "Transform failure rejects candidate");
        Check(!File.Exists(failureCandidate), "Failed transform leaves no accepted candidate");

        await Rejected<IOException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, baselineRevision,
            (input, _, _) => { File.WriteAllBytes(input, [1, 2, 3, 4]); return Task.CompletedTask; }), "Mutated transform input is rejected");
        Check(ReadCurrentCheckpointHash(failureStore, failures) == baselineCheckpoint, "Mutating disposable input cannot change authoritative history");

        var malformed = Path.Combine(work, "malformed.pdf");
        await Rejected<Exception>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, baselineRevision,
            (_, candidate, _) => { File.WriteAllText(candidate, "not a PDF"); return Task.CompletedTask; }), "Malformed candidate fails validation");
        Check(ReadCurrentCheckpointHash(failureStore, failures) == baselineCheckpoint && failures.Revision == baselineRevision,
            "Malformed candidate leaves committed state unchanged");

        var twoPage = Path.Combine(root, "fixtures", "corpus-v1", "engine", "page-source.pdf");
        await Rejected<InvalidOperationException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, baselineRevision,
            (input, candidate, token) => MakeCandidate(workerPath, twoPage, candidate, "WRONG_PAGE_COUNT", token)),
            "Valid PDF with wrong page count is rejected");

        var signedPath = Path.Combine(root, "fixtures", "corpus-v1", "edge", "signed.pdf");
        await Rejected<IOException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, baselineRevision,
            (input, candidate, _) => { File.Copy(signedPath, candidate); return Task.CompletedTask; }),
            "Candidate that introduces a signature field is rejected");

        using (var cancel = new CancellationTokenSource())
        {
            await Rejected<OperationCanceledException>(() => failures.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, baselineRevision,
                async (input, candidate, token) =>
                {
                    await MakeCandidate(workerPath, input, candidate, "CANCELED_AFTER_CANDIDATE", token);
                    cancel.Cancel();
                }, cancel.Token), "Cancellation after candidate creation prevents commit");
        }

        failures.Dispose();
        var manifestStore = new RecoveryStore(Path.Combine(work, "manifest-failure"));
        using (var manifestFailure = await DocumentSession.OpenAsync(workerPath, manifestStore, source))
        {
            var before = manifestFailure.Revision;
            var manifestHash = ReadCurrentCheckpointHash(manifestStore, manifestFailure);
            manifestStore.BeforeManifestCommit = () => throw new IOException("synthetic recovery manifest failure");
            await Rejected<IOException>(() => manifestFailure.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, before,
                (input, candidate, token) => MakeCandidate(workerPath, input, candidate, "MANIFEST_FAILURE", token)),
                "Recovery manifest failure rejects candidate commit");
            manifestStore.BeforeManifestCommit = null;
            Check(manifestFailure.Revision == before && ReadCurrentCheckpointHash(manifestStore, manifestFailure) == manifestHash,
                "Manifest failure rolls back candidate publication and revision");
        }

        using (var signed = await DocumentSession.OpenAsync(workerPath, new RecoveryStore(Path.Combine(work, "signed-recovery")), signedPath))
        {
            var called = false;
            await Rejected<InvalidOperationException>(() => signed.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, signed.Revision,
                (_, _, _) => { called = true; return Task.CompletedTask; }), "Signed document is refused before transform");
            Check(!called, "Signed document refusal occurs before candidate generation");
        }

        var encrypted = Path.Combine(root, "fixtures", "corpus-v1", "compatibility", "password-aes256.pdf");
        using (var password = await DocumentSession.OpenAsync(workerPath, new RecoveryStore(Path.Combine(work, "password-recovery")), encrypted, "corpus-user"))
        {
            var called = false;
            await Rejected<InvalidOperationException>(() => password.ApplyExternalCandidateAsync(ExternalEditKind.Ocr, password.Revision,
                (_, _, _) => { called = true; return Task.CompletedTask; }), "Password-protected document is refused before transform");
            Check(!called, "Password refusal occurs before candidate generation");
        }

        Check(RecoveryStore.Fingerprint(source) == sourceHash, "All external-candidate outcomes preserve source bytes");
        File.WriteAllText(Path.Combine(work, "results.json"), JsonSerializer.Serialize(new { Passed = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS {checks.Count} external candidate checks. Evidence: {work}");
    }

    static string ReadCurrentCheckpointHash(RecoveryStore store, DocumentSession session)
    {
        return store.Read(session.Id).History[store.Read(session.Id).Index].Sha256;
    }
}
