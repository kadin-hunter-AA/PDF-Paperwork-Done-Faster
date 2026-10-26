using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HunterPdf.Core;

public sealed record Checkpoint(string FileName, string Sha256, string Command);
public sealed record RecoveryRecord(
    int Version, Guid Id, string OriginalPath, string OriginalHash,
    string LastSavePath, string LastSaveHash,
    int SignatureCount, bool AllowSignedCopy, bool PasswordRequired,
    long Revision, Checkpoint[] History, int Index, string SavedCheckpoint)
{
    [JsonIgnore]
    public bool IsDirty => History[Index].FileName != SavedCheckpoint;
}
public sealed record RecoveryEntry(Guid Id, string OriginalPath, bool IsDirty, bool PasswordRequired);
public sealed record RecoveryScan(IReadOnlyList<RecoveryEntry> Entries, IReadOnlyList<string> Errors);

public sealed class RecoveryStore
{
    public string Root { get; }
    internal Action? BeforeManifestCommit { get; set; }

    public RecoveryStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        RejectReparse(Root);
    }

    internal string DirectoryFor(Guid id)
    {
        var path = Path.GetFullPath(Path.Combine(Root, id.ToString("N")));
        if (!path.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recovery path is outside its storage directory.");
        return path;
    }

    internal FileStream Acquire(Guid id, bool create)
    {
        var directory = DirectoryFor(id);
        if (create) Directory.CreateDirectory(directory);
        RejectReparse(directory);
        return new FileStream(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal string SnapshotPath(Guid id, string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || !name.EndsWith(".pdf", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..^4], "N", out _))
            throw new IOException("Invalid recovery checkpoint name.");
        var path = Path.Combine(DirectoryFor(id), name);
        if (File.Exists(path)) RejectReparse(path);
        return path;
    }

    internal static bool IsSnapshotName(string name) =>
        name.Length == 36 && name.EndsWith(".pdf", StringComparison.Ordinal) && Guid.TryParseExact(name[..^4], "N", out _);

    // Best-effort removal of one snapshot file; false means it may still exist (locked, linked or unexpected path).
    internal bool TryDeleteSnapshot(Guid id, string name)
    {
        try
        {
            var path = SnapshotPath(id, name);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // Removes only snapshot-named .pdf files that the record no longer references (crash leftovers and pruned
    // checkpoints). Anything else in the directory is left alone. Call only while holding the session lease.
    internal int SweepStray(Guid id, RecoveryRecord record)
    {
        var referenced = record.History.Select(c => c.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryFor(id), "*.pdf").ToList())
            {
                var name = Path.GetFileName(file);
                if (!IsSnapshotName(name) || referenced.Contains(name)) continue;
                if (TryDeleteSnapshot(id, name)) removed++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    internal RecoveryRecord Read(Guid id)
    {
        var path = Path.Combine(DirectoryFor(id), "session.json");
        RejectReparse(path);
        var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path))
            ?? throw new IOException("Recovery record is empty.");
        if (record.Version != 1 || record.Id != id || record.History is null || record.History.Length == 0 ||
            record.Index < 0 || record.Index >= record.History.Length || record.Revision < 0 ||
            record.SignatureCount < 0 || string.IsNullOrEmpty(record.OriginalPath) || !Path.IsPathFullyQualified(record.OriginalPath) ||
            string.IsNullOrEmpty(record.LastSavePath) || !Path.IsPathFullyQualified(record.LastSavePath) ||
            !ValidHash(record.OriginalHash) || !ValidHash(record.LastSaveHash) ||
            record.History.Any(c => c is null || !ValidHash(c.Sha256)) ||
            record.History.Select(c => c.FileName).Distinct().Count() != record.History.Length)
            throw new IOException("Recovery record is invalid.");
        SnapshotPath(id, record.SavedCheckpoint);
        foreach (var checkpoint in record.History)
        {
            var snapshot = SnapshotPath(id, checkpoint.FileName);
            if (!File.Exists(snapshot) || Fingerprint(snapshot) != checkpoint.Sha256)
                throw new IOException("Recovery checkpoint failed its integrity check.");
        }
        return record;
    }

    internal void Write(RecoveryRecord record)
    {
        var directory = DirectoryFor(record.Id);
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            BeforeManifestCommit?.Invoke();
            File.Move(temp, Path.Combine(directory, "session.json"), true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public RecoveryScan Scan()
    {
        var entries = new List<RecoveryEntry>();
        var errors = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            FileStream? lease = null;
            try
            {
                lease = Acquire(id, false);
                if (!File.Exists(Path.Combine(directory, "session.json"))) continue;
                var record = Read(id);
                entries.Add(new RecoveryEntry(id, record.OriginalPath, record.IsDirty, record.PasswordRequired));
            }
            catch (IOException e) when (lease is null && (e.HResult & 0xffff) is 32 or 33) { /* A live session owns its lease. */ }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"Recovery record {id:N} could not be read; its files have been retained.");
            }
            finally { lease?.Dispose(); }
        }
        return new RecoveryScan(entries, errors);
    }

    internal void Remove(Guid id)
    {
        var directory = DirectoryFor(id); // Absolute, GUID-only path confined to Root.
        RejectReparse(directory);
        foreach (var child in Directory.EnumerateFileSystemEntries(directory)) RejectReparse(child);
        Directory.Delete(directory, true);
    }

    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked recovery or destination paths are not supported.");
    }

    internal static string Fingerprint(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    internal static void CopyDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }
}
