using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HunterPdf.Desktop;

internal sealed record SavedSignature(string Id, SignatureKind Kind, DateTime CreatedUtc, int Width, int Height);

/// <summary>Local store of saved visual signatures: transparent PNGs (named s-/i-GUID) plus a small JSON index. Nothing else is stored.</summary>
internal sealed partial class SignatureStore
{
    const int MaxEntries = 200;
    readonly string root;
    readonly object gate = new();
    internal SignatureStore(string? root = null) =>
        this.root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HunterOps", "PdfEditor", "Signatures"));
    internal string Root => root;
    string IndexPath => Path.Combine(root, "index.json");

    [GeneratedRegex("^[si]-[0-9a-f]{32}$")] private static partial Regex IdPattern();
    sealed record IndexEntry(string Id, string Kind, DateTime Created, int Width, int Height);

    public SavedSignature Save(SignatureImage image)
    {
        var png = StampImages.BgraToPng(image.Bgra, image.Width, image.Height);
        lock (gate)
        {
            Directory.CreateDirectory(root); RejectReparse(root);
            var id = (image.Kind == SignatureKind.Initials ? "i-" : "s-") + Guid.NewGuid().ToString("N");
            using (var file = new FileStream(PngPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            { file.Write(png); file.Flush(true); }
            var entry = new SavedSignature(id, image.Kind, DateTime.UtcNow, image.Width, image.Height);
            var list = ReadAll().Where(e => e.Id != id).Append(entry).OrderBy(e => e.CreatedUtc).ToList();
            while (list.Count > MaxEntries) { TryDelete(PngPath(list[0].Id)); list.RemoveAt(0); }
            WriteIndex(list); return entry;
        }
    }

    public IReadOnlyList<SavedSignature> List() { lock (gate) return ReadAll().OrderByDescending(e => e.CreatedUtc).ToList(); }

    public SignatureImage? Load(string id)
    {
        if (!IdPattern().IsMatch(id)) return null;
        lock (gate)
        {
            try
            {
                var path = PngPath(id); var info = new FileInfo(path);
                if (!info.Exists || info.Length > StampImages.MaxPngBytes) return null;
                var (bgra, w, h) = StampImages.PngToBgra(File.ReadAllBytes(path));
                return new SignatureImage(bgra, w, h, id[0] == 'i' ? SignatureKind.Initials : SignatureKind.Signature);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException) { return null; }
        }
    }

    public bool Delete(string id)
    {
        if (!IdPattern().IsMatch(id)) return false;
        lock (gate)
        {
            var removed = TryDelete(PngPath(id));
            var list = ReadAll().Where(e => e.Id != id).ToList();
            if (Directory.Exists(root)) WriteIndex(list);
            return removed;
        }
    }

    string PngPath(string id) => Path.Combine(root, id + ".png");
    static bool TryDelete(string path) { try { if (!File.Exists(path)) return false; File.Delete(path); return true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }
    static void RejectReparse(string path) { if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("The signature folder is not a plain directory."); }

    /// <summary>PNG files are the truth; the index only supplies creation time and size. A corrupt or stale index is rebuilt.</summary>
    List<SavedSignature> ReadAll()
    {
        if (!Directory.Exists(root)) return [];
        var indexed = new Dictionary<string, IndexEntry>(); var corrupt = false;
        try
        {
            if (File.Exists(IndexPath) && new FileInfo(IndexPath).Length <= 1024 * 1024)
                foreach (var e in JsonSerializer.Deserialize<List<IndexEntry>>(File.ReadAllBytes(IndexPath)) ?? throw new JsonException())
                    if (e is not null && e.Id is not null && IdPattern().IsMatch(e.Id)) indexed[e.Id] = e;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { corrupt = true; indexed.Clear(); }
        var result = new List<SavedSignature>(); var changed = corrupt;
        foreach (var file in Directory.EnumerateFiles(root, "*.png"))
        {
            var id = Path.GetFileNameWithoutExtension(file); if (!IdPattern().IsMatch(id)) continue;
            var kind = id[0] == 'i' ? SignatureKind.Initials : SignatureKind.Signature;
            if (indexed.TryGetValue(id, out var e) && e.Width > 0 && e.Height > 0) { result.Add(new SavedSignature(id, kind, DateTime.SpecifyKind(e.Created, DateTimeKind.Utc), e.Width, e.Height)); continue; }
            try
            {
                if (new FileInfo(file).Length > StampImages.MaxPngBytes) continue;
                var (_, w, h) = StampImages.PngToBgra(File.ReadAllBytes(file));
                result.Add(new SavedSignature(id, kind, File.GetCreationTimeUtc(file), w, h)); changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException) { }
        }
        if (indexed.Count != result.Count(r => indexed.ContainsKey(r.Id))) changed = true;
        if (changed) { try { WriteIndex(result); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return result;
    }

    void WriteIndex(List<SavedSignature> list)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(list.Select(e => new IndexEntry(e.Id, e.Kind.ToString(), e.CreatedUtc, e.Width, e.Height)).ToList());
        var temp = Path.Combine(root, "index." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(data); file.Flush(true); }
            if (File.Exists(IndexPath)) File.Replace(temp, IndexPath, null); else File.Move(temp, IndexPath);
        }
        finally { TryDelete(temp); }
    }
}
