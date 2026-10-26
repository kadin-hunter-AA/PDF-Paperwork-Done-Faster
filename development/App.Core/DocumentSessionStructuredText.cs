using System.Text.Json;

namespace HunterPdf.Core;

public sealed class StructuredTextPreview : IDisposable
{
    internal Guid SessionId { get; }
    internal long Revision { get; }
    internal string Candidate { get; }
    internal string SourceHash { get; }
    internal string CandidateHash { get; }
    internal bool Disposed { get; private set; }
    public JsonElement Render { get; }
    internal StructuredTextPreview(Guid id, long revision, string candidate, string sourceHash, string candidateHash, JsonElement render)
    { SessionId = id; Revision = revision; Candidate = candidate; SourceHash = sourceHash; CandidateHash = candidateHash; Render = render; }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try { File.Delete(Candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed partial class DocumentSession
{
    public Task<JsonElement> InspectStructuredTextAsync(AdvancedEngineLocation location, int page, int cursor,
        bool includeInvisible, long expectedRevision, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            CheckStructuredTextRequest(page, expectedRevision);
            var sourceHash = record.History[record.Index].Sha256;
            if (RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash) throw new IOException("Checkpoint integrity check failed.");
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
                var result = await engine.RunAsync(new { Operation = "InspectText", Input = AdvancedEngineClient.FilePath(input),
                    Output = "", Page = page, Cursor = cursor, IncludeInvisible = includeInvisible }, cancellation);
                if (!string.Equals(result.GetProperty("SourceSHA256").GetString(), sourceHash, StringComparison.OrdinalIgnoreCase) ||
                    RecoveryStore.Fingerprint(input) != sourceHash || RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash)
                    throw new IOException("The text inspection input changed.");
                return result;
            }
            finally { TryRemoveTemporary(input); }
        }, cancellation, isolated: true);

    /// <summary>Finds the page-level mapped text run matching a clicked text object, in one companion call.</summary>
    public Task<JsonElement> FindStructuredTextAsync(AdvancedEngineLocation location, int page, string text, int occurrence,
        long expectedRevision, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            CheckStructuredTextRequest(page, expectedRevision);
            if (string.IsNullOrEmpty(text) || text.Length > 4000) throw new InvalidOperationException("The clicked text cannot be matched.");
            var sourceHash = record.History[record.Index].Sha256;
            if (RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash) throw new IOException("Checkpoint integrity check failed.");
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
                var result = await engine.RunAsync(new { Operation = "FindText", Input = AdvancedEngineClient.FilePath(input),
                    Output = "", Page = page, Text = text, Occurrence = occurrence, IncludeInvisible = false }, cancellation);
                if (!string.Equals(result.GetProperty("SourceSHA256").GetString(), sourceHash, StringComparison.OrdinalIgnoreCase) ||
                    RecoveryStore.Fingerprint(input) != sourceHash || RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash)
                    throw new IOException("The text inspection input changed.");
                return result;
            }
            finally { TryRemoveTemporary(input); }
        }, cancellation, isolated: true);

    void CheckStructuredTextRequest(int page, long revision)
    {
        if (revision != record.Revision) throw new InvalidOperationException("The document changed. Refresh the text selection.");
        if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
        if (password is not null) throw new InvalidOperationException("Mapped text editing does not support encrypted documents.");
    }

    public Task<StructuredTextPreview> PreviewStructuredTextAsync(AdvancedEngineLocation location, int page,
        JsonElement selection, string replacement, bool allowWider, bool includeInvisible, int width, int height,
        long expectedRevision, CancellationToken cancellation = default)
    {
        if (replacement is null || replacement.Length > 4000) throw new ArgumentException("Replacement text must contain at most 4000 characters.");
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 8_000_000)
            throw new ArgumentException("Text preview exceeds the rendering limit.");
        var frozen = selection.Clone();
        return RunAsync(async () =>
        {
            CheckStructuredTextRequest(page, expectedRevision);
            await EnsureWorkerAsync(cancellation);
            var sourceHash = record.History[record.Index].Sha256;
            if (!string.Equals(frozen.GetProperty("SourceSHA256").GetString(), sourceHash, StringComparison.OrdinalIgnoreCase) || RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash)
                throw new InvalidOperationException("The text selection is stale. Inspect the page again.");
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var candidate = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var retained = false;
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
                var result = await engine.RunAsync(new { Operation = "EditText", Input = AdvancedEngineClient.FilePath(input),
                    Output = AdvancedEngineClient.FilePath(candidate), Page = page, Selection = frozen,
                    Replacement = replacement, AllowWider = allowWider, IncludeInvisible = includeInvisible }, cancellation);
                RecoveryStore.RejectReparse(candidate);
                using (var durable = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
                using var lease = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = RecoveryStore.Fingerprint(candidate);
                if (!result.GetProperty("Applied").GetBoolean() || !string.Equals(result.GetProperty("OutputSHA256").GetString(), hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The text candidate failed its integrity check.");
                var validated = await RequestAsync("ValidateCandidate", new { Path = candidate }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount ||
                    validated.Data.GetProperty("SignatureCount").GetInt32() != record.SignatureCount ||
                    validated.Data.GetProperty("SecurityHandlerRevision").GetInt32() != -1)
                    throw new IOException("The text candidate failed native validation.");
                using var previewWorker = new WorkerClient(workerExecutable);
                previewWorker.Start();
                var previewId = Guid.NewGuid();
                var opened = await previewWorker.SendAsync(Wire.Create(previewId, 0, "Open", new { Path = candidate }), cancellation);
                if (!opened.Success) throw new IOException("The text preview could not be opened.");
                var rendered = await previewWorker.SendAsync(Wire.Create(previewId, 0, "Render", new { Page = page, Width = width, Height = height }), cancellation);
                if (!rendered.Success) throw new IOException("The text preview could not be rendered.");
                cancellation.ThrowIfCancellationRequested();
                if (RecoveryStore.Fingerprint(input) != sourceHash || RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash ||
                    RecoveryStore.Fingerprint(candidate) != hash) throw new IOException("The preview input or candidate changed.");
                retained = true;
                return new StructuredTextPreview(Id, record.Revision, candidate, sourceHash, hash, rendered.Data);
            }
            finally { TryRemoveTemporary(input); if (!retained) TryRemoveTemporary(candidate); }
        }, cancellation, isolated: true);
    }

    /// <summary>Keeps a companion engine process loaded so on-page text edits do not wait for library startup.</summary>
    public static void PrewarmAdvancedEngine(AdvancedEngineLocation location) =>
        AdvancedEngineClient.Prewarm(location.PythonExecutable, location.WorkerScript);

    /// <summary>
    /// Applies a mapped text replacement in one step for on-page editing: the companion writes the
    /// candidate directly into the external-edit commit, which hash-checks the input and validates the
    /// candidate natively before recording one checkpoint. No separate preview render is produced.
    /// </summary>
    public Task ApplyStructuredTextAsync(AdvancedEngineLocation location, int page, JsonElement selection, string replacement,
        bool allowWider, long expectedRevision, CancellationToken cancellation = default)
    {
        if (replacement is null || replacement.Length > 4000) throw new ArgumentException("Replacement text must contain at most 4000 characters.");
        var frozen = selection.Clone();
        return ApplyExternalCandidateAsync(ExternalEditKind.StructuredText, expectedRevision, async (input, candidate, token) =>
        {
            if (page < 0 || page >= PageCount) throw new ArgumentOutOfRangeException(nameof(page));
            if (!string.Equals(frozen.GetProperty("SourceSHA256").GetString(), RecoveryStore.Fingerprint(input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The text selection is stale. Click the text again.");
            var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
            var result = await engine.RunAsync(new { Operation = "EditText", Input = AdvancedEngineClient.FilePath(input),
                Output = AdvancedEngineClient.FilePath(candidate), Page = page, Selection = frozen,
                Replacement = replacement, AllowWider = allowWider, IncludeInvisible = false }, token);
            if (!result.GetProperty("Applied").GetBoolean() ||
                !string.Equals(result.GetProperty("OutputSHA256").GetString(), RecoveryStore.Fingerprint(candidate), StringComparison.OrdinalIgnoreCase))
                throw new IOException("The text candidate failed its integrity check.");
        }, cancellation);
    }

    public Task ApplyStructuredTextPreviewAsync(StructuredTextPreview preview, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return ApplyExternalCandidateAsync(ExternalEditKind.StructuredText, preview.Revision, (input, candidate, token) =>
        {
            if (preview.Disposed || preview.SessionId != Id || preview.SourceHash != record.History[record.Index].Sha256 ||
                RecoveryStore.Fingerprint(input) != preview.SourceHash || RecoveryStore.Fingerprint(preview.Candidate) != preview.CandidateHash)
                throw new InvalidOperationException("The text preview expired or changed. Create a new preview.");
            token.ThrowIfCancellationRequested();
            RecoveryStore.CopyDurably(preview.Candidate, candidate);
            if (RecoveryStore.Fingerprint(candidate) != preview.CandidateHash) throw new IOException("The text candidate changed during commit.");
            return Task.CompletedTask;
        }, cancellation);
    }
}
