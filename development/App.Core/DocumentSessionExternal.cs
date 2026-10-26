namespace HunterPdf.Core;

internal enum ExternalEditKind { FillForm, Ocr, StructuredText, QuickTool, FormFields, RichText, Signature }

public sealed partial class DocumentSession
{
    // Only trusted, feature-specific adapters may call this. A transform receives
    // a disposable input copy, never the source PDF or authoritative checkpoint.
    internal Task ApplyExternalCandidateAsync(ExternalEditKind kind, long expectedRevision,
        Func<string, string, CancellationToken, Task> transform, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (record.Revision != expectedRevision)
                throw new InvalidOperationException("The document changed. Refresh the selected operation before applying it.");
            if (record.SignatureCount > 0 && !record.AllowSignedCopy)
                throw new SignedDocumentException();
            if (password is not null)
                throw new InvalidOperationException("This operation does not support password-protected PDFs.");
            await EnsureWorkerAsync(cancellation);
            var expectedHash = record.History[record.Index].Sha256;
            if (RecoveryStore.Fingerprint(CurrentSnapshot) != expectedHash)
                throw new IOException("The committed checkpoint failed its integrity check.");
            var input = store.SnapshotPath(Id, Guid.NewGuid().ToString("N") + ".pdf");
            var name = Guid.NewGuid().ToString("N") + ".pdf";
            var snapshot = store.SnapshotPath(Id, name);
            var committed = false;
            try
            {
                RecoveryStore.CopyDurably(CurrentSnapshot, input);
                await transform(input, snapshot, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (RecoveryStore.Fingerprint(input) != expectedHash ||
                    RecoveryStore.Fingerprint(CurrentSnapshot) != expectedHash)
                    throw new IOException("The transform changed its input. No edit was accepted.");
                RecoveryStore.RejectReparse(snapshot);
                using (var durable = new FileStream(snapshot, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    durable.Flush(flushToDisk: true);
                // Keep the validated bytes immutable until durable history commit.
                using var candidateLease = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                var validated = await RequestAsync("ValidateCandidate", new { Path = snapshot }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount)
                    throw new IOException("The external edit changed the document page count.");
                if (validated.Data.GetProperty("SignatureCount").GetInt32() != record.SignatureCount ||
                    validated.Data.GetProperty("SecurityHandlerRevision").GetInt32() != -1)
                    throw new IOException("An external edit cannot change signature fields or introduce encryption.");
                cancellation.ThrowIfCancellationRequested();
                var checkpoint = new Checkpoint(name, RecoveryStore.Fingerprint(snapshot), kind.ToString());
                CommitCheckpoint(checkpoint);
                committed = true;
                // No external candidate ever mutates the currently loaded native PDF.
                workerReady = false;
                return true;
            }
            finally
            {
                // Orphaned temporary files are confined to this recovery directory.
                // A cleanup problem must not misreport a committed edit as failed.
                TryRemoveTemporary(input);
                if (!committed) TryRemoveTemporary(snapshot);
            }
        }, cancellation, isolated: true);

    static void TryRemoveTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
