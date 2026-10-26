using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HunterPdf.Core;

public sealed record InstalledFont(string Family, string Style, string Path, int FaceIndex, bool Embeddable);

/// <summary>Lists installed TrueType/OpenType faces from the Windows system and per-user font folders.</summary>
public static class FontCatalog
{
    static readonly object Gate = new();
    static IReadOnlyList<InstalledFont>? cache;

    /// <summary>Cached list of installed faces, ordered by family then style. Malformed files are skipped.</summary>
    public static IReadOnlyList<InstalledFont> List()
    {
        lock (Gate) return cache ??= Enumerate();
    }

    /// <summary>Drops the cache so the next <see cref="List"/> rescans the font folders.</summary>
    public static void Refresh() { lock (Gate) cache = null; }

    internal static IEnumerable<string> FontFolders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows)) yield return System.IO.Path.Combine(windows, "Fonts");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return System.IO.Path.Combine(local, "Microsoft", "Windows", "Fonts");
    }

    static IReadOnlyList<InstalledFont> Enumerate()
    {
        var files = new List<string>();
        foreach (var folder in FontFolders().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                files.AddRange(Directory.EnumerateFiles(folder).Where(IsFontFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var found = new ConcurrentBag<InstalledFont>();
        Parallel.ForEach(files, file =>
        {
            try { foreach (var face in InspectFile(file)) found.Add(face.Font); }
            catch (Exception) { /* a malformed or unreadable font is skipped */ }
        });
        return found.OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Style, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.FaceIndex).ToArray();
    }

    static bool IsFontFile(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc";

    internal readonly record struct FaceInspection(InstalledFont Font, int FsType);

    /// <summary>Parses every face of one font file (name table and OS/2 fsType). Throws for malformed files.</summary>
    internal static IReadOnlyList<FaceInspection> InspectFile(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = RandomAccess.GetLength(handle);
        if (length < 12 || length > int.MaxValue) throw new InvalidDataException();
        var head = new byte[16];
        Read(handle, 0, head);
        var offsets = new List<long>();
        if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')
        {
            var count = (int)U32(head, 8);
            if (count < 1 || count > 256) throw new InvalidDataException();
            var table = new byte[4 * count];
            Read(handle, 12, table);
            for (var i = 0; i < count; i++) offsets.Add(U32(table, 4 * i));
        }
        else offsets.Add(0);
        var faces = new List<FaceInspection>();
        for (var index = 0; index < offsets.Count; index++)
        {
            try { faces.Add(InspectFace(handle, length, path, offsets[index], index)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException) { }
        }
        return faces;
    }

    static FaceInspection InspectFace(SafeFileHandle handle, long length, string path, long offset, int index)
    {
        var header = new byte[12];
        Read(handle, offset, header);
        var version = U32(header, 0);
        if (version is not (0x00010000 or 0x4F54544F or 0x74727565)) throw new InvalidDataException();
        var count = U16(header, 4);
        if (count == 0 || count > 256) throw new InvalidDataException();
        var directory = new byte[16 * count];
        Read(handle, offset + 12, directory);
        var tables = new Dictionary<string, (long Offset, long Length)>();
        for (var i = 0; i < count; i++)
        {
            var tag = Encoding.Latin1.GetString(directory, 16 * i, 4);
            var tableOffset = U32(directory, 16 * i + 8);
            var tableLength = U32(directory, 16 * i + 12);
            if (tableOffset + tableLength > length) throw new InvalidDataException();
            tables[tag] = (tableOffset, tableLength);
        }
        foreach (var required in new[] { "head", "hhea", "maxp", "hmtx", "cmap" })
            if (!tables.ContainsKey(required)) throw new InvalidDataException();
        var outlines = (tables.ContainsKey("glyf") && tables.ContainsKey("loca")) || tables.ContainsKey("CFF ");
        var fsType = 0;
        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 10)
        {
            var bytes = new byte[2];
            Read(handle, os2.Offset + 8, bytes);
            fsType = U16(bytes, 0);
        }
        var (family, style) = tables.TryGetValue("name", out var name) && name.Length is > 6 and <= 1_048_576
            ? ReadNames(handle, name) : ("", "");
        if (string.IsNullOrWhiteSpace(family)) family = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(style)) style = "Regular";
        var restricted = (fsType & 0x0002) != 0 && (fsType & 0x000C) == 0;
        var bitmapOnly = (fsType & 0x0200) != 0;
        var embeddable = outlines && !restricted && !bitmapOnly;
        return new FaceInspection(new InstalledFont(family.Trim(), style.Trim(), path, index, embeddable), fsType);
    }

    static (string Family, string Style) ReadNames(SafeFileHandle handle, (long Offset, long Length) table)
    {
        var data = new byte[table.Length];
        Read(handle, table.Offset, data);
        var count = U16(data, 2);
        var storage = U16(data, 4);
        var best = new Dictionary<int, (int Rank, string Value)>();
        for (var i = 0; i < count; i++)
        {
            var record = 6 + 12 * i;
            if (record + 12 > data.Length) break;
            var platform = U16(data, record);
            var language = U16(data, record + 4);
            var id = U16(data, record + 6);
            var size = U16(data, record + 8);
            var start = storage + U16(data, record + 10);
            if (id is not (1 or 2 or 16 or 17) || start + size > data.Length) continue;
            string text;
            int rank;
            if (platform == 3 && language == 0x0409) { text = Encoding.BigEndianUnicode.GetString(data, start, size); rank = 0; }
            else if (platform == 3) { text = Encoding.BigEndianUnicode.GetString(data, start, size); rank = 1; }
            else if (platform == 0) { text = Encoding.BigEndianUnicode.GetString(data, start, size); rank = 2; }
            else if (platform == 1) { text = Encoding.Latin1.GetString(data, start, size); rank = 3; }
            else continue;
            if (!best.TryGetValue(id, out var current) || rank < current.Rank) best[id] = (rank, text);
        }
        string Pick(int preferred, int fallback) =>
            best.TryGetValue(preferred, out var p) && p.Value.Length > 0 ? p.Value : best.TryGetValue(fallback, out var f) ? f.Value : "";
        return (Pick(16, 1), Pick(17, 2));
    }

    static void Read(SafeFileHandle handle, long offset, byte[] buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(done), offset + done);
            if (read == 0) throw new InvalidDataException();
            done += read;
        }
    }

    static int U16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];
    static long U32(byte[] data, int offset) => ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];
}
