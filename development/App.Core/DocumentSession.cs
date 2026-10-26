using System.Text.Json;

namespace HunterPdf.Core;

public sealed class DocumentConflictException(string message) : IOException(message);
public sealed class DocumentPasswordRequiredException(string message) : InvalidOperationException(message);
public sealed class SignedDocumentException() : InvalidOperationException(
    "This PDF contains signature fields. Editing requires explicit consent to edit a copy. Any existing signatures will not be valid for the edited copy.");
public sealed class SaveCompletedException(string destination, Exception inner) : IOException(
    "The PDF was saved, but its recovery record could not be updated. The recovery checkpoints have been retained.", inner)
{
    public string Destination { get; } = destination;
}
public sealed record SaveResult(string Destination, string? Backup);
internal enum SaveStage { CandidateCreated, CandidateValidated, BeforePublish }

public sealed partial class DocumentSession : IDisposable
{
    readonly WorkerClient worker;
    readonly string workerExecutable;
    readonly RecoveryStore store;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string? password;
    FileStream? lease;
    Guid workerDocument;
    long workerRevision;
    bool workerReady;
    bool closed;
    RecoveryRecord record;
    readonly HashSet<string> pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id => record.Id;
    public string OriginalPath => record.OriginalPath;
    public string LastSavePath => record.LastSavePath;
    public long Revision => record.Revision;
    public bool IsDirty => record.IsDirty;
    public bool CanUndo => record.Index > 0;
    public bool CanRedo => record.Index + 1 < record.History.Length;
    public int SignatureCount => record.SignatureCount;
    public bool SignedCopyAuthorized => record.AllowSignedCopy;
    public bool SignaturesInvalidated => record.SignatureCount > 0 && record.History[record.Index].FileName != record.History[0].FileName;
    public int PageCount { get; private set; }
    /// <summary>Limits applied after each committed edit. Initialized from <see cref="Core.HistoryPolicy.Default"/>; a change takes effect at the next edit or compaction.</summary>
    public HistoryPolicy HistoryPolicy { get; set; } = HistoryPolicy.Default;
    /// <summary>Number of checkpoints currently kept, including the opened one and any redo entries.</summary>
    public int HistoryCount => record.History.Length;
    /// <summary>Total size on disk of the snapshot files the history references.</summary>
    public long HistoryBytes => record.History.Sum(c => SnapshotSize(c.FileName));
    internal int? WorkerProcessId => worker.ProcessId;
    internal Action<SaveStage, string>? SaveProbe { get; set; }

    string CurrentSnapshot => store.SnapshotPath(Id, record.History[record.Index].FileName);

    DocumentSession(string executable, RecoveryStore store, RecoveryRecord record, string? password, FileStream lease)
    {
        worker = new WorkerClient(executable);
        workerExecutable = executable;
        this.store = store;
        this.record = record;
        this.password = password;
        this.lease = lease;
    }

    public static async Task<DocumentSession> OpenAsync(string executable, RecoveryStore store, string source,
        string? password = null, CancellationToken cancellation = default)
    {
        source = Path.GetFullPath(source);
        var id = Guid.NewGuid();
        var lease = store.Acquire(id, true);
        DocumentSession? session = null;
        try
        {
            var name = Guid.NewGuid().ToString("N") + ".pdf";
            var snapshot = store.SnapshotPath(id, name);
            RecoveryStore.CopyDurably(source, snapshot);
            var hash = RecoveryStore.Fingerprint(snapshot);
            var record = new RecoveryRecord(1, id, source, hash, source, hash, 0, false,
                password is not null, 0, [new Checkpoint(name, hash, "Open")], 0, name);
            session = new DocumentSession(executable, store, record, password, lease);
            var opened = await session.ReloadAsync(cancellation);
            session.record = record with { SignatureCount = opened.Data.GetProperty("SignatureCount").GetInt32() };
            store.Write(session.record);
            return session;
        }
        catch
        {
            session?.worker.Dispose();
            lease.Dispose();
            store.Remove(id);
            throw;
        }
    }

    public static async Task<DocumentSession> RecoverAsync(string executable, RecoveryStore store, Guid id,
        string? password = null, CancellationToken cancellation = default)
    {
        var lease = store.Acquire(id, false);
        DocumentSession? session = null;
        try
        {
            var record = store.Read(id);
            store.SweepStray(id, record); // Crash leftovers and pruned snapshots; only unreferenced snapshot-named PDFs.
            session = new DocumentSession(executable, store, record, password, lease);
            await session.ReloadAsync(cancellation);
            return session;
        }
        catch { session?.worker.Dispose(); lease.Dispose(); throw; }
    }

    async Task<Response> ReloadAsync(CancellationToken cancellation)
    {
        worker.Stop();
        worker.Start();
        workerReady = false;
        workerDocument = Guid.NewGuid();
        workerRevision = 0;
        var opened = await RequestAsync("Open", new { Path = CurrentSnapshot, Password = password, AllowSignedCopy = record.AllowSignedCopy }, cancellation);
        PageCount = opened.Data.GetProperty("PageCount").GetInt32();
        workerReady = true;
        DeleteUnreferenced([]); // A fresh worker no longer holds old snapshots open; retry earlier failures.
        return opened;
    }

    async Task EnsureWorkerAsync(CancellationToken cancellation)
    {
        if (!workerReady || worker.ProcessId is null) await ReloadAsync(cancellation);
    }

    async Task<Response> RequestAsync(string operation, object args, CancellationToken cancellation)
    {
        long bytes = 0;
        try { bytes = new FileInfo(CurrentSnapshot).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var response = await worker.SendAsync(Wire.Create(workerDocument, workerRevision, operation, args), cancellation,
            WorkerTimeouts.ForOperation(operation, bytes, PageCount));
        if (!response.Success)
        {
            if (operation == "Open" && response.ErrorCode == WireErrorCodes.PasswordRequired)
                throw new DocumentPasswordRequiredException(response.Error ?? "This PDF requires a password or the password is incorrect.");
            throw new InvalidOperationException(response.Error);
        }
        workerRevision = response.Revision;
        return response;
    }

    async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellation, bool isolated = false)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            ObjectDisposedException.ThrowIf(closed, this);
            return await action();
        }
        catch (Exception e) when (e is WorkerUnavailableException or OperationCanceledException)
        {
            if (!isolated) workerReady = false;
            throw;
        }
        finally { gate.Release(); }
    }

    public Task<JsonElement> ExtractAsync(int page, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("Extract", new { Page = page, IncludeCharacters = false }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> ExtractTextLayoutAsync(int page, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("Extract", new { Page = page, IncludeCharacters = true }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> PageInfoAsync(CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("PageInfo", new { }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> BookmarksAsync(CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("Bookmarks", new { }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> RenderAsync(int page, int width, int height, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            await EnsureWorkerAsync(cancellation);
            return (await RequestAsync("Render", new { Page = page, Width = width, Height = height }, cancellation)).Data;
        }, cancellation);

    public Task<JsonElement> PreviewTextAsync(int page, string objectId, string text, int width, int height,
        CancellationToken cancellation = default) => RunAsync(async () =>
        {
            if (record.SignatureCount > 0 && !record.AllowSignedCopy) throw new SignedDocumentException();
            await EnsureWorkerAsync(cancellation);
            var parts = objectId.Split(':');
            if (parts.Length != 4 || parts[0] != workerDocument.ToString("N") ||
                parts[1] != workerRevision.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                parts[2] != page.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                !int.TryParse(parts[3], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var objectIndex) || objectIndex < 0)
                throw new InvalidOperationException("The selected text is stale. Select the object again.");
            if (width < 1 || height < 1 || width > 4096 || height > 4096 || (long)width * height > 8_000_000)
                throw new InvalidOperationException("Preview size exceeds the rendering limit.");
            // A separate worker opens the exact durable checkpoint, including unsaved committed edits.
            // Candidate mutation never reaches this session's worker, history, recovery record or source.
            using var preview = new WorkerClient(workerExecutable);
            preview.Start();
            var previewId = Guid.NewGuid();
            async Task<Response> Send(string operation, long revision, object arguments)
            {
                var result = await preview.SendAsync(Wire.Create(previewId, revision, operation, arguments), cancellation);
                if (!result.Success) throw new InvalidOperationException(result.Error);
                return result;
            }
            await Send("Open", 0, new { Path = CurrentSnapshot, Password = password, AllowSignedCopy = record.AllowSignedCopy });
            var previewObject = $"{previewId:N}:0:{page}:{objectIndex}";
            await Send("ReplaceText", 0, new { Page = page, ObjectId = previewObject, Text = text });
            return (await Send("Render", 1, new { Page = page, Width = width, Height = height })).Data;
        }, cancellation, isolated: true);

    public Task AuthorizeSignedCopyAsync(CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            var next = record with { AllowSignedCopy = true };
            store.Write(next);
            record = next;
            await ReloadAsync(cancellation);
            return true;
        }, cancellation);

    public Task AddTextAsync(int page, string text, double x, double y, CancellationToken cancellation = default) =>
        EditAsync("AddText", new { Page = page, Text = text, X = x, Y = y }, cancellation);

    public Task ReplaceTextAsync(int page, string objectId, string text, CancellationToken cancellation = default) =>
        EditAsync("ReplaceText", new { Page = page, ObjectId = objectId, Text = text }, cancellation);

    public Task AddMarkupAsync(int page, string kind, object[] quads, CancellationToken cancellation = default) =>
        EditAsync("AddMarkup", new { Page = page, Kind = kind, Quads = quads }, cancellation);

    public Task AddDrawingAsync(int page, string kind, double left, double bottom, double right, double top,
        CancellationToken cancellation = default) =>
        EditAsync("AddDrawing", new { Page = page, Kind = kind, Left = left, Bottom = bottom, Right = right, Top = top }, cancellation);

    public Task AddTextBoxAsync(int page, string text, double left, double bottom, double right, double top,
        CancellationToken cancellation = default)
    {
        if (!double.IsFinite(left) || !double.IsFinite(bottom) || !double.IsFinite(right) || !double.IsFinite(top))
            throw new InvalidOperationException("Text box bounds must be finite PDF page coordinates.");
        return EditAsync("AddTextBox", new { Page = page, Text = text, Left = left, Bottom = bottom, Right = right, Top = top }, cancellation);
    }

    public Task AddInkAsync(int page, string kind, IEnumerable<(double X, double Y)> points,
        CancellationToken cancellation = default)
    {
        var list = points.Select(p => new[] { p.X, p.Y }).ToArray();
        if (list.Any(p => !double.IsFinite(p[0]) || !double.IsFinite(p[1])))
            throw new InvalidOperationException("Stroke points must be finite PDF page coordinates.");
        return EditAsync("AddInk", new { Page = page, Kind = kind, Points = list }, cancellation);
    }

    public Task AddCommentAsync(int page, string contents, double left, double bottom, double right, double top, CancellationToken cancellation = default) =>
        EditAsync("AddComment", new { Page = page, Contents = contents, Left = left, Bottom = bottom, Right = right, Top = top }, cancellation);

    public Task DeleteAnnotationAsync(int page, string annotationId, CancellationToken cancellation = default) =>
        EditAsync("DeleteAnnotation", new { Page = page, AnnotationId = annotationId }, cancellation);

    public Task MoveDrawingAsync(int page, string annotationId, double dx, double dy, CancellationToken cancellation = default)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy))
            throw new InvalidOperationException("Enter a finite, nonzero move distance in PDF points.");
        return EditAsync("MoveDrawing", new { Page = page, AnnotationId = annotationId, Dx = dx, Dy = dy }, cancellation);
    }

    public Task RestyleDrawingAsync(int page, string annotationId, int red, int green, int blue, double width, CancellationToken cancellation = default)
    {
        if (!double.IsFinite(width))
            throw new InvalidOperationException("Line width must be between 0.5 and 12 points.");
        return EditAsync("RestyleDrawing", new { Page = page, AnnotationId = annotationId, Red = red, Green = green, Blue = blue, Width = width }, cancellation);
    }

    public Task UpdateCommentAsync(int page, string annotationId, string contents, CancellationToken cancellation = default) =>
        EditAsync("UpdateComment", new { Page = page, AnnotationId = annotationId, Contents = contents }, cancellation);

    public Task RotatePageAsync(int page, int rotation, CancellationToken cancellation = default) =>
        EditAsync("RotatePage", new { Page = page, Rotation = rotation }, cancellation);

    async Task EditAsync(string operation, object args, CancellationToken cancellation, int pageCountDelta = 0)
    {
        await RunAsync(async () =>
        {
            if (record.SignatureCount > 0 && !record.AllowSignedCopy) throw new SignedDocumentException();
            await EnsureWorkerAsync(cancellation);
            var expectedPageCount = checked(PageCount + pageCountDelta);
            if (expectedPageCount < 1 || (pageCountDelta != 0 && expectedPageCount > 50_000)) throw new InvalidOperationException("Page insertion supports documents with up to 50,000 pages.");
            var name = Guid.NewGuid().ToString("N") + ".pdf";
            var snapshot = store.SnapshotPath(Id, name);
            try
            {
                await RequestAsync(operation, args, cancellation);
                await RequestAsync("SaveCandidate", new { Path = snapshot }, cancellation);
                var validated = await RequestAsync("ValidateCandidate", new { Path = snapshot, Password = password }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != expectedPageCount)
                    throw new IOException("Edit checkpoint has an unexpected page count.");
                var checkpoint = new Checkpoint(name, RecoveryStore.Fingerprint(snapshot), operation);
                CommitCheckpoint(checkpoint); // Durable checkpoint commit before the edit is reported as accepted.
                PageCount = expectedPageCount;
                // PDFium can keep a replaced shared image in other loaded pages' caches.
                // The validated checkpoint has isolated resource streams; reopen it before
                // the next read/edit instead of continuing with that uncertain live cache.
                if (operation == "ReplaceImage") workerReady = false;
                return true;
            }
            catch
            {
                // Any uncertain native mutation is discarded; the last committed checkpoint remains authoritative.
                worker.Stop();
                workerReady = false;
                if (File.Exists(snapshot)) File.Delete(snapshot);
                throw;
            }
        }, cancellation);
    }

    // Single append point for every committed edit. Appends the checkpoint (discarding any redo branch), prunes the
    // oldest checkpoints beyond HistoryPolicy, writes the record durably, and only then deletes unreferenced snapshot
    // files. Never pruned: the opened checkpoint (History[0]), the saved checkpoint, and the current (newest) one.
    // After pruning, Undo from the oldest kept step goes to the next older kept checkpoint (never to a pruned state),
    // and Undo stops at the opened checkpoint. Cleanup failures are tolerated and retried at the next commit.
    internal void CommitCheckpoint(Checkpoint checkpoint)
    {
        var kept = record.History.Take(record.Index + 1).Append(checkpoint).ToList();
        var removed = record.History.Skip(record.Index + 1).Select(c => c.FileName).ToList();
        var policy = HistoryPolicy;
        var maxSteps = Math.Max(1, policy.MaxSteps);
        var sizes = kept.Select(c => SnapshotSize(c.FileName)).ToList();
        var total = sizes.Sum();
        for (var i = 1; i < kept.Count - 1 && (kept.Count - 1 > maxSteps || total > policy.MaxBytes);)
        {
            if (kept[i].FileName == record.SavedCheckpoint) { i++; continue; }
            removed.Add(kept[i].FileName);
            total -= sizes[i];
            kept.RemoveAt(i);
            sizes.RemoveAt(i);
        }
        var next = record with { History = kept.ToArray(), Index = kept.Count - 1, Revision = record.Revision + 1 };
        store.Write(next);
        record = next;
        DeleteUnreferenced(removed);
        // The native engine may still hold a pruned snapshot open. Reload lazily before the next operation so it is released.
        if (pendingDeletes.Count > 0) workerReady = false;
    }

    long SnapshotSize(string name)
    {
        try { return new FileInfo(store.SnapshotPath(Id, name)).Length; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    // Deletes snapshots no longer in History (plus earlier failures). Never throws; survivors are retried later.
    void DeleteUnreferenced(IEnumerable<string> names)
    {
        foreach (var name in names) pendingDeletes.Add(name);
        var live = record.History.Select(c => c.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in pendingDeletes.ToList())
            if (live.Contains(name) || store.TryDeleteSnapshot(Id, name)) pendingDeletes.Remove(name);
    }

    /// <summary>
    /// Reclaims history disk space: keeps only the opened checkpoint, the saved checkpoint and the current one
    /// (identical entries merged), discards the redo branch and deletes every other snapshot. The document content,
    /// revision and dirty state are unchanged and no worker reload is needed. Works whether or not the document is
    /// dirty; if it is, Undo can afterwards only reach the saved (if older than current) and opened checkpoints.
    /// The record is written durably before any file is deleted.
    /// </summary>
    public Task CompactHistoryAsync(CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            var kept = new List<Checkpoint>();
            for (var i = 0; i <= record.Index; i++)
            {
                var c = record.History[i];
                if ((i == 0 || i == record.Index || c.FileName == record.SavedCheckpoint) && kept.All(k => k.FileName != c.FileName)) kept.Add(c);
            }
            var removed = record.History.Select(c => c.FileName).Where(n => kept.All(k => k.FileName != n)).ToList();
            if (removed.Count > 0)
            {
                var next = record with { History = kept.ToArray(), Index = kept.Count - 1 };
                store.Write(next);
                record = next;
            }
            DeleteUnreferenced(removed);
            if (pendingDeletes.Count > 0) // The worker may hold a removed snapshot open; reopen the current checkpoint to release it.
            {
                await ReloadAsync(cancellation);
                if (pendingDeletes.Count > 0) workerReady = false;
            }
            return true;
        }, cancellation);

    public Task UndoAsync(CancellationToken cancellation = default) => MoveHistoryAsync(-1, cancellation);
    public Task RedoAsync(CancellationToken cancellation = default) => MoveHistoryAsync(1, cancellation);

    Task MoveHistoryAsync(int direction, CancellationToken cancellation) =>
        RunAsync(async () =>
        {
            var index = record.Index + direction;
            if (index < 0 || index >= record.History.Length) throw new InvalidOperationException("No history entry is available.");
            cancellation.ThrowIfCancellationRequested();
            var next = record with { Index = index, Revision = record.Revision + 1 };
            // Checkpoint integrity is checked before committing selection.
            if (RecoveryStore.Fingerprint(store.SnapshotPath(Id, next.History[index].FileName)) != next.History[index].Sha256)
                throw new IOException("History checkpoint failed its integrity check.");
            var previous = record;
            try
            {
                record = next;
                await ReloadAsync(cancellation);
                store.Write(next);
                return true;
            }
            catch
            {
                record = previous;
                worker.Stop();
                workerReady = false;
                throw;
            }
        }, cancellation);

    public Task<SaveResult> SaveAsync(string destination, bool overwrite = false, CancellationToken cancellation = default) =>
        RunAsync(async () =>
        {
            destination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(destination)!;
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The save folder does not exist.");
            if (destination.StartsWith(store.Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose a destination outside recovery storage.");
            if (record.SignatureCount > 0 && SamePath(destination, OriginalPath))
                throw new IOException("Signed originals are protected. Save the edited document to another path.");
            var exists = File.Exists(destination);
            if (exists && !overwrite) throw new IOException("The destination exists. Explicit overwrite permission is required.");
            if (exists)
            {
                RecoveryStore.RejectReparse(destination);
                if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException("The destination is read-only.");
            }
            string? expected = exists ? RecoveryStore.Fingerprint(destination) : null;
            if (SamePath(destination, OriginalPath) && expected != record.OriginalHash)
                throw new DocumentConflictException("The original PDF changed outside the application. Use Save As to preserve both versions.");
            if (SamePath(destination, LastSavePath) && expected != record.LastSaveHash)
                throw new DocumentConflictException("The saved PDF changed outside the application. Use Save As to preserve both versions.");
            await EnsureWorkerAsync(cancellation);
            var candidate = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".candidate.pdf");
            string? backup = null;
            var published = false;
            try
            {
                // Publish exact committed bytes: no additional PDF rewrite or signature change on a clean Save As.
                RecoveryStore.CopyDurably(CurrentSnapshot, candidate);
                SaveProbe?.Invoke(SaveStage.CandidateCreated, candidate);
                var validated = await RequestAsync("ValidateCandidate", new { Path = candidate, Password = password }, cancellation);
                if (validated.Data.GetProperty("PageCount").GetInt32() != PageCount ||
                    RecoveryStore.Fingerprint(candidate) != record.History[record.Index].Sha256)
                    throw new IOException("The save candidate failed validation.");
                SaveProbe?.Invoke(SaveStage.CandidateValidated, candidate);
                cancellation.ThrowIfCancellationRequested();
                SaveProbe?.Invoke(SaveStage.BeforePublish, candidate);
                // Recheck bytes immediately before the atomic namespace operation.
                if (RecoveryStore.Fingerprint(candidate) != record.History[record.Index].Sha256)
                    throw new IOException("The validated save candidate changed.");
                var actual = File.Exists(destination) ? RecoveryStore.Fingerprint(destination) : null;
                if (actual != expected) throw new DocumentConflictException("The destination changed during saving. No replacement was made.");
                if (exists)
                {
                    backup = Path.Combine(directory, Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".backup.pdf");
                    File.Replace(candidate, destination, backup);
                }
                else File.Move(candidate, destination); // Exclusive creation rejects a late competing file.
                published = true;
                var savedHash = record.History[record.Index].Sha256;
                var next = record with
                {
                    LastSavePath = destination, LastSaveHash = savedHash,
                    OriginalHash = SamePath(destination, OriginalPath) ? savedHash : record.OriginalHash,
                    SavedCheckpoint = record.History[record.Index].FileName
                };
                record = next; // The save really happened even if subsequent recovery bookkeeping fails.
                try { store.Write(next); }
                catch (Exception e) { throw new SaveCompletedException(destination, e); }
                return new SaveResult(destination, backup);
            }
            finally
            {
                if (!published && File.Exists(candidate)) File.Delete(candidate);
            }
        }, cancellation);

    public async Task CloseAsync(bool discardUnsaved = false, CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (closed) return;
            if (IsDirty && !discardUnsaved) throw new InvalidOperationException("Unsaved changes require saving or explicit discard.");
            worker.Dispose();
            lease?.Dispose();
            lease = null;
            store.Remove(Id);
            closed = true;
        }
        finally { gate.Release(); }
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // Dispose deliberately retains recovery. Only CloseAsync may discard checkpoints.
    public void Dispose()
    {
        worker.Dispose();
        lease?.Dispose();
        lease = null;
        closed = true;
    }
}
