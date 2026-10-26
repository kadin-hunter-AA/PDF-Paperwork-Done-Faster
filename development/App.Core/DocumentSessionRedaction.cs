using System.Text.Json;

namespace HunterPdf.Core;

/// <summary>Coordinates in PDF points from the top-left of an unrotated page.</summary>
public sealed record RedactionRegion(int Page, double Left, double Top, double Right, double Bottom);
public sealed record RedactedCopyResult(string Destination, string Sha256, bool MetadataRemoved);

public sealed class RedactionPreview : IDisposable
{
    internal Guid SessionId { get; }
    internal long Revision { get; }
    internal string Candidate { get; }
    internal string SourceHash { get; }
    internal string CandidateHash { get; }
    internal bool Disposed { get; private set; }
    public JsonElement Render { get; }
    internal RedactionPreview(Guid sessionId, long revision, string candidate, string sourceHash,
        string candidateHash, JsonElement render)
    {
        SessionId = sessionId; Revision = revision; Candidate = candidate;
        SourceHash = sourceHash; CandidateHash = candidateHash; Render = render;
    }
    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try { File.Delete(Candidate); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed partial class DocumentSession
{
    internal Action<string>? RedactionPublishProbe { get; set; }

    public Task<RedactionPreview> PreviewRedactionAsync(AdvancedEngineLocation location,
        IReadOnlyList<RedactionRegion> regions, int previewPage, int width, int height,
        long expectedRevision, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count is < 1 or > 128) throw new ArgumentException("Select 1–128 redaction rectangles.", nameof(regions));
        if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 8_000_000)
            throw new ArgumentException("Redaction preview size exceeds the rendering limit.");
        // Freeze caller-owned geometry before entering the asynchronous session gate.
        var frozenRegions = regions.ToArray();
        return RunAsync(async () =>
        {
            if (record.Revision != expectedRevision) throw new InvalidOperationException("The document changed. Refresh the redaction preview.");
            if (password is not null || record.SignatureCount > 0)
                throw new InvalidOperationException("Redaction does not support encrypted PDFs or signature fields.");
            if (previewPage < 0 || previewPage >= PageCount || frozenRegions.Any(r => r is null || r.Page < 0 || r.Page >= PageCount))
                throw new ArgumentException("A selected redaction page is outside the document.");
            await EnsureWorkerAsync(cancellation);
            var sourceHash = record.History[record.Index].Sha256;
            if (RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash) throw new IOException("The current checkpoint failed its integrity check.");
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var candidate = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var retained = false;
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                var engine = new AdvancedEngineClient(location.PythonExecutable, location.WorkerScript);
                var result = await engine.RunAsync(new { Operation = "Redact", Input = AdvancedEngineClient.FilePath(input),
                    Output = AdvancedEngineClient.FilePath(candidate), Regions = frozenRegions }, cancellation);
                if (!result.TryGetProperty("Applied", out var applied) || applied.ValueKind != JsonValueKind.True ||
                    !result.TryGetProperty("MetadataRemoved", out var metadata) || metadata.ValueKind != JsonValueKind.True ||
                    !result.TryGetProperty("TextAndVectorsPreserved", out var vectors) || vectors.ValueKind != JsonValueKind.True ||
                    !result.TryGetProperty("OutputSHA256", out var hash) || hash.ValueKind != JsonValueKind.String)
                    throw new IOException("The redaction engine did not validate its candidate.");
                RecoveryStore.RejectReparse(candidate);
                using (var durable = new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) durable.Flush(true);
                using var candidateLease = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                var candidateHash = RecoveryStore.Fingerprint(candidate);
                if (!string.Equals(hash.GetString(), candidateHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The redaction candidate failed its integrity check.");
                var validated = await RequestAsync("ValidateCandidate", new { Path = candidate }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount ||
                    validated.Data.GetProperty("SignatureCount").GetInt32() != 0 ||
                    validated.Data.GetProperty("SecurityHandlerRevision").GetInt32() != -1)
                    throw new IOException("The redaction candidate failed native validation.");
                using var previewWorker = new WorkerClient(workerExecutable);
                previewWorker.Start();
                var previewId = Guid.NewGuid();
                var opened = await previewWorker.SendAsync(Wire.Create(previewId, 0, "Open", new { Path = candidate }), cancellation);
                if (!opened.Success) throw new IOException("The redaction preview could not be opened.");
                var rendered = await previewWorker.SendAsync(Wire.Create(previewId, 0, "Render", new { Page = previewPage, Width = width, Height = height }), cancellation);
                if (!rendered.Success) throw new IOException("The redaction preview could not be rendered.");
                cancellation.ThrowIfCancellationRequested();
                if (RecoveryStore.Fingerprint(input) != sourceHash || RecoveryStore.Fingerprint(CurrentSnapshot) != sourceHash ||
                    RecoveryStore.Fingerprint(candidate) != candidateHash)
                    throw new IOException("A redaction input or candidate changed while preparing the preview.");
                var preview = new RedactionPreview(Id, record.Revision, candidate, sourceHash, candidateHash, rendered.Data);
                retained = true;
                return preview;
            }
            finally { TryRemoveTemporary(input); if (!retained) TryRemoveTemporary(candidate); }
        }, cancellation, isolated: true);
    }

    public Task<RedactedCopyResult> PublishRedactedCopyAsync(RedactionPreview preview, string destination,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return RunAsync(async () =>
        {
            if (preview.Disposed || preview.SessionId != Id || preview.Revision != record.Revision ||
                preview.SourceHash != record.History[record.Index].Sha256)
                throw new InvalidOperationException("The redaction preview is stale or belongs to another document. Create a new preview.");
            destination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(directory) || File.Exists(destination) || Directory.Exists(destination) ||
                SamePath(destination, OriginalPath) || SamePath(destination, LastSavePath) ||
                destination.StartsWith(store.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a new redacted PDF path outside recovery storage; existing files are never replaced.");
            RecoveryStore.RejectReparse(directory); RecoveryStore.RejectReparse(preview.Candidate);
            await EnsureWorkerAsync(cancellation);
            using var previewLease = new FileStream(preview.Candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (RecoveryStore.Fingerprint(preview.Candidate) != preview.CandidateHash || RecoveryStore.Fingerprint(CurrentSnapshot) != preview.SourceHash)
                throw new IOException("The reviewed redaction candidate or source checkpoint changed.");
            var candidate = Path.Combine(directory, ".redaction-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                RecoveryStore.CopyDurably(preview.Candidate, candidate);
                var validated = await RequestAsync("ValidateCandidate", new { Path = candidate }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount ||
                    validated.Data.GetProperty("SignatureCount").GetInt32() != 0 ||
                    validated.Data.GetProperty("SecurityHandlerRevision").GetInt32() != -1)
                    throw new IOException("The redaction export failed native validation.");
                RedactionPublishProbe?.Invoke(candidate);
                cancellation.ThrowIfCancellationRequested();
                if (RecoveryStore.Fingerprint(candidate) != preview.CandidateHash ||
                    RecoveryStore.Fingerprint(CurrentSnapshot) != preview.SourceHash)
                    throw new IOException("The reviewed redaction bytes changed before publication.");
                File.Move(candidate, destination);
                return new RedactedCopyResult(destination, preview.CandidateHash, true);
            }
            finally { TryRemoveTemporary(candidate); }
        }, cancellation, isolated: true);
    }
}
