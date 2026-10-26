namespace HunterPdf.Pdfium;

public sealed partial class Engine
{
    readonly Dictionary<Guid, (long Revision, string Path, string? Password, byte[] Hash)> renderSnapshots = new();
    readonly Dictionary<Guid, (long Revision, Pdf Pdf)> renderCopies = new();

    void DisposeRenderCopy(Guid id)
    {
        if (!renderCopies.Remove(id, out var copy)) return;
        copy.Pdf.Dispose();
        copy.Pdf.ReleaseRenderBytes();
    }
    void SetRenderSnapshot(Guid id, long revision, string path, string? password)
    {
        DisposeRenderCopy(id);
        renderSnapshots[id] = (revision, Path.GetFullPath(path), password, HashFile(path));
    }
    Pdf IsolatedRenderDocument(Guid id, long revision)
    {
        var current = Get(id, revision);
        if (renderCopies.TryGetValue(id, out var cached) && cached.Revision == revision) return cached.Pdf;
        DisposeRenderCopy(id);
        Pdf copy;
        if (renderSnapshots.TryGetValue(id, out var snapshot) && snapshot.Revision == revision)
        {
            if (!HashFile(snapshot.Path).SequenceEqual(snapshot.Hash))
                throw new InvalidOperationException("The render snapshot changed. Reopen the document before continuing.");
            copy = Pdf.Open(snapshot.Path, snapshot.Password);
        }
        else
        {
            // Uncommitted native previews have no checkpoint file. Clone their
            // current state in memory; rendering must never mutate edit state.
            copy = current.CloneForRendering(snapshot.Password);
        }
        renderCopies[id] = (revision, copy);
        return copy;
    }
}
